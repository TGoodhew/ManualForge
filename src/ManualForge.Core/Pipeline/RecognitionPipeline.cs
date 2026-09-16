using System.Diagnostics;
using System.Threading.Channels;
using ManualForge.Core.Ocr;
using ManualForge.Core.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualForge.Core.Pipeline;

public sealed class PipelineOptions
{
    /// <summary>
    /// CPU workers turning pages into bitmaps. PDFium rasterises concurrently and safely —
    /// measured at 1.87x on two threads and 3.16x on four, byte-identical to serial output — so
    /// this is a real knob rather than a nominal one.
    /// </summary>
    public int RasterWorkers { get; init; } = 2;

    /// <summary>
    /// Pages in flight on the GPU. See <see cref="GpuMemoryProbe"/> for why this is chosen from
    /// free VRAM rather than fixed: past what the card holds, throughput does not degrade, it
    /// collapses.
    /// </summary>
    public int GpuConcurrency { get; init; } = 1;

    /// <summary>
    /// How many rasterised pages may wait for the GPU. Each is a PNG of a 300 dpi page, so this
    /// bounds memory as well as scheduling: the point of a bounded channel here is that a fast
    /// rasteriser cannot run ahead and fill the heap with bitmaps the GPU will not reach for
    /// minutes.
    /// </summary>
    public int RasterQueueDepth { get; init; } = 8;

    /// <summary>Pages recognised so far, reported as they complete.</summary>
    public IProgress<PipelinePageProgress>? Progress { get; init; }

    /// <summary>
    /// Documents are written here as their last page is cached, so assembly can start on one
    /// document while the GPU is already recognising the next.
    ///
    /// Without this the pipeline would have to finish every document before any file was produced,
    /// which on a thirty-hour run means nothing to show for the first twenty-nine of them. The
    /// writer is completed when the run ends, so a consumer can simply read to the end.
    /// </summary>
    public ChannelWriter<RecognitionJob>? Completed { get; init; }
}

/// <summary>One page finished, for progress reporting.</summary>
public sealed record PipelinePageProgress(
    string DocumentPath,
    int PageNumber,
    int PageCount,
    int WordsRecognised,
    bool FromCache,
    TimeSpan RasterTime,
    TimeSpan OcrTime);

public sealed record PipelineReport(
    int DocumentsSeen,
    int PagesRecognised,
    int PagesReused,
    int PagesFailed,
    TimeSpan Elapsed)
{
    public int PagesTotal => PagesRecognised + PagesReused;

    public double PagesPerMinute => Elapsed.TotalMinutes <= 0 ? 0 : PagesTotal / Elapsed.TotalMinutes;
}

/// <summary>A document the pipeline should recognise, and the identity to cache it under.</summary>
/// <param name="CacheKey">
/// The document's stable path in the library. Not <paramref name="SourcePath"/>, which for a
/// flattened or stripped document is a temporary file with a different name on every attempt.
/// </param>
public sealed record RecognitionJob(string SourcePath, string CacheKey, int PageCount);

