using ManualForge.Core.Auditing;
using ManualForge.Core.Geometry;
using SkiaSharp;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// Where a table's row line crosses its column line it cuts the rule into short segments, and a
/// segment of rule between two closely spaced rows is the size, aspect and fill of a character —
/// it passes every shape test the blob filter has. It was the commonest false positive in this
/// corpus: 40 of the 141 flagged pages of one calibration guide were ruled test-record forms whose
/// every word extracts perfectly.
///
/// <para>
/// Position is what separates them, so these tests are about position. They work on a bitmap
/// rather than a PDF because the discriminator is a fact about pixels, and building a PDF that
/// rasterises to exactly the arrangement in question would be testing PDFium as well.
/// </para>
/// </summary>
public sealed class RuleSegmentTests
{
    // A4 at the 150 dpi audit render.
    private const int Width = 1240;
    private const int Height = 1754;

    private static readonly PageGeometry Geometry = new(0, 0, 595, 842, 0, Width, Height);

    /// <summary>A white page with solid dark blocks on it, in image pixels.</summary>
    private static SKBitmap PageWith(IEnumerable<(int X, int Y, int W, int H)> blocks)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        using var paint = new SKPaint { Color = SKColors.Black };
        foreach (var (x, y, w, h) in blocks)
            canvas.DrawRect(x, y, w, h, paint);

        canvas.Flush();
        return bitmap;
    }

    private static int BlobsIn(IEnumerable<(int X, int Y, int W, int H)> blocks, DoctorOptions? options = null)
    {
        using var bitmap = PageWith(blocks);
        return InkAnalyser.Analyse(bitmap, Geometry, [], options ?? new DoctorOptions()).GlyphLikeBlobs;
    }

    /// <summary>A column rule broken into pieces by the row lines crossing it.</summary>
    private static IEnumerable<(int X, int Y, int W, int H)> RuleFragments(int x, int count = 20) =>
        Enumerable.Range(0, count).Select(i => (x, 200 + i * 60, 3, 8));

    [Fact]
    public void AColumnOfIdenticalHairlineFragmentsIsARuleAndNotLettering()
    {
        // Twenty pieces, each 3 px wide at 150 dpi - about 1.4 pt - all at one x. Every shape
        // threshold passes them: tall enough, narrow enough, solid, aspect near one.
        Assert.Equal(0, BlobsIn(RuleFragments(x: 300)));
    }

    [Fact]
    public void SeveralRulesAreStillSeveralRules()
    {
        var table = RuleFragments(x: 300).Concat(RuleFragments(x: 600)).Concat(RuleFragments(x: 900));

        Assert.Equal(0, BlobsIn(table));
    }

    [Fact]
    public void BlocksOfDifferingWidthDownOneColumnAreNot()
    {
        // A left-aligned column of text shares an x, which is why alignment alone is not enough to
        // convict. Glyphs differ in width, and that is the difference being relied on here.
        var text = Enumerable.Range(0, 20).Select(i => (300, 200 + i * 60, 5 + i % 4, 8));

        Assert.Equal(20, BlobsIn(text));
    }

    [Fact]
    public void AWiderColumnOfIdenticalBlocksIsNotThinEnoughToBeARule()
    {
        // 8 px is nearly 4 pt, far wider than any rule in this corpus and an ordinary width for a
        // glyph's ink. Alignment alone must not take these.
        var blocks = Enumerable.Range(0, 20).Select(i => (300, 200 + i * 60, 8, 10));

        Assert.Equal(20, BlobsIn(blocks));
    }

    [Fact]
    public void TwoAlignedFragmentsAreACoincidenceRatherThanATable()
    {
        // Three, because a table has at least that many rows before it looks like a table, and two
        // aligned letters happen all the time.
        Assert.Equal(2, BlobsIn(RuleFragments(x: 300, count: 2)));
    }

    [Fact]
    public void TheTestCanBeSwitchedOff()
    {
        // Switching it off restores the count exactly, which is also the check that these blobs
        // were being counted before the rule and are not failing some other threshold.
        var options = new DoctorOptions { RuleSegmentRun = 0 };

        Assert.Equal(20, BlobsIn(RuleFragments(x: 300), options));
    }
}
