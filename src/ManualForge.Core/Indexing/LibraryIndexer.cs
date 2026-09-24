using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;

namespace ManualForge.Core.Indexing;

public sealed class IndexOptions
{
    /// <summary>Where the index lives. Defaults to one beside the job state.</summary>
    public string? IndexPath { get; init; }

    /// <summary>Folders excluded from indexing, matching the processor's own exclusions.</summary>
    public IReadOnlyList<string> ExcludedFolderNames { get; init; } = ["BASELINE", "_Originals", "_GroundTruth"];

    /// <summary>Re-index every document even if its content has not changed.</summary>
    public bool Force { get; init; }

    /// <summary>
    /// Also write the plain-text sidecar next to the index, one file per document.
    ///
    /// The index is what answers questions; the sidecars are what let something else — a grep, a
    /// different tool, a future me — work over the same text without this application.
    /// </summary>
    public string? SidecarDirectory { get; init; }

    /// <summary>
    /// The audit database holding text the repair recovered from under-extracted pages. Null takes
    /// the one beside the index; a path that does not exist simply means nothing to merge.
    ///
    /// <para>
    /// This is how recovered text reaches search. It is merged here, at index time, rather than
    /// written back into the PDFs, because a page that already carries a text layer must never be
    /// given a second one: an extractor sorts the two together by position and returns them
    /// interleaved character by character, leaving the document less searchable than it was.
    /// </para>
    /// </summary>
    public string? DoctorStorePath { get; init; }

    /// <summary>Ignore recovered text even when there is some. For proving what the merge changed.</summary>
    public bool WithoutRepairs { get; init; }

    /// <summary>
    /// How many documents to extract text from at once.
    ///
    /// <para>
    /// Extraction is the whole cost of indexing and it is per-document work that shares nothing, so
    /// it parallelises almost perfectly. Writing does not: one SQLite connection, one thread, in the
    /// order results arrive. That asymmetry is the design — workers extract, a single reader writes.
    /// </para>
    /// <para>
    /// The default is deliberately not the core count. These are large scanned documents and each
    /// worker holds one in memory while it reads it, so the useful degree of parallelism is bounded
    /// by memory and by the disk the library sits on rather than by cores.
    /// </para>
    /// </summary>
    public int Workers { get; init; } = Math.Clamp(Environment.ProcessorCount / 3, 1, 8);
}

public sealed record IndexProgress(string Path, int DocumentsDone, int DocumentsTotal, int PagesIndexed);

public sealed record IndexReport(
    int DocumentsIndexed,
    int DocumentsUnchanged,
    int DocumentsFailed,
    int DocumentsWithoutText,
    long PagesIndexed,
    TimeSpan Elapsed)
{
    public double PagesPerMinute => Elapsed.TotalMinutes <= 0 ? 0 : PagesIndexed / Elapsed.TotalMinutes;

    /// <summary>Documents into which text recovered by the repair was merged.</summary>
    public int DocumentsWithRecoveredText { get; init; }

    /// <summary>Pages that now carry some recognised text alongside the PDF's own.</summary>
    public long PagesWithRecoveredText { get; init; }
}

