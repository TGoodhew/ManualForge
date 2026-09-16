using System.Diagnostics;
using ManualForge.Core.Classification;
using ManualForge.Core.Pdf;
using ManualForge.Core.State;
using ManualForge.Core.Verification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf.IO;

namespace ManualForge.Core.Pipeline;

public sealed class LibraryOptions
{
    /// <summary>Root of the manual library.</summary>
    public required string Root { get; init; }

    /// <summary>Where untouched originals are kept, mirroring the source tree.</summary>
    public string OriginalsFolderName { get; init; } = "_Originals";

    /// <summary>State database. Defaults to one inside the originals folder.</summary>
    public string? StatePath { get; init; }

    /// <summary>Do everything except move or replace any file.</summary>
    public bool DryRun { get; init; }

    /// <summary>Per-class decisions about what to do.</summary>
    public ClassificationPolicy Policy { get; init; } = new();

    /// <summary>Stop after this many files. 0 means no limit.</summary>
    public int Limit { get; init; }

    /// <summary>Process files in this order. Smallest-first gets useful output soonest.</summary>
    public bool SmallestFirst { get; init; } = true;

    /// <summary>
    /// Allow modifying digitally signed files. Off by default: adding a text layer invalidates the
    /// signature, and flattening removes it outright.
    /// </summary>
    public bool AllowSignedFiles { get; init; }
}

public sealed record FileOutcome(
    string Path,
    TextClass TextClass,
    ClassAction Action,
    FileStatus Status,
    int PageCount,
    int WordsWritten,
    double WorstDeviationPt,
    bool WasFlattened,
    TimeSpan Duration,
    string? Error);

