using ManualForge.Core.Auditing;
using ManualForge.Core.Geometry;
using SkiaSharp;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// Lettering printed through a block of ink rather than with it: white on black, which a detector
/// looking for marks is structurally blind to. It was the only miss in the recall sample re-drawn
/// after the render gate was fixed.
///
/// <para>
/// Every test here is about the same tension. Looking inside a block of ink finds the button
/// labels; looking inside the wrong block — a page border, a photograph — finds the whole page and
/// flags it for nothing.
/// </para>
/// </summary>
public sealed class ReverseVideoTests
{
    private const int Width = 1240;
    private const int Height = 1754;

    private static readonly PageGeometry Geometry = new(0, 0, 595, 842, 0, Width, Height);

    private static SKBitmap Page(Action<SKCanvas, SKPaint, SKPaint> draw)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        using var ink = new SKPaint { Color = SKColors.Black };
        using var paper = new SKPaint { Color = SKColors.White };
        draw(canvas, ink, paper);

        canvas.Flush();
        return bitmap;
    }

    /// <summary>
    /// With the looking turned on, which is not the default: it finds real lettering and real
    /// photographs alike, and the measurement that would justify defaulting it on has not been made.
    /// </summary>
    private static int BlobsIn(SKBitmap bitmap, DoctorOptions? options = null) =>
        InkAnalyser.Analyse(
            bitmap, Geometry, [], options ?? new DoctorOptions { ReverseVideo = true }).GlyphLikeBlobs;

    /// <summary>A filled badge with light letter-shaped holes punched out of it.</summary>
    private static void Badge(SKCanvas canvas, SKPaint ink, SKPaint paper, int x, int y, int letters = 5)
    {
        canvas.DrawRect(x, y, 26 * letters + 20, 44, ink);
        for (var i = 0; i < letters; i++)
            canvas.DrawRect(x + 14 + i * 26, y + 12, 14, 22, paper);
    }

    [Fact]
    public void WhiteLettersOnABlackBadgeAreFound()
    {
        using var bitmap = Page((canvas, ink, paper) => Badge(canvas, ink, paper, 300, 400));

        Assert.Equal(5, BlobsIn(bitmap));
    }

    [Fact]
    public void AndAreNotFoundByDefault()
    {
        // The paired assertion, and the current default: without this looking, the same page yields
        // nothing at all. That is the blind spot - and also why turning it on is not free, since
        // what it finds on a real page is lettering and photographs in roughly equal measure.
        using var bitmap = Page((canvas, ink, paper) => Badge(canvas, ink, paper, 300, 400));

        Assert.Equal(0, BlobsIn(bitmap, new DoctorOptions()));
    }

    [Fact]
    public void ASolidBlockWithNothingInItIsNotLettering()
    {
        using var bitmap = Page((canvas, ink, _) => canvas.DrawRect(300, 400, 400, 120, ink));

        Assert.Equal(0, BlobsIn(bitmap));
    }

    [Fact]
    public void APageBorderDoesNotTurnTheWholePageInsideOut()
    {
        // The failure this would have if the fill test were dropped. A border is a cluster far too
        // big to be a letter, and its "inside" is every word on the page - so the page would go
        // from a handful of blobs to hundreds, and every bordered page in the corpus would flag.
        using var bordered = Page((canvas, ink, _) =>
        {
            using var outline = new SKPaint { Color = SKColors.Black, IsStroke = true, StrokeWidth = 3 };
            canvas.DrawRect(80, 80, Width - 160, Height - 160, outline);

            for (var i = 0; i < 12; i++)
                canvas.DrawRect(200 + i * 30, 500, 16, 22, ink);
        });

        using var unbordered = Page((canvas, ink, _) =>
        {
            for (var i = 0; i < 12; i++)
                canvas.DrawRect(200 + i * 30, 500, 16, 22, ink);
        });

        Assert.Equal(BlobsIn(unbordered), BlobsIn(bordered));
    }

    [Fact]
    public void APhotographIsNotReadAsAWallOfLettering()
    {
        // Light detail inside a large dark area is a photograph, not text. This is the change most
        // likely to flag a thousand pages for nothing, so the page-share cap is a test rather than
        // a hope.
        using var bitmap = Page((canvas, ink, paper) =>
        {
            canvas.DrawRect(100, 100, 1000, 900, ink);

            var random = new Random(1);
            for (var i = 0; i < 400; i++)
                canvas.DrawRect(120 + random.Next(950), 120 + random.Next(850), 12, 18, paper);
        });

        Assert.Equal(0, BlobsIn(bitmap));
    }

    [Fact]
    public void ABadgeWhoseLabelTheTextLayerAlreadyHoldsIsLeftAlone()
    {
        // Free, and worth pinning: the walk only ever traverses ink that no extracted glyph
        // accounts for, so a badge covered by the text layer is never offered for inspection.
        using var bitmap = Page((canvas, ink, paper) => Badge(canvas, ink, paper, 300, 400));

        // The badge, in display points, Y up from the bottom of the page.
        var badge = new RectD(
            300 * Geometry.PointsPerPixelX,
            842 - (444 * Geometry.PointsPerPixelY),
            150 * Geometry.PointsPerPixelX,
            44 * Geometry.PointsPerPixelY);

        Assert.Equal(
            0,
            InkAnalyser.Analyse(
                bitmap, Geometry, [badge], new DoctorOptions { ReverseVideo = true }).GlyphLikeBlobs);
    }
}
