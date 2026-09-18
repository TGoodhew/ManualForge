using System.Collections.Concurrent;
using System.Security.Cryptography;
using ManualForge.Core.Indexing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualForge.Core.Auditing;

public sealed record DoctorProgress(
    string Path, int DocumentsDone, int DocumentsTotal, int FlaggedDocuments, long FlaggedPages);

public sealed record DoctorReport(
    int DocumentsAudited,
    int DocumentsUnchanged,
    int DocumentsFailed,
    long PagesExamined,
    long PagesFlagged,
    long PagesWithNoTextLayer,
    long PagesUndecodable,
    long EstimatedRecoverableCharacters,
    IReadOnlyList<DocumentAudit> Audits,
    TimeSpan Elapsed)
{
    public double PagesPerMinute => Elapsed.TotalMinutes <= 0 ? 0 : PagesExamined / Elapsed.TotalMinutes;

    /// <summary>Documents with at least one flagged page, worst first.</summary>
    public IReadOnlyList<DocumentAudit> Ranked =>
        Audits.Where(a => a.FlaggedPageCount > 0).OrderByDescending(a => a.Score).ToArray();

    /// <summary>
    /// Documents enough of which is drawn rather than typeset that searching them will mislead.
    /// This is the list somebody has to act on, and the reason the audit exists.
    /// </summary>
    public IReadOnlyList<DocumentAudit> ToRepair =>
        Ranked.Where(a => a.Verdict == DocumentVerdict.UnderExtracted).ToArray();

    /// <summary>
    /// Documents whose text layer is sound but which have isolated figure pages with lettering in
    /// them. Real findings, and small ones; kept out of the list above so it stays readable.
    /// </summary>
    public IReadOnlyList<DocumentAudit> WithFigures =>
        Ranked.Where(a => a.Verdict == DocumentVerdict.Figures).ToArray();

    /// <summary>
    /// Scanned documents whose existing OCR missed a substantial part of their lettering. A real
    /// gap and a much larger one, kept separate because the remedy differs and because a list of
    /// three hundred scanned service manuals would bury the twelve that are drawn.
    /// </summary>
    public IReadOnlyList<DocumentAudit> WithScanGaps =>
        Ranked.Where(a => a.Verdict == DocumentVerdict.ScannedGaps).ToArray();

    /// <summary>Flagged pages whose missing content is drawn on the page rather than photographed.</summary>
    public long DrawnPagesFlagged => Audits.Sum(a => (long)a.DrawnPageCount);
}

/// <summary>
/// Runs the under-extraction audit over a whole library.
///
/// <para>
/// Nothing is OCR'd and nothing is written to any PDF. The point of shipping this on its own is
/// that the report has standalone value: until it has been run, nobody knows whether the problem
/// is one manual or two hundred, and that number decides whether a repair is a morning's work or a
/// project.
/// </para>
/// </summary>
public sealed class DoctorRunner(ILogger<DoctorRunner>? logger = null)
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public async Task<DoctorReport> RunAsync(
        string root,
        DoctorOptions? options = null,
        string? storePath = null,
        bool force = false,
        IProgress<DoctorProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        options ??= new DoctorOptions();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var files = File.Exists(root)
            ? [Path.GetFullPath(root)]
            : Discover(root, options).ToArray();

        using var store = new DoctorStore(storePath ?? DoctorStore.DefaultPathFor(
            File.Exists(root) ? Path.GetDirectoryName(Path.GetFullPath(root))! : root));

        var detector = new UnderExtractionDetector(options);

        var audits = new ConcurrentBag<DocumentAudit>();
        var done = 0;
        var unchanged = 0;
        var failed = 0;
        long flaggedPages = 0;
        var flaggedDocuments = 0;

        // The store is a single SQLite connection, so writes are funnelled through one lock while
        // the auditing itself — which is where all the time goes — runs in parallel.
        var storeLock = new object();

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.Workers),
                CancellationToken = cancellationToken,
            },
            async (path, token) =>
            {
                DocumentAudit audit;
                try
                {
                    var hash = await HashAsync(path, token).ConfigureAwait(false);

                    if (!force)
                    {
                        string? previous;
                        lock (storeLock)
                            previous = store.ContentHashOf(path);

                        if (previous == hash)
                        {
                            Interlocked.Increment(ref unchanged);
                            Interlocked.Increment(ref done);
                            return;
                        }
                    }

                    audit = detector.Audit(path, hash, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    audit = new DocumentAudit(path, Path.GetFileNameWithoutExtension(path), "", 0, [], ex.Message);
                }

                if (audit.Error is not null)
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning("Could not audit {Path}: {Error}", path, audit.Error);
                }

                audits.Add(audit);

                lock (storeLock)
                    store.Save(audit);

                if (audit.FlaggedPageCount > 0)
                {
                    Interlocked.Increment(ref flaggedDocuments);
                    Interlocked.Add(ref flaggedPages, audit.FlaggedPageCount);

                    _logger.LogInformation(
                        "{Path}: {Flagged} of {Pages} pages under-extracted, about {Chars} characters recoverable",
                        path, audit.FlaggedPageCount, audit.PageCount, audit.EstimatedRecoverableCharacters);
                }

                var completed = Interlocked.Increment(ref done);
                progress?.Report(new DoctorProgress(
                    path, completed, files.Length, flaggedDocuments, Interlocked.Read(ref flaggedPages)));
            }).ConfigureAwait(false);

        stopwatch.Stop();

        var all = audits.ToArray();

        var report = new DoctorReport(
            all.Length - failed,
            unchanged,
            failed,
            all.Sum(a => (long)a.Pages.Count),
            flaggedPages,
            all.Sum(a => (long)a.Pages.Count(p => p.Verdict == PageVerdict.NoTextLayer)),
            all.Sum(a => (long)a.Pages.Count(p => p.Verdict == PageVerdict.Undecodable)),
            all.Sum(a => (long)a.EstimatedRecoverableCharacters),
            all,
            stopwatch.Elapsed);

        _logger.LogInformation(
            "Audited {Documents} document(s), {Pages} pages, in {Seconds:F1}s: " +
            "{FlaggedDocuments} document(s) and {FlaggedPages} page(s) under-extracted",
            report.DocumentsAudited, report.PagesExamined, stopwatch.Elapsed.TotalSeconds,
            report.Ranked.Count, report.PagesFlagged);

        return report;
    }

    public static IEnumerable<string> Discover(string root, DoctorOptions options) =>
        LibraryIndexer.Discover(root, new IndexOptions { ExcludedFolderNames = options.ExcludedFolderNames });

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
