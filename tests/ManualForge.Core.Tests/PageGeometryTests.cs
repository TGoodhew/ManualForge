using ManualForge.Core.Geometry;

namespace ManualForge.Core.Tests;

public class PageGeometryTests
{
    private const double Tolerance = 1e-9;

    /// <summary>US Letter at 300 dpi, no rotation, origin at (0,0).</summary>
    private static PageGeometry Letter300(int rotation = 0, double llx = 0, double lly = 0)
    {
        const double w = 612, h = 792;
        var quarterTurned = rotation is 90 or 270 or -90;
        var pxW = (int)Math.Round((quarterTurned ? h : w) * 300 / 72.0);
        var pxH = (int)Math.Round((quarterTurned ? w : h) * 300 / 72.0);
        return new PageGeometry(llx, lly, w, h, rotation, pxW, pxH);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void MapsTheFourImageCornersOntoTheFourCropBoxCorners(int rotation)
    {
        var g = Letter300(rotation);

        var corners = new[]
        {
            g.ToUserSpace(0, 0),
            g.ToUserSpace(g.PixelWidth, 0),
            g.ToUserSpace(g.PixelWidth, g.PixelHeight),
            g.ToUserSpace(0, g.PixelHeight),
        };

        // Whatever the rotation, the image covers exactly the crop box: the same four points come
        // back, only in a different order.
        var xs = corners.Select(c => Math.Round(c.X, 6)).Order().ToArray();
        var ys = corners.Select(c => Math.Round(c.Y, 6)).Order().ToArray();

        Assert.Equal([0, 0, 612, 612], xs);
        Assert.Equal([0, 0, 792, 792], ys);
    }

    [Fact]
    public void UnrotatedTopLeftPixelIsTheTopLeftOfThePage()
    {
        var g = Letter300();
        var p = g.ToUserSpace(0, 0);

        Assert.Equal(0, p.X, Tolerance);
        Assert.Equal(792, p.Y, Tolerance);   // PDF Y grows upwards, so the image top is the page top
    }

    [Fact]
    public void NonZeroCropOriginShiftsEveryPoint()
    {
        var g = Letter300(rotation: 0, llx: 36, lly: 48);
        var p = g.ToUserSpace(0, 0);

        Assert.Equal(36, p.X, Tolerance);
        Assert.Equal(48 + 792, p.Y, Tolerance);
    }

    [Fact]
    public void RotatedNinetyMapsImageRightwardsOntoPageUpwards()
    {
        var g = Letter300(rotation: 90);

        var left = g.ToUserSpace(0, g.PixelHeight / 2.0);
        var right = g.ToUserSpace(g.PixelWidth, g.PixelHeight / 2.0);

        // Moving right across the displayed image must move up the unrotated page.
        Assert.Equal(left.X, right.X, 1e-6);
        Assert.True(right.Y > left.Y);
    }

    [Fact]
    public void RotatedTwoSeventyMapsImageRightwardsOntoPageDownwards()
    {
        var g = Letter300(rotation: 270);

        var left = g.ToUserSpace(0, g.PixelHeight / 2.0);
        var right = g.ToUserSpace(g.PixelWidth, g.PixelHeight / 2.0);

        Assert.Equal(left.X, right.X, 1e-6);
        Assert.True(right.Y < left.Y);
    }

    [Theory]
    [InlineData(0, 1, 0, 0, 1)]
    [InlineData(90, 0, 1, -1, 0)]
    [InlineData(180, -1, 0, 0, -1)]
    [InlineData(270, 0, -1, 1, 0)]
    public void TextMatrixRotationFollowsThePageRotation(int rotation, double a, double b, double c, double d)
    {
        var g = Letter300(rotation);
        var m = g.TextRotationMatrix;

        Assert.Equal(a, m.A, Tolerance);
        Assert.Equal(b, m.B, Tolerance);
        Assert.Equal(c, m.C, Tolerance);
        Assert.Equal(d, m.D, Tolerance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void WordPlacementAdvancesAlongTheTextDirection(int rotation)
    {
        var g = Letter300(rotation);
        var box = new RectD(300, 400, 500, 60);

        var placement = g.PlaceWordBox(box);

        // The end of the run, found by advancing from the origin along the text direction, must
        // coincide with where the box's bottom-right corner maps to.
        var endX = placement.BaselineOrigin.X + placement.A * placement.WidthPt;
        var endY = placement.BaselineOrigin.Y + placement.B * placement.WidthPt;
        var expectedEnd = g.ToUserSpace(box.Right, box.Bottom);

        Assert.Equal(expectedEnd.X, endX, 1e-6);
        Assert.Equal(expectedEnd.Y, endY, 1e-6);
    }

    [Fact]
    public void FontSizeAndWidthComeBackInPoints()
    {
        var g = Letter300();
        // 60 px tall and 500 px wide at 300 dpi is 14.4 pt by 120 pt.
        var placement = g.PlaceWordBox(new RectD(300, 400, 500, 60));

        Assert.Equal(14.4, placement.FontSizePt, 1e-6);
        Assert.Equal(120.0, placement.WidthPt, 1e-6);
    }

    [Fact]
    public void BaselineOffsetDropsTheBaselineBelowTheBox()
    {
        var g = Letter300();
        var box = new RectD(300, 400, 500, 60);

        var flush = g.PlaceWordBox(box, 0.0);
        var dropped = g.PlaceWordBox(box, 0.25);

        // A quarter of 60 px is 15 px, which is 3.6 pt, downwards on the page.
        Assert.Equal(flush.BaselineOrigin.Y - 3.6, dropped.BaselineOrigin.Y, 1e-6);
    }

    [Theory]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    [InlineData(360, 0)]
    [InlineData(89, 90)]
    public void RotationIsNormalisedToAQuarterTurn(int given, int expected)
        => Assert.Equal(expected, PageGeometry.NormaliseRotation(given));

    [Fact]
    public void EffectiveDpiReportsWhatTheRasterActuallyIs()
    {
        // Ask for a 612x792 pt page rendered 2550 px wide: that is exactly 300 dpi.
        var g = new PageGeometry(0, 0, 612, 792, 0, 2550, 3300);

        Assert.Equal(300, g.EffectiveDpiX, 1e-6);
        Assert.Equal(300, g.EffectiveDpiY, 1e-6);
    }

    [Fact]
    public void RejectsADegenerateCropBox()
        => Assert.Throws<ArgumentException>(() => new PageGeometry(0, 0, 0, 792, 0, 100, 100));

    [Fact]
    public void RejectsADegenerateRaster()
        => Assert.Throws<ArgumentException>(() => new PageGeometry(0, 0, 612, 792, 0, 0, 100));
}
