using ManualForge.Core.Rendering;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// A page too large for the pixel limit is read at the highest resolution that fits. It used to be
/// refused, and a refusal failed the whole document: 116 finished pages of a 120-page book were
/// lost to one 36-inch fold-out at 600 dpi (#21).
/// </summary>
public sealed class RasteriserLimitTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "manualforge-raster-" + Guid.NewGuid().ToString("N"));

    public RasteriserLimitTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AnOversizedPageIsReadAtTheHighestResolutionThatFits()
    {
        // A letter page at 300 dpi is 2550 x 3300, 8.4 M pixels. Allow a million.
        var path = TestPdf.Scanned(Path.Combine(_directory, "page.pdf"), pages: 1);
        var rasteriser = new PageRasteriser(new RasterOptions { Dpi = 300, MaxPixels = 1_000_000 });

        using var page = rasteriser.Render(File.ReadAllBytes(path), 0);

        var pixels = (long)page.Width * page.Height;
        Assert.InRange(pixels, 900_000, 1_000_000);
        Assert.Equal(TestPdf.WidthPt / TestPdf.HeightPt, (double)page.Width / page.Height, precision: 2);
    }

    [Fact]
    public void APageWithinTheLimitIsReadAtTheResolutionAskedFor()
    {
        var path = TestPdf.Scanned(Path.Combine(_directory, "page.pdf"), pages: 1);
        var rasteriser = new PageRasteriser(new RasterOptions { Dpi = 150 });

        using var page = rasteriser.Render(File.ReadAllBytes(path), 0);

        // PDFium rounds to the pixel either way.
        Assert.InRange(page.Width, 1274, 1276);
        Assert.InRange(page.Height, 1649, 1651);
    }
}
