using System.Diagnostics;
using ManualForge.Core.Classification;
using ManualForge.Core.Ocr;
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

    /// <summary>
    /// Top-level folders excluded from discovery, beyond the originals tree.
    ///
    /// BASELINE holds reference copies kept deliberately as they are — another engine's output,
    /// retained so its quality can be measured against ours. Processing those would overwrite the
    /// very thing they exist to compare against, so they are never candidates for work.
    /// </summary>
    public IReadOnlyList<string> ExcludedFolderNames { get; init; } = ["BASELINE"];

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
    /// Refuse to modify digitally signed files. Off by default, so signed files are processed like
    /// any other.
    ///
    /// The default is deliberate for this library: the signatures on these manuals come from
    /// whoever scanned or redistributed them decades ago, not from anything worth relying on, and
    /// refusing silently left work undone that had been asked for. Every signature that is
    /// invalidated is reported, so it is never a surprise.
    /// </summary>
    public bool RefuseSignedFiles { get; init; }

    /// <summary>
    /// Reconsider files that were previously skipped. Needed whenever the reason for skipping has
    /// changed — a widened policy, or a skip that turned out to be wrong.
    /// </summary>
    public bool RetrySkipped { get; init; }

    /// <summary>
    /// Recognise each distinct document once, copying the result to any byte-identical twins.
    /// On a library assembled over years this is not a marginal saving.
    /// </summary>
    public bool Deduplicate { get; init; } = true;
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
    string? Error,
    bool SignatureInvalidated = false);

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
    ILogger<LibraryProcessor>? logger = null,
    IPageOcrCache? pageCache = null)
{
    private readonly IPageOcrCache _pageCache = pageCache ?? NullPageOcrCache.Instance;

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

        if (options.RetrySkipped)
        {
            var reset = store.ResetSkipped();
            _logger.LogInformation("Reconsidering {Count} previously skipped file(s)", reset);
        }

        var discovered = Discover(options).ToArray();

        // Anything recorded but no longer on disk is marked before classifying, so totals describe
        // the library as it is rather than as it once was.
        var missing = store.MarkMissing(discovered.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (missing > 0)
            _logger.LogInformation("{Count} recorded file(s) are no longer on disk", missing);

        foreach (var path in discovered)
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

        if (options.Deduplicate)
            DeduplicateOutstanding(store, cancellationToken);

        return store.All();
    }

    /// <summary>
    /// Deletes the records of files that are no longer on disk, and returns what was removed.
    /// Survey marks them on every run; this is the separate, explicit step that forgets them.
    /// </summary>
    public IReadOnlyList<string> TrimMissing(LibraryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var store = OpenStore(options);

        var trimmed = store.TrimMissing();
        if (trimmed.Count > 0)
            _logger.LogInformation("Forgot {Count} record(s) for files that are no longer on disk", trimmed.Count);

        return trimmed;
    }

    /// <summary>
    /// Hashes the files that are about to be worked on and points duplicates at a single primary.
    /// Only files marked for work are hashed: sparing effort on files nobody is touching would
    /// cost more to discover than it saves.
    /// </summary>
    private void DeduplicateOutstanding(JobStore store, CancellationToken cancellationToken)
    {
        var candidates = store.All()
            .Where(r => r.Action is ClassAction.Ocr or ClassAction.StripAndRedo)
            .Where(r => r.Status is not FileStatus.Completed)
            .ToArray();

        if (candidates.Length == 0)
            return;

        var report = new ContentDeduplicator(_logger).Apply(store, candidates, cancellationToken);

        if (report.RedundantFiles > 0)
        {
            _logger.LogInformation(
                "Deduplication: {Distinct} distinct documents among {Examined} files, sparing {Pages} pages of recognition",
                report.DistinctDocuments, report.FilesExamined, report.RedundantPages);
        }
    }

    /// <summary>
    /// Processes everything the policy marks for work. Safe to run repeatedly: finished files are
    /// skipped, and an interrupted run continues from the first document that did not finish —
    /// reusing the pages that document had already recognised, so an interruption costs the page in
    /// flight rather than the document.
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

        // A copy cannot be taken until its primary has been produced, so process every primary
        // first and the copies afterwards.
        outstanding = outstanding
            .OrderBy(r => r.Action == ClassAction.CopyFromDuplicate ? 1 : 0)
            .ToList();
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
        var signatureInvalidated = false;

        try
        {
            store.SetStatus(path, FileStatus.InProgress);
            Directory.CreateDirectory(workingDirectory);

            // A byte-identical twin of a file we have already recognised: take its finished
            // result instead of spending the GPU on the same pages again. The copy still gets its
            // own original preserved, so the originals tree stays a complete mirror.
            if (record.Action == ClassAction.CopyFromDuplicate)
                return CopyFromPrimary(store, options, record, stopwatch);

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

            // A digital signature is a claim about the bytes of the file, and adding a text layer
            // breaks it. Processing anyway is the default, but never silently: every signature
            // invalidated is reported and logged.
            if (capabilities.HasSignature)
            {
                if (options.RefuseSignedFiles)
                {
                    const string reason = "The file is digitally signed and --refuse-signed was given.";
                    store.SetStatus(path, FileStatus.Skipped, reason);
                    return Outcome(record, FileStatus.Skipped, 0, 0, false, stopwatch.Elapsed, reason);
                }

                signatureInvalidated = true;
                _logger.LogWarning(
                    "{Path} is digitally signed; adding a text layer invalidates that signature. " +
                    "The untouched original is kept in the originals tree.", path);
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

            var report = _builder!.BuildAsync(
                source,
                outputPath,
                // Keyed on the library path, not on `source`, which for a flattened or stripped
                // document is a temporary file with a new name on every attempt.
                new BuildOptions { Overwrite = true, CacheKey = path },
                progress: null,
                cancellationToken).GetAwaiter().GetResult();

            foreach (var page in report.Pages)
                store.RecordPage(path, page.PageNumber, PageStatus.Completed, page.WordsWritten, 0, (long)page.OcrTime.TotalMilliseconds);

            if (report.ResumedPages > 0)
            {
                _logger.LogInformation(
                    "Resumed {Resumed} of {Total} pages of {Path} from an earlier attempt",
                    report.ResumedPages, report.Pages.Count, path);
            }

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

            // Adding a text layer must change nothing else about the document. Compare the output's
            // structure against the input's: page count, per-page geometry and rotation, and the
            // dimensions and compression of every image.
            //
            // This check exists because its absence let a real defect through. Reading page.CropBox
            // to work out the geometry silently wrote /CropBox [0 0 0 0] into every page that had
            // none, and it reached 148 files before anything noticed — the alignment measurement
            // only caught it on rotated pages, where the error happened not to cancel.
            var structuralChanges = PdfFlattener.Compare(
                PdfFlattener.Fingerprint(source), PdfFlattener.Fingerprint(outputPath));

            if (structuralChanges.Count > 0)
            {
                var detail = "The output is not structurally identical to the source: "
                    + string.Join(" ", structuralChanges.Take(3));
                store.SetStatus(path, FileStatus.Failed, detail);
                return Outcome(record, FileStatus.Failed, report.TotalWordsWritten, worstDeviation, flattened,
                    stopwatch.Elapsed, detail);
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

            // The document is finished, so its cached recognition has done its job. Releasing it
            // here keeps the cache to the documents actually in flight rather than the whole
            // library.
            _pageCache.Clear(path);

            _logger.LogInformation(
                "Completed {Path}: {Words} words, worst deviation {Deviation:F3} pt, original kept at {Original}",
                path, report.TotalWordsWritten, worstDeviation, originalDestination);

            return Outcome(record, FileStatus.Completed, report.TotalWordsWritten, worstDeviation, flattened,
                stopwatch.Elapsed, null, signatureInvalidated);
        }
        catch (OperationCanceledException)
        {
            // Leave the document InProgress with its recognised pages cached. The next run rebuilds
            // the document but reuses that recognition, so an interruption costs the page in flight
            // rather than the document.
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

    /// <summary>
    /// Gives a duplicate the searchable file already produced for its primary.
    /// </summary>
    private FileOutcome CopyFromPrimary(
        JobStore store, LibraryOptions options, FileRecord record, System.Diagnostics.Stopwatch stopwatch)
    {
        var path = record.Path;

        if (record.DuplicateOf is null)
        {
            const string reason = "Marked as a duplicate but with no primary recorded.";
            store.SetStatus(path, FileStatus.Failed, reason);
            return Outcome(record, FileStatus.Failed, 0, 0, false, stopwatch.Elapsed, reason);
        }

        var primary = store.Find(record.DuplicateOf);
        if (primary is null || primary.Status != FileStatus.Completed || !File.Exists(primary.Path))
        {
            // The primary failed, or was never reached. Leave this one outstanding rather than
            // failing it: another run may yet produce the primary.
            var reason = $"Waiting for its primary, {Path.GetFileName(record.DuplicateOf)}, to be produced.";
            store.SetStatus(path, FileStatus.Classified, reason);
            return Outcome(record, FileStatus.Classified, 0, 0, false, stopwatch.Elapsed, reason);
        }

        if (options.DryRun)
        {
            _logger.LogInformation("Dry run: {Path} would be copied from {Primary}", path, primary.Path);
            return Outcome(record, FileStatus.Classified, 0, 0, false, stopwatch.Elapsed, null);
        }

        // Same ordering as the OCR path: preserve this file's original first, then put the
        // searchable version in its place.
        var originalDestination = OriginalsPathFor(options, path);
        Directory.CreateDirectory(Path.GetDirectoryName(originalDestination)!);
        File.Move(path, originalDestination, overwrite: false);

        try
        {
            File.Copy(primary.Path, path, overwrite: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Moved {Path} to {Original} but could not copy the searchable version from {Primary}",
                path, originalDestination, primary.Path);
            store.SetStatus(path, FileStatus.Failed,
                $"Original is at {originalDestination}. {ex.Message}");
            throw;
        }

        store.SetPaths(path, path, originalDestination);
        store.UpdateFingerprint(path);
        store.SetStatus(path, FileStatus.Completed);

        _logger.LogInformation(
            "Copied {Primary} to {Path}: identical content, so it needed no recognition of its own",
            primary.Path, path);

        return Outcome(record, FileStatus.Completed, 0, 0, false, stopwatch.Elapsed, null);
    }

    private static FileOutcome Outcome(
        FileRecord record, FileStatus status, int words, double deviation, bool flattened, TimeSpan duration,
        string? error, bool signatureInvalidated = false)
        => new(record.Path, record.TextClass, record.Action, status, record.PageCount, words, deviation, flattened,
            duration, error, signatureInvalidated);

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

        var excluded = new List<string>
        {
            // Never treat an original we set aside as a new input; that would loop forever.
            Path.Combine(root, options.OriginalsFolderName) + Path.DirectorySeparatorChar,
        };

        foreach (var name in options.ExcludedFolderNames)
            excluded.Add(Path.Combine(root, name) + Path.DirectorySeparatorChar);

        return Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories)
            .Where(f => !excluded.Any(e => f.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }

    public static JobStore OpenStore(LibraryOptions options, bool readOnly = false)
        => new(StatePathFor(options), readOnly);

    /// <summary>Where a library's state database lives.</summary>
    public static string StatePathFor(LibraryOptions options)
        => options.StatePath
           ?? Path.Combine(Path.GetFullPath(options.Root), options.OriginalsFolderName, "manualforge.db");
}
