using ManualForge.Core.Geometry;
using ManualForge.Core.Ocr;
using ManualForge.Core.Pipeline;
using ManualForge.Core.Rendering;
using ManualForge.Core.Text;

namespace ManualForge.Core.Tests;

/// <summary>A cache that lives only as long as the test, and counts what was asked of it.</summary>
internal sealed class MemoryPageOcrCache : IPageOcrCache
{
    private readonly Dictionary<(string Path, int Page, string Settings), CachedPage> _entries = [];

    public int Saves { get; private set; }

    public CachedPage? TryGet(string documentPath, int pageNumber, string settingsFingerprint)
        => _entries.GetValueOrDefault((Path.GetFullPath(documentPath), pageNumber, settingsFingerprint));

    public void Save(string documentPath, int pageNumber, string settingsFingerprint, CachedPage page)
    {
        Saves++;
        _entries[(Path.GetFullPath(documentPath), pageNumber, settingsFingerprint)] = page;
    }

    public void Clear(string documentPath)
    {
        foreach (var key in _entries.Keys.Where(k => k.Path == Path.GetFullPath(documentPath)).ToArray())
            _entries.Remove(key);
    }
}

/// <summary>
/// A page whose recognition is already cached should cost nothing at all — not recognition, and
/// not the rasterisation that used to happen anyway just to ask how big the page was.
///
/// Measured over 40,000 pages of the real library, rasterising is 55 ms a page against 1,034 ms of
/// recognition. Small, but a resumed run paid it for a bitmap it then threw away, and on a
/// 639-page manual that is half a minute of nothing.
/// </summary>
public class CachedPageReuseTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));

    public CachedPageReuseTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private (SearchablePdfBuilder Builder, FakeOcrEngine Engine, MemoryPageOcrCache Cache) NewBuilder()
    {
        var engine = new FakeOcrEngine();
        var cache = new MemoryPageOcrCache();
        var builder = new SearchablePdfBuilder(
            engine,
            new PageRasteriser(new RasterOptions { Dpi = 150 }),
            new TextLayerWriter(),
            pageCache: cache);
        return (builder, engine, cache);
    }

    [Fact]
    public async Task ACachedPageIsNotRasterisedAgain()
    {
        var source = TestPdf.Scanned(Path.Combine(_directory, "scan.pdf"), pages: 1);
        var output = Path.Combine(_directory, "out.pdf");
        var (builder, engine, cache) = NewBuilder();

        // Dimensions nothing would ever really produce, so the report can only be echoing the
        // cache. A rasterised letter page at 150 dpi is 1275x1650.
        cache.Save(Path.GetFullPath(source), 1, builder.SettingsFingerprint,
            new CachedPage(1234, 5678, [new RecognisedWord("HEWLETT", new RectD(100, 200, 260, 31), 0.98)]));

        var report = await builder.BuildAsync(source, output, new BuildOptions { Overwrite = true });

        var page = Assert.Single(report.Pages);
        Assert.Equal(1234, page.PixelWidth);
        Assert.Equal(5678, page.PixelHeight);
        Assert.Equal(0, engine.PagesRecognised);
        Assert.Equal(1, report.ResumedPages);
    }

    [Fact]
    public async Task APageRecognisedNowRemembersTheSizeItWasRecognisedAt()
    {
        var source = TestPdf.Scanned(Path.Combine(_directory, "scan.pdf"), pages: 1);
        var output = Path.Combine(_directory, "out.pdf");
        var (builder, _, cache) = NewBuilder();

        var report = await builder.BuildAsync(source, output, new BuildOptions { Overwrite = true });
        var page = Assert.Single(report.Pages);

        var cached = cache.TryGet(Path.GetFullPath(source), 1, builder.SettingsFingerprint);
        Assert.NotNull(cached);
        Assert.True(cached.HasDimensions);
        Assert.Equal(page.PixelWidth, cached.PixelWidth);
        Assert.Equal(page.PixelHeight, cached.PixelHeight);

        // 612x792 pt at 150 dpi. PDFium rounds the height down, which is exactly why the raster's
        // own dimensions are what gets cached rather than a figure computed from the page box.
        Assert.Equal(1275, cached.PixelWidth);
        Assert.Equal(1649, cached.PixelHeight);
    }

    [Fact]
    public async Task AnEntryCachedBeforeSizesWereStoredStillWorks()
    {
        // Rows written by the version that cached words alone report zero for both dimensions.
        // They must keep working — discarding them would throw away the recognition they hold —
        // so the page falls back to being rasterised and the size comes from the raster.
        var source = TestPdf.Scanned(Path.Combine(_directory, "scan.pdf"), pages: 1);
        var output = Path.Combine(_directory, "out.pdf");
        var (builder, engine, cache) = NewBuilder();

        cache.Save(Path.GetFullPath(source), 1, builder.SettingsFingerprint,
            new CachedPage(0, 0, [new RecognisedWord("HEWLETT", new RectD(100, 200, 260, 31), 0.98)]));

        var report = await builder.BuildAsync(source, output, new BuildOptions { Overwrite = true });

        var page = Assert.Single(report.Pages);
        Assert.Equal(1275, page.PixelWidth);
        Assert.Equal(1649, page.PixelHeight);

        // The words were still reused: no recognition happened.
        Assert.Equal(0, engine.PagesRecognised);
        Assert.Equal(1, report.ResumedPages);
    }
}