/// <summary>
/// Walks a library and puts every page of every document into the search index.
///
/// <para>
/// This reads the finished PDFs rather than anything the OCR pipeline produced, and that is
/// deliberate. Most of a library is never OCR'd by this application at all — three quarters of the
/// corpus it was built against already had a good text layer and was correctly left alone — so an
/// index fed from recognition results would cover only the quarter that needed work. Reading the
/// files means the index describes the library rather than the run.
/// </para>
/// </summary>
public sealed class LibraryIndexer(ILogger<LibraryIndexer>? logger = null)
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public async Task<IndexReport> IndexAsync(
        string root,
        IndexOptions? options = null,
        IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        options ??= new IndexOptions();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var indexPath = options.IndexPath ?? DefaultIndexPath(root);

        using var index = new SearchIndex(indexPath);

        var doctorPath = options.DoctorStorePath ?? Auditing.DoctorStore.DefaultPathFor(root);
        using var doctor = !options.WithoutRepairs && File.Exists(doctorPath)
            ? new Auditing.DoctorStore(doctorPath, readOnly: true)
            : null;

        if (doctor is not null)
            _logger.LogInformation("Merging recovered text from {Path}", doctorPath);

        var files = Discover(root, options).ToArray();
        var indexed = 0;
        var unchanged = 0;
        var failed = 0;
        var withoutText = 0;
        var repairedDocuments = 0;
        long repairedPages = 0;
        long pages = 0;

        // Deciding what to skip touches both databases, so it happens here, on one thread, before
        // any worker starts. It is cheap next to extraction — a hash of the file and two lookups —
        // and doing it first means the parallel half never touches SQLite at all.
        var pending = new List<PendingDocument>();

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var hash = await HashAsync(path, cancellationToken).ConfigureAwait(false);

                var repairs = doctor?.Repairs(path, hash) ?? new Dictionary<int, Auditing.PageRepair>();
                var supplement = SupplementHash(repairs);

                // Both hashes, because a repair changes what should be indexed without changing the
                // file. Skipping on the content hash alone would mean recovered text never arrived.
                if (!options.Force
                    && index.ContentHashOf(path) == hash
                    && index.SupplementHashOf(path) == supplement)
                {
                    unchanged++;
                    progress?.Report(new IndexProgress(
                        path, indexed + unchanged + failed, files.Length, (int)pages));
                    continue;
                }

                pending.Add(new PendingDocument(path, hash, supplement, repairs));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning(ex, "Could not read {Path}", path);
            }
        }

        var workers = Math.Max(1, options.Workers);
        _logger.LogInformation(
            "{Pending} document(s) to extract, {Unchanged} unchanged, {Workers} worker(s)",
            pending.Count, unchanged, workers);

        // Bounded, so that fast workers cannot run ahead of the writer and hold a queue of whole
        // extracted documents in memory. One slot each plus one in hand is enough to keep the
        // writer fed without letting the backlog grow.
        var channel = Channel.CreateBounded<ExtractedDocument>(
            new BoundedChannelOptions(workers + 1) { SingleReader = true });

        var writing = Task.Run(async () =>
        {
            await foreach (var done in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                index.AddDocument(
                    done.Path, Title(root, done.Path), done.ContentHash,
                    done.Text, done.Provenance, done.SupplementHash);

                indexed++;
                pages += done.PagesWithText;

                if (done.HasNoText)
                {
                    // An image-only document that was never OCR'd has nothing to index. Recording
                    // it anyway means a later run can tell "indexed, no text" from "never seen",
                    // and the count is worth reporting rather than hiding.
                    withoutText++;
                }

                if (done.Provenance.Count > 0)
                {
                    repairedDocuments++;
                    repairedPages += done.Provenance.Count;
                }

                if (options.SidecarDirectory is not null)
                    await WriteSidecarAsync(
                        root, done.Path, done.Text, options.SidecarDirectory, cancellationToken)
                        .ConfigureAwait(false);

                progress?.Report(new IndexProgress(
                    done.Path, indexed + unchanged + failed, files.Length, (int)pages));
            }
        }, cancellationToken);

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
            async (document, token) =>
            {
                try
                {
                    var embedded = ExtractPages(document.Path, token);
                    var (text, provenance) = Merge(embedded, document.Repairs);

                    await channel.Writer.WriteAsync(
                        new ExtractedDocument(
                            document.Path, document.ContentHash, document.SupplementHash,
                            text, provenance,
                            text.Count(t => !t.IsEmpty),
                            embedded.Count == 0 || embedded.All(t => t.IsEmpty)),
                        token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One document that will not open must not cost the other five hundred, and a
                    // worker that throws must not take the writer down with it.
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning(ex, "Could not index {Path}", document.Path);
                }
            }).ConfigureAwait(false);

        channel.Writer.Complete();
        await writing.ConfigureAwait(false);

        index.Optimise();
        stopwatch.Stop();

        var report = new IndexReport(indexed, unchanged, failed, withoutText, pages, stopwatch.Elapsed)
        {
            DocumentsWithRecoveredText = repairedDocuments,
            PagesWithRecoveredText = repairedPages,
        };

        _logger.LogInformation(
            "Indexed {Indexed} document(s) ({Unchanged} unchanged, {Failed} failed, {Empty} with no text), " +
            "{Pages} pages in {Seconds:F1}s",
            report.DocumentsIndexed, report.DocumentsUnchanged, report.DocumentsFailed,
            report.DocumentsWithoutText, report.PagesIndexed, stopwatch.Elapsed.TotalSeconds);

        return report;
    }

    /// <summary>A document that needs extracting, and everything about it the databases already know.</summary>
    private sealed record PendingDocument(
        string Path,
        string ContentHash,
        string SupplementHash,
        IReadOnlyDictionary<int, Auditing.PageRepair> Repairs);

    /// <summary>
    /// One extracted document on its way from a worker to the writer. Everything the write needs
    /// travels with it, so the writer never has to go back to a file or to a database.
    /// </summary>
    private sealed record ExtractedDocument(
        string Path,
        string ContentHash,
        string SupplementHash,
        IReadOnlyList<IndexedPageText> Text,
        IReadOnlyDictionary<int, PageProvenance> Provenance,
        int PagesWithText,
        bool HasNoText);

    /// <summary>
    /// Folds recovered text into the extracted text, page by page, and says where each page's text
    /// came from.
    ///
    /// <para>
    /// The embedded text comes first and is passed through untouched — byte for byte what it was
    /// before any of this existed, which is the property the whole design turns on. Recovered text
    /// is appended after it, separated by a blank line, and is only ever present for pages the
    /// audit flagged and the repair reached.
    /// </para>
    /// </summary>
    public static (IReadOnlyList<IndexedPageText> Text, IReadOnlyDictionary<int, PageProvenance> Provenance)
        Merge(IReadOnlyList<IndexedPageText> embedded, IReadOnlyDictionary<int, Auditing.PageRepair> repairs)
    {
        if (repairs.Count == 0)
            return (embedded, new Dictionary<int, PageProvenance>());

        var merged = new List<IndexedPageText>(embedded.Count);
        var provenance = new Dictionary<int, PageProvenance>();

        for (var i = 0; i < embedded.Count; i++)
        {
            var pageNumber = i + 1;

            if (!repairs.TryGetValue(pageNumber, out var repair) || repair.OcrText.Length == 0)
            {
                merged.Add(embedded[i]);
                continue;
            }

            // The recovered text goes through the same de-hyphenation as everything else, so a
            // label broken across two lines of a diagram is searchable as one word.
            var recovered = Dehyphenator.Prepare(repair.OcrText);

            var text = embedded[i].IsEmpty
                ? recovered.Text
                : embedded[i].Text + "\n\n" + recovered.Text;

            var alternates = string.Join(
                ' ',
                new[] { embedded[i].Alternates, recovered.Alternates }.Where(a => a.Length > 0));

            merged.Add(new IndexedPageText(text, alternates));

            provenance[pageNumber] = new PageProvenance(
                embedded[i].Text.Length, recovered.Text.Length, repair.MeanConfidence, recovered.Text);
        }

        return (merged, provenance);
    }

    /// <summary>
    /// Identifies the recovered text folded into one document, so that a repair which leaves the
    /// file untouched still forces a re-index.
    /// </summary>
    public static string SupplementHash(IReadOnlyDictionary<int, Auditing.PageRepair> repairs)
    {
        if (repairs.Count == 0)
            return string.Empty;

        var builder = new System.Text.StringBuilder();
        foreach (var pageNumber in repairs.Keys.Order())
        {
            var repair = repairs[pageNumber];
            builder.Append(pageNumber).Append(':')
                   .Append(repair.OcrText.Length).Append(':')
                   .Append(repair.RepairedUtc.ToUnixTimeSeconds()).Append(';');
        }

        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }

    /// <summary>Extracts and prepares every page of one document.</summary>
    public static IReadOnlyList<IndexedPageText> ExtractPages(
        string path, CancellationToken cancellationToken = default)
    {
        using var document = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true });

        var pages = new List<IndexedPageText>(document.NumberOfPages);
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // GetWords, not Text: many PDFs position words rather than emitting spaces, and
                // the raw string comes back as one unbroken run. Lines are rebuilt from the words'
                // own vertical positions so the de-hyphenator has line ends to work with.
                pages.Add(Dehyphenator.Prepare(Lines(page)));
            }
            catch (Exception)
            {
                // One page that will not parse must not cost the other five hundred.
                pages.Add(new IndexedPageText(string.Empty, string.Empty));
            }
        }

        return pages;
    }

    /// <summary>
    /// Rebuilds a page's lines from word positions. The de-hyphenator needs to know where lines
    /// end, and that is a fact about geometry rather than about the characters.
    /// </summary>
    private static string Lines(UglyToad.PdfPig.Content.Page page)
    {
        var words = page.GetWords().ToArray();
        if (words.Length == 0)
            return string.Empty;

        var lines = new List<(double Top, List<string> Words)>();

        foreach (var word in words)
        {
            var top = word.BoundingBox.Top;
            var line = lines.FirstOrDefault(l => Math.Abs(l.Top - top) < 3.0);

            if (line.Words is null)
            {
                line = (top, []);
                lines.Add(line);
            }

            line.Words.Add(word.Text);
        }

        return string.Join('\n', lines
            .OrderByDescending(l => l.Top)
            .Select(l => string.Join(' ', l.Words)));
    }

    private static async Task WriteSidecarAsync(
        string root, string path, IReadOnlyList<IndexedPageText> pages,
        string directory, CancellationToken cancellationToken)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        var target = Path.Combine(directory, Path.ChangeExtension(relative, ".txt"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var text = string.Join(
            Environment.NewLine,
            pages.Select((p, i) => $"--- page {i + 1} ---{Environment.NewLine}{p.Text}"));

        await File.WriteAllTextAsync(target, text, new System.Text.UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>A name a person would recognise: the file name without its extension.</summary>
    private static string Title(string root, string path) => Path.GetFileNameWithoutExtension(path);

    public static string DefaultIndexPath(string root) =>
        Path.Combine(Path.GetFullPath(root), "_Originals", "manualforge-index.db");

    public static IEnumerable<string> Discover(string root, IndexOptions options)
    {
        var full = Path.GetFullPath(root);
        var excluded = options.ExcludedFolderNames
            .Select(name => Path.Combine(full, name) + Path.DirectorySeparatorChar)
            .ToArray();

        return Directory.EnumerateFiles(full, "*.pdf", SearchOption.AllDirectories)
            .Where(f => !excluded.Any(e => f.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