/// <summary>
/// Walks a manual library and brings each file to its target state: classify, flatten if the file
/// refuses modification, OCR if the policy says so, verify, and only then replace the original —
/// which is moved aside, never overwritten.
///
/// The ordering of the final steps is the safety property that matters. Nothing in the library is
/// touched until the new file exists and has been verified to open cleanly, to have the same page
/// count as the source, and to carry a non-empty text layer. Only then is the original moved into
/// the originals tree and the new file put in its place, both as moves on the same volume.
/// </summary>
public sealed class LibraryProcessor(
    SearchablePdfBuilder? builder,
    DocumentClassifier classifier,
    ILogger<LibraryProcessor>? logger = null)
{
    // Null is legitimate: Survey classifies without ever running OCR, and constructing an engine
    // just to look at text layers would download models and occupy the GPU for nothing.
    private readonly SearchablePdfBuilder? _builder = builder;
    private readonly DocumentClassifier _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>
    /// Classifies every PDF under the root and records the result, without changing any file.
    /// This is the pass that produces the summary table to review before committing to a run.
    /// </summary>
    public IReadOnlyList<FileRecord> Survey(
        LibraryOptions options,
        IProgress<DocumentClassification>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var store = OpenStore(options);

        foreach (var path in Discover(options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = store.Register(path);
            if (record.Status is not FileStatus.Discovered && record.PageCount > 0)
            {
                // Already classified and unchanged since; nothing to redo.
                continue;
            }

            var capabilities = PdfInspector.Inspect(path);
            var classification = _classifier.Classify(path);
            var action = capabilities.IsHopeless
                ? ClassAction.Skip
                : options.Policy.ActionFor(classification.Class);

            store.RecordClassification(path, classification, capabilities, action);
            if (action == ClassAction.Skip)
                store.SetStatus(path, FileStatus.Skipped);

            progress?.Report(classification);
        }

        return store.All();
    }

    /// <summary>
    /// Processes everything the policy marks for work. Safe to run repeatedly: finished files are
    /// skipped, interrupted files resume from the page they reached.
    /// </summary>
    public IReadOnlyList<FileOutcome> Run(
        LibraryOptions options,
        IProgress<FileOutcome>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_builder is null)
            throw new InvalidOperationException("Run needs an OCR builder; this processor was created for surveying only.");

        using var store = OpenStore(options);

        var outstanding = store.Outstanding();
        if (options.SmallestFirst)
            outstanding = outstanding.OrderBy(r => r.Fingerprint.SizeBytes).ToList();
        if (options.Limit > 0)
            outstanding = outstanding.Take(options.Limit).ToList();

        var outcomes = new List<FileOutcome>(outstanding.Count);

        foreach (var record in outstanding)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = ProcessOne(store, options, record, cancellationToken);
            outcomes.Add(outcome);
            progress?.Report(outcome);
        }

        return outcomes;
    }

    private FileOutcome ProcessOne(
        JobStore store, LibraryOptions options, FileRecord record, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var path = record.Path;
        var workingDirectory = Path.Combine(Path.GetTempPath(), "ManualForge", Guid.NewGuid().ToString("N"));
        var flattened = false;

        try
        {
            store.SetStatus(path, FileStatus.InProgress);
            Directory.CreateDirectory(workingDirectory);

            var source = path;

            // Step 1: if the file refuses modification, rebuild it into one that does not. The
            // flattened copy is verified against the original's page geometry and image streams
            // before it is used for anything.
            var capabilities = PdfInspector.Inspect(path);
            if (capabilities.IsHopeless)
            {
                store.SetStatus(path, FileStatus.Failed, $"Cannot be opened: {capabilities.Detail}");
                return Outcome(record, FileStatus.Failed, 0, 0, false, stopwatch.Elapsed, capabilities.Detail);
            }

            // A digital signature is a claim about the bytes of the file. Adding a text layer
            // breaks that claim, and flattening drops the signature entirely. Neither is ours to
            // decide silently, so a signed file is refused unless explicitly allowed.
            if (capabilities.HasSignature && !options.AllowSignedFiles)
            {
                const string reason = "The file is digitally signed; modifying it would invalidate the signature.";
                store.SetStatus(path, FileStatus.Skipped, reason);
                return Outcome(record, FileStatus.Skipped, 0, 0, false, stopwatch.Elapsed, reason);
            }

            if (capabilities.NeedsFlattening)
            {
                var flattenedPath = Path.Combine(workingDirectory, "flattened.pdf");
                var flattenResult = new PdfFlattener().Flatten(path, flattenedPath);
                if (!flattenResult.IsVerified)
                {
                    var detail = string.Join(" ", flattenResult.Discrepancies.Take(3));
                    store.SetStatus(path, FileStatus.Failed, "Flatten verification failed: " + detail);
                    return Outcome(record, FileStatus.Failed, 0, 0, true, stopwatch.Elapsed, detail);
                }

                source = flattenedPath;
                flattened = true;
                _logger.LogInformation(
                    "Flattened {Path}: cleared {Blocker}, {Pages} pages, {Ratio:P1} of the original size",
                    path, flattenResult.BlockerCleared, flattenResult.PageCount, flattenResult.SizeRatio);
            }

            // Step 2: if we are replacing an existing text layer, remove it first so that
            // extraction does not return the old and the new interleaved.
            if (record.Action == ClassAction.StripAndRedo)
            {
                var strippedPath = Path.Combine(workingDirectory, "stripped.pdf");
                using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
                {
                    var stripResult = TextLayerStripper.Strip(document);
                    document.Save(strippedPath);
                    _logger.LogInformation(
                        "Stripped {Path}: {Blocks} text blocks from {Pages} pages",
                        path, stripResult.TextBlocksRemoved, stripResult.PagesChanged);
                }
                source = strippedPath;
            }

            // Step 3: OCR into a new file in the working directory. Nothing in the library has
            // been touched at this point.
            var outputPath = Path.Combine(workingDirectory, "searchable.pdf");
            var completed = store.CompletedPages(path);

            var report = _builder!.BuildAsync(
                source,
                outputPath,
                new BuildOptions { Overwrite = true },
                progress: null,
                cancellationToken).GetAwaiter().GetResult();

            foreach (var page in report.Pages)
                store.RecordPage(path, page.PageNumber, PageStatus.Completed, page.WordsWritten, 0, (long)page.OcrTime.TotalMilliseconds);
            _ = completed;

            // Step 4: verify before anything is replaced. Opens cleanly, same page count, and a
            // text layer that is actually there.
            var verifications = _builder!.Verify(outputPath, report.SourcePageCount);
            var worstDeviation = verifications.Count == 0 ? 0 : verifications.Max(v => v.WorstDeviationPt);
            var totalLetters = verifications.Sum(v => v.ExtractedLetters);

            if (totalLetters == 0)
            {
                store.SetStatus(path, FileStatus.Failed, "The output carried no text layer.");
                return Outcome(record, FileStatus.Failed, report.TotalWordsWritten, worstDeviation, flattened, stopwatch.Elapsed,
                    "The output carried no text layer.");
            }

            if (options.DryRun)
            {
                store.SetStatus(path, FileStatus.Classified, null);
                _logger.LogInformation("Dry run: {Path} would gain {Words} words", path, report.TotalWordsWritten);
                return Outcome(record, FileStatus.Classified, report.TotalWordsWritten, worstDeviation, flattened, stopwatch.Elapsed, null);
            }

            // Step 5: move the original aside, then put the new file in its place. Both are moves
            // on the same volume, so each is atomic, and the original exists in exactly one place
            // at every instant.
            var originalDestination = OriginalsPathFor(options, path);
            Directory.CreateDirectory(Path.GetDirectoryName(originalDestination)!);
            File.Move(path, originalDestination, overwrite: false);

            try
            {
                File.Move(outputPath, path, overwrite: false);
            }
            catch (Exception ex)
            {
                // The original is safe in the originals tree; say exactly where, so nothing is lost.
                _logger.LogError(ex,
                    "Moved {Path} to {Original} but could not put the searchable copy in its place. " +
                    "The searchable copy is at {Output}", path, originalDestination, outputPath);
                store.SetStatus(path, FileStatus.Failed,
                    $"Original is at {originalDestination}; searchable copy is at {outputPath}. {ex.Message}");
                throw;
            }

            store.SetPaths(path, path, originalDestination);
            // The file at this path is now the searchable one, so the recorded fingerprint has to
            // describe that rather than the source it replaced. Otherwise restoring the original
            // later matches the stale fingerprint and the file is never reprocessed.
            store.UpdateFingerprint(path);
            store.SetStatus(path, FileStatus.Completed);

            _logger.LogInformation(
                "Completed {Path}: {Words} words, worst deviation {Deviation:F3} pt, original kept at {Original}",
                path, report.TotalWordsWritten, worstDeviation, originalDestination);

            return Outcome(record, FileStatus.Completed, report.TotalWordsWritten, worstDeviation, flattened, stopwatch.Elapsed, null);
        }
        catch (OperationCanceledException)
        {
            // Leave the file InProgress with its finished pages recorded, so the next run resumes.
            throw;
        }
        catch (Exception ex)
        {
            store.SetStatus(path, FileStatus.Failed, ex.Message);
            _logger.LogError(ex, "Failed on {Path}", path);
            return Outcome(record, FileStatus.Failed, 0, 0, flattened, stopwatch.Elapsed, ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workingDirectory))
                    Directory.Delete(workingDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is untidy, not dangerous.
            }
        }
    }

    private static FileOutcome Outcome(
        FileRecord record, FileStatus status, int words, double deviation, bool flattened, TimeSpan duration, string? error)
        => new(record.Path, record.TextClass, record.Action, status, record.PageCount, words, deviation, flattened, duration, error);

    /// <summary>
    /// Where a file's original is kept: one tree under the root, mirroring the source structure.
    /// </summary>
    public static string OriginalsPathFor(LibraryOptions options, string path)
    {
        var root = Path.GetFullPath(options.Root);
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, full);
        return Path.Combine(root, options.OriginalsFolderName, relative);
    }

    public IEnumerable<string> Discover(LibraryOptions options)
    {
        var root = Path.GetFullPath(options.Root);
        var originals = Path.Combine(root, options.OriginalsFolderName);

        return Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories)
            // Never treat an original we set aside as a new input; that would loop forever.
            .Where(f => !f.StartsWith(originals + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }

    public static JobStore OpenStore(LibraryOptions options)
    {
        var path = options.StatePath
            ?? Path.Combine(Path.GetFullPath(options.Root), options.OriginalsFolderName, "manualforge.db");
        return new JobStore(path);
    }
}