/// <summary>
/// Recognises pages ahead of the document that needs them, so the GPU is never waiting on a CPU.
///
/// <para>
/// The structure is three stages joined by bounded channels — documents in, pages rasterised, pages
/// recognised — and the thing that makes it simple is what it does <i>not</i> do. It does not
/// assemble PDFs, verify them, or move anything. It fills the page cache. The document path that
/// follows is exactly the one phase 2 already had and already tests: rebuild from cache, verify,
/// replace the original only once the replacement is proven. Parallelism was not allowed anywhere
/// near the part that can lose a file.
/// </para>
///
/// <para>
/// Why this is worth having at all, measured rather than assumed. Recognition is 95% of the work
/// and the GPU averages 40% utilisation while doing it, because every page alternates CPU phases
/// (decode, deskew, crop, CTC) with GPU phases (detection, recognition). Overlapping pages fills
/// those gaps. Overlapping rasterisation, which is what the architecture was originally drawn to
/// do, is worth 5% on its own — rasterising is 55 ms against 1,034 ms of recognition.
/// </para>
/// </summary>
public sealed class RecognitionPipeline(
    IOcrEngine engine,
    PageRasteriser rasteriser,
    IPageOcrCache cache,
    string settingsFingerprint,
    ILogger<RecognitionPipeline>? logger = null)
{
    private readonly IOcrEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly PageRasteriser _rasteriser = rasteriser ?? throw new ArgumentNullException(nameof(rasteriser));
    private readonly IPageOcrCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly string _settings = settingsFingerprint
        ?? throw new ArgumentNullException(nameof(settingsFingerprint));
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>A page on its way from the rasteriser to the GPU.</summary>
    private sealed record RasterisedPageJob(
        RecognitionJob Document, int PageNumber, byte[] Png, int PixelWidth, int PixelHeight, TimeSpan RasterTime);

    public async Task<PipelineReport> RunAsync(
        IReadOnlyList<RecognitionJob> documents,
        PipelineOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        options ??= new PipelineOptions();

        var stopwatch = Stopwatch.StartNew();
        var recognised = 0;
        var reused = 0;
        var failed = 0;

        // Pages still to account for, per document. A document is announced as done when this
        // reaches zero, whether its pages were recognised, reused or failed - a page that could
        // not be recognised is not a reason to hold the document back, because the path that
        // follows will rasterise and retry it itself and is where a failure belongs.
        var outstanding = documents.ToDictionary(d => d, d => d.PageCount);
        var outstandingLock = new object();

        void AccountForPage(RecognitionJob document)
        {
            if (options.Completed is null)
                return;

            bool done;
            lock (outstandingLock)
                done = --outstanding[document] <= 0;

            if (done)
                options.Completed.TryWrite(document);
        }

        // Bounded, and deliberately shallow. A 300 dpi page is a few hundred kilobytes as PNG and
        // several megabytes decoded, so an unbounded queue in front of the slowest stage is a
        // memory leak with good intentions.
        var rasterQueue = Channel.CreateBounded<RasterisedPageJob>(new BoundedChannelOptions(options.RasterQueueDepth)
        {
            SingleWriter = options.RasterWorkers == 1,
            SingleReader = options.GpuConcurrency == 1,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // One work item per page, so a short document cannot leave rasteriser threads idle while a
        // long one is still being read.
        var pageQueue = Channel.CreateBounded<(RecognitionJob Document, int PageNumber)>(
            new BoundedChannelOptions(Math.Max(options.RasterWorkers * 4, 16))
            {
                SingleWriter = true,
                SingleReader = options.RasterWorkers == 1,
                FullMode = BoundedChannelFullMode.Wait,
            });

        using var failure = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = failure.Token;

        // Stage 1: enumerate the pages that are not already cached.
        var enumerate = Task.Run(async () =>
        {
            try
            {
                foreach (var document in documents)
                {
                    for (var pageNumber = 1; pageNumber <= document.PageCount; pageNumber++)
                    {
                        token.ThrowIfCancellationRequested();

                        // Asked here as well as in the builder, so a document that is already fully
                        // recognised costs nothing at all rather than a rasterised page each.
                        if (_cache.TryGet(document.CacheKey, pageNumber, _settings) is { HasDimensions: true })
                        {
                            Interlocked.Increment(ref reused);
                            AccountForPage(document);
                            continue;
                        }

                        await pageQueue.Writer.WriteAsync((document, pageNumber), token).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                pageQueue.Writer.TryComplete();
            }
        }, token);

        // Stage 2: rasterise. Each worker holds one document's bytes at a time; pages arrive in
        // document order, so the same bytes are reused across a run of pages rather than re-read.
        var rasterWorkers = Enumerable.Range(0, Math.Max(1, options.RasterWorkers)).Select(_ => Task.Run(async () =>
        {
            string? loadedPath = null;
            byte[]? loadedBytes = null;

            await foreach (var (document, pageNumber) in pageQueue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (!string.Equals(loadedPath, document.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    loadedBytes = await File.ReadAllBytesAsync(document.SourcePath, token).ConfigureAwait(false);
                    loadedPath = document.SourcePath;
                }

                var watch = Stopwatch.StartNew();
                try
                {
                    using var raster = _rasteriser.Render(loadedBytes!, pageNumber - 1);
                    var png = raster.EncodePng();
                    watch.Stop();

                    await rasterQueue.Writer
                        .WriteAsync(new RasterisedPageJob(document, pageNumber, png, raster.Width, raster.Height, watch.Elapsed), token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A page that will not rasterise is left uncached. The document path that
                    // follows will meet the same failure and record it against the file, which is
                    // where a failure belongs; stopping the whole run for one page would not be.
                    Interlocked.Increment(ref failed);
                    AccountForPage(document);
                    _logger.LogWarning(ex, "Could not rasterise page {Page} of {Path}", pageNumber, document.SourcePath);
                }
            }
        }, token)).ToArray();

        var closeRasterQueue = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(rasterWorkers).ConfigureAwait(false);
            }
            finally
            {
                rasterQueue.Writer.TryComplete();
            }
        }, CancellationToken.None);

        // Stage 3: recognise. This is the only stage whose width is chosen from hardware.
        var gpuWorkers = Enumerable.Range(0, Math.Max(1, options.GpuConcurrency)).Select(_ => Task.Run(async () =>
        {
            await foreach (var job in rasterQueue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    var page = await _engine.RecognisePageAsync(job.Png, job.PageNumber, token).ConfigureAwait(false);
                    watch.Stop();

                    if (page.PixelWidth != 0 && page.PixelWidth != job.PixelWidth)
                    {
                        // Boxes from a differently sized raster would be wrong everywhere and
                        // undetectably so. Leave the page uncached and let the builder rasterise
                        // and recognise it itself, where the warning is already handled.
                        _logger.LogWarning(
                            "Page {Page} of {Path}: the engine reports {EngineWidth}x{EngineHeight} against a " +
                            "{RasterWidth}x{RasterHeight} raster; leaving it for the builder",
                            job.PageNumber, job.Document.SourcePath,
                            page.PixelWidth, page.PixelHeight, job.PixelWidth, job.PixelHeight);
                        Interlocked.Increment(ref failed);
                        AccountForPage(job.Document);
                        continue;
                    }

                    var words = page.Words.Where(w => w.IsUsable).ToArray();
                    _cache.Save(job.Document.CacheKey, job.PageNumber, _settings,
                        new CachedPage(job.PixelWidth, job.PixelHeight, words));

                    Interlocked.Increment(ref recognised);
                    AccountForPage(job.Document);

                    options.Progress?.Report(new PipelinePageProgress(
                        job.Document.CacheKey, job.PageNumber, job.Document.PageCount,
                        words.Length, FromCache: false, job.RasterTime, watch.Elapsed));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref failed);
                    AccountForPage(job.Document);
                    _logger.LogWarning(ex, "Could not recognise page {Page} of {Path}", job.PageNumber, job.Document.SourcePath);
                }
            }
        }, token)).ToArray();

        try
        {
            await Task.WhenAll([enumerate, .. gpuWorkers]).ConfigureAwait(false);
            await closeRasterQueue.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stop the other stages before surfacing the first failure, so nothing is left running
            // behind a cancelled run.
            await failure.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll([enumerate, closeRasterQueue, .. gpuWorkers]).ConfigureAwait(false); }
            catch (Exception) { /* the original exception is the one worth reporting */ }
            throw;
        }
        finally
        {
            // However the run ended, the consumer must be able to stop reading. A cancelled run
            // that left a reader waiting on a channel nobody will ever complete is a deadlock.
            options.Completed?.TryComplete();
        }

        stopwatch.Stop();

        var report = new PipelineReport(documents.Count, recognised, reused, failed, stopwatch.Elapsed);

        _logger.LogInformation(
            "Recognised {Recognised} pages ({Reused} already cached, {Failed} failed) from {Documents} " +
            "document(s) in {Seconds:F1}s at {Rate:F1} pages/min",
            report.PagesRecognised, report.PagesReused, report.PagesFailed,
            report.DocumentsSeen, stopwatch.Elapsed.TotalSeconds, report.PagesPerMinute);

        return report;
    }
}
