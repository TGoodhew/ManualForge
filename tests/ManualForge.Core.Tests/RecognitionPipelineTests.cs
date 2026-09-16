using System.Threading.Channels;
using ManualForge.Core.Geometry;
using ManualForge.Core.Ocr;
using ManualForge.Core.Pipeline;
using ManualForge.Core.Rendering;

namespace ManualForge.Core.Tests;

/// <summary>
/// The pipeline fills the page cache ahead of the document that needs it. It never opens a file in
/// the library, never writes a PDF and never moves anything, which is deliberate: the part of this
/// application that can lose a manual stays single-threaded and unchanged.
///
/// So what is worth testing here is that it feeds the cache correctly and completely, that it
/// bounds what it holds in memory, that it tells the assembler about a document only once that
/// document is genuinely ready, and that it can be stopped without leaving anyone waiting.
/// </summary>
public class RecognitionPipelineTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));

    public RecognitionPipelineTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private const string Settings = "test-settings";

    /// <summary>
    /// How long any wait on the pipeline is allowed to take.
    ///
    /// Every test here reads from a channel that the pipeline is responsible for completing, so a
    /// pipeline that fails to complete one does not fail the test, it hangs it. That is worse than
    /// a failure: it wedges the suite, leaves a test host holding the build output, and gives no
    /// clue what broke. Checking these tests against a deliberately broken build is how that was
    /// discovered, so the bound is here rather than in the one test that happened to have it.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private string Scanned(string name, int pages) =>
        TestPdf.Scanned(Path.Combine(_directory, name), pages);

    private static RecognitionPipeline NewPipeline(IOcrEngine engine, IPageOcrCache cache) =>
        new(engine, new PageRasteriser(new RasterOptions { Dpi = 150 }), cache, Settings);

    private static RecognitionPipeline NewPipeline(
        IOcrEngine engine, IPageOcrCache cache, PageRasteriser rasteriser) =>
        new(engine, rasteriser, cache, Settings);

    /// <summary>Counts every page the pipeline asked to have rasterised.</summary>
    private sealed class CountingRasteriser(RasterOptions options) : PageRasteriser(options)
    {
        private int _count;

        public int Rasterised => Volatile.Read(ref _count);

        public override RasterisedPage Render(byte[] pdfBytes, int pageIndex)
        {
            var page = base.Render(pdfBytes, pageIndex);
            Interlocked.Increment(ref _count);
            return page;
        }
    }

    private static RecognitionJob Job(string path, int pages) =>
        new(path, Path.GetFullPath(path), pages);

    [Fact]
    public async Task EveryPageOfEveryDocumentReachesTheCache()
    {
        var a = Scanned("a.pdf", 3);
        var b = Scanned("b.pdf", 2);
        var cache = new MemoryPageOcrCache();
        var engine = new FakeOcrEngine();

        var report = await NewPipeline(engine, cache).RunAsync([Job(a, 3), Job(b, 2)]).WaitAsync(Patience);

        Assert.Equal(5, report.PagesRecognised);
        Assert.Equal(0, report.PagesReused);
        Assert.Equal(0, report.PagesFailed);

        foreach (var (path, pages) in new[] { (a, 3), (b, 2) })
        {
            for (var page = 1; page <= pages; page++)
            {
                var cached = cache.TryGet(Path.GetFullPath(path), page, Settings);
                Assert.NotNull(cached);
                Assert.True(cached.HasDimensions);
                Assert.Equal(3, cached.Words.Count);
            }
        }
    }

    [Fact]
    public async Task PagesAlreadyCachedAreNotRecognisedAgain()
    {
        var path = Scanned("a.pdf", 4);
        var cache = new MemoryPageOcrCache();
        var engine = new FakeOcrEngine();

        // Two pages already done, as an interrupted earlier run would have left them.
        foreach (var page in new[] { 1, 3 })
        {
            cache.Save(Path.GetFullPath(path), page, Settings,
                new CachedPage(1275, 1649, [new RecognisedWord("EARLIER", new RectD(10, 10, 50, 12), 0.9)]));
        }

        var report = await NewPipeline(engine, cache).RunAsync([Job(path, 4)]).WaitAsync(Patience);

        Assert.Equal(2, report.PagesRecognised);
        Assert.Equal(2, report.PagesReused);
        Assert.Equal([2, 4], engine.PagesSeen.Order());

        // And the pages that were already there were left exactly as they were.
        Assert.Equal("EARLIER", cache.TryGet(Path.GetFullPath(path), 1, Settings)!.Words[0].Text);
    }

    [Fact]
    public async Task ADocumentIsAnnouncedOnlyOnceItsLastPageIsCached()
    {
        var a = Scanned("a.pdf", 2);
        var b = Scanned("b.pdf", 3);
        var cache = new MemoryPageOcrCache();

        var completed = Channel.CreateUnbounded<RecognitionJob>();
        var announced = new List<(string Path, int CachedAtTheTime)>();

        var consumer = Task.Run(async () =>
        {
            await foreach (var job in completed.Reader.ReadAllAsync())
            {
                // Every page of this document must already be in the cache by the time it is
                // announced, or the assembler would rebuild a document with pages missing.
                var present = Enumerable.Range(1, job.PageCount)
                    .Count(p => cache.TryGet(job.CacheKey, p, Settings) is not null);
                announced.Add((job.CacheKey, present));
            }
        });

        await NewPipeline(new FakeOcrEngine(), cache)
            .RunAsync([Job(a, 2), Job(b, 3)], new PipelineOptions { Completed = completed.Writer })
            .WaitAsync(Patience);

        await consumer.WaitAsync(Patience);

        Assert.Equal(2, announced.Count);
        Assert.Equal(2, announced.Single(x => x.Path == Path.GetFullPath(a)).CachedAtTheTime);
        Assert.Equal(3, announced.Single(x => x.Path == Path.GetFullPath(b)).CachedAtTheTime);
    }

    [Fact]
    public async Task ADocumentWhoseEveryPageIsCachedIsAnnouncedWithoutAnyWork()
    {
        var path = Scanned("a.pdf", 2);
        var cache = new MemoryPageOcrCache();
        var engine = new FakeOcrEngine();

        for (var page = 1; page <= 2; page++)
        {
            cache.Save(Path.GetFullPath(path), page, Settings,
                new CachedPage(1275, 1649, [new RecognisedWord("DONE", new RectD(10, 10, 50, 12), 0.9)]));
        }

        var completed = Channel.CreateUnbounded<RecognitionJob>();
        await NewPipeline(engine, cache)
            .RunAsync([Job(path, 2)], new PipelineOptions { Completed = completed.Writer })
            .WaitAsync(Patience);

        Assert.Equal(0, engine.PagesRecognised);
        Assert.Single(await completed.Reader.ReadAllAsync().ToListAsync().WaitAsync(Patience));
    }

    [Fact]
    public async Task RecognitionRunsAsManyPagesAtOnceAsItIsTold()
    {
        var path = Scanned("a.pdf", 8);
        var engine = new FakeOcrEngine { OnPage = _ => Task.Delay(40) };

        await NewPipeline(engine, new MemoryPageOcrCache())
            .RunAsync([Job(path, 8)], new PipelineOptions { GpuConcurrency = 3, RasterWorkers = 2 })
            .WaitAsync(Patience);

        // The whole point of the stage: more than one page in flight at a time. Exactly three is
        // not guaranteed on a loaded machine, but more than one is, given eight pages and a delay
        // far longer than the scheduling.
        Assert.InRange(engine.PeakConcurrency, 2, 3);
    }

    [Fact]
    public async Task OnePageAtATimeMeansOnePageAtATime()
    {
        var path = Scanned("a.pdf", 6);
        var engine = new FakeOcrEngine { OnPage = _ => Task.Delay(20) };

        await NewPipeline(engine, new MemoryPageOcrCache())
            .RunAsync([Job(path, 6)], new PipelineOptions { GpuConcurrency = 1, RasterWorkers = 2 })
            .WaitAsync(Patience);

        // Past what VRAM holds, throughput does not degrade, it collapses. So a request for one
        // has to mean one.
        Assert.Equal(1, engine.PeakConcurrency);
    }

    [Fact]
    public async Task TheRasteriserCannotRunAheadOfTheGpuWithoutBound()
    {
        // A 300 dpi page is several megabytes decoded. An unbounded queue in front of the slowest
        // stage would let a fast rasteriser fill the heap with bitmaps the GPU will not reach for
        // minutes, so the queue depth is what keeps memory flat on a 639-page manual.
        //
        // This asserts the lead directly - pages rasterised minus pages recognised - because the
        // first version of it inferred the bound from how far recognition had got, which an
        // unbounded queue does not change at all. It passed against exactly the defect it was
        // written to catch.
        const int pages = 24;
        const int queueDepth = 2;
        const int rasterWorkers = 4;

        var path = Scanned("a.pdf", pages);
        var rasteriser = new CountingRasteriser(new RasterOptions { Dpi = 150 });

        var worstLead = 0;
        var engine = new FakeOcrEngine
        {
            OnPage = _ =>
            {
                // Sampled while a page is held in recognition, which is when the rasteriser has
                // had every chance to run ahead.
                var lead = rasteriser.Rasterised - _recognised;
                worstLead = Math.Max(worstLead, lead);
                Interlocked.Increment(ref _recognised);
                return Task.Delay(25);
            },
        };

        var report = await NewPipeline(engine, new MemoryPageOcrCache(), rasteriser)
            .RunAsync([Job(path, pages)], new PipelineOptions
            {
                RasterWorkers = rasterWorkers,
                GpuConcurrency = 1,
                RasterQueueDepth = queueDepth,
            })
            .WaitAsync(Patience);

        Assert.Equal(pages, report.PagesRecognised);

        // At most the queue itself, plus one page held by each worker waiting to hand it over,
        // plus the one being recognised. Anything beyond that means the bound is not holding.
        Assert.InRange(worstLead, 1, queueDepth + rasterWorkers + 1);
        Assert.True(worstLead < pages, "the rasteriser ran ahead of the GPU without limit");
    }

    private int _recognised;

    [Fact]
    public async Task CancellingStopsTheRunAndLetsTheConsumerFinish()
    {
        var path = Scanned("a.pdf", 20);
        var engine = new FakeOcrEngine { OnPage = _ => Task.Delay(50) };

        using var cancellation = new CancellationTokenSource();
        var completed = Channel.CreateUnbounded<RecognitionJob>();

        // A reader left waiting on a channel nobody will ever complete is a deadlock, and a run
        // that is cancelled must not become one.
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in completed.Reader.ReadAllAsync()) { }
        });

        var run = NewPipeline(engine, new MemoryPageOcrCache())
            .RunAsync([Job(path, 20)], new PipelineOptions { Completed = completed.Writer }, cancellation.Token);

        await Task.Delay(100);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Patience));
        await consumer.WaitAsync(Patience);
    }

    [Fact]
    public async Task APageThatWillNotRecogniseIsCountedAndDoesNotHoldBackItsDocument()
    {
        var path = Scanned("a.pdf", 4);
        var cache = new MemoryPageOcrCache();
        var engine = new FakeOcrEngine
        {
            BeforePage = page =>
            {
                if (page == 2)
                    throw new InvalidOperationException("the card fell over");
            },
        };

        var completed = Channel.CreateUnbounded<RecognitionJob>();
        var report = await NewPipeline(engine, cache)
            .RunAsync([Job(path, 4)], new PipelineOptions { Completed = completed.Writer })
            .WaitAsync(Patience);

        Assert.Equal(3, report.PagesRecognised);
        Assert.Equal(1, report.PagesFailed);

        // The document is still handed on. The page it could not do is simply not cached, and the
        // assembler will rasterise and recognise it itself - which is where a failure belongs,
        // because that is the code that records it against the file.
        Assert.Single(await completed.Reader.ReadAllAsync().ToListAsync().WaitAsync(Patience));
        Assert.Null(cache.TryGet(Path.GetFullPath(path), 2, Settings));
        Assert.NotNull(cache.TryGet(Path.GetFullPath(path), 3, Settings));
    }

    [Fact]
    public async Task APageTheEngineSizesDifferentlyIsLeftForTheBuilder()
    {
        var path = Scanned("a.pdf", 2);
        var cache = new MemoryPageOcrCache();

        var report = await NewPipeline(new FakeOcrEngine { MisreportPixelWidth = 999 }, cache)
            .RunAsync([Job(path, 2)]).WaitAsync(Patience);

        // Boxes from a differently sized raster would be wrong everywhere and undetectably so.
        Assert.Equal(0, report.PagesRecognised);
        Assert.Equal(2, report.PagesFailed);
        Assert.Null(cache.TryGet(Path.GetFullPath(path), 1, Settings));
    }

    [Fact]
    public async Task ConcurrencyDoesNotChangeWhatIsRecognised()
    {
        var path = Scanned("a.pdf", 6);

        var serial = new MemoryPageOcrCache();
        await NewPipeline(new FakeOcrEngine(), serial)
            .RunAsync([Job(path, 6)], new PipelineOptions { GpuConcurrency = 1, RasterWorkers = 1 })
            .WaitAsync(Patience);

        var parallel = new MemoryPageOcrCache();
        await NewPipeline(new FakeOcrEngine(), parallel)
            .RunAsync([Job(path, 6)], new PipelineOptions { GpuConcurrency = 3, RasterWorkers = 4 })
            .WaitAsync(Patience);

        for (var page = 1; page <= 6; page++)
        {
            var a = serial.TryGet(Path.GetFullPath(path), page, Settings);
            var b = parallel.TryGet(Path.GetFullPath(path), page, Settings);
            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.Equal(a.PixelWidth, b.PixelWidth);
            Assert.Equal(a.PixelHeight, b.PixelHeight);
            Assert.Equal(a.Words.Select(w => w.Text), b.Words.Select(w => w.Text));
            Assert.Equal(a.Words.Select(w => w.BoxPx), b.Words.Select(w => w.BoxPx));
        }
    }
}

internal static class ChannelReaderExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
            items.Add(item);
        return items;
    }
}
