using ManualForge.Core.Geometry;
using ManualForge.Core.Ocr;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// A column of short table entries boxed as one tall region is read on its side - <c>NNNNN</c>,
/// <c>mmmmm</c> - and has to be cut back into its rows. The cut is made where the column's own ink
/// has blank rows, which a table's rows always leave and a single large glyph never does.
/// </summary>
public sealed class TallStackTests
{
    private const int Width = 200, Height = 300, Line = 30;

    private static bool[] Page(params RectD[] marks)
    {
        var ink = new bool[Width * Height];
        foreach (var m in marks)
            for (int y = (int)m.Top; y < (int)m.Bottom; y++)
                for (int x = (int)m.Left; x < (int)m.Right; x++)
                    ink[y * Width + x] = true;
        return ink;
    }

    [Fact]
    public void SplittingIsTheDefault()
    {
        // The documented default, pinned: a flag wired backwards once went unnoticed for two
        // whole ingests because nothing asserted which way round it was.
        Assert.True(new OcrEngineOptions().SplitTallStacks);
    }

    [Theory]
    [InlineData(20, 90, true)]     // five rows of A2 prefixes
    [InlineData(20, 75, true)]     // exactly two and a half lines
    [InlineData(20, 60, false)]    // two lines: an ordinary tall word, not a stack
    [InlineData(100, 90, false)]   // wider than tall: a block of text, not a column
    [InlineData(80, 200, false)]   // tall, but a block of a component layout: no column is that wide
    public void AStackIsTallAndNarrow(double width, double height, bool stack)
    {
        Assert.Equal(stack, TallStacks.IsStack(new RectD(0, 0, width, height), Line));
    }

    [Fact]
    public void AColumnIsCutAtTheGapsBetweenItsRows()
    {
        // Five designator prefixes, 20 px tall, one every 35 px; the box is the detector's, a
        // little larger than the ink.
        var marks = Enumerable.Range(0, 5).Select(i => RectD.FromEdges(52, 20 + i * 35, 70, 40 + i * 35)).ToArray();

        var rows = TallStacks.Rows(Page(marks), Width, Height, RectD.FromEdges(48, 15, 74, 200), Line);

        Assert.Equal(marks, rows);
    }

    [Fact]
    public void ARowIsAsWideAsItsOwnInk()
    {
        // A2 on one row, A12 on the next: each row keeps its own width, not the column's.
        var rows = TallStacks.Rows(
            Page(RectD.FromEdges(52, 20, 64, 40), RectD.FromEdges(52, 55, 72, 75)),
            Width, Height, RectD.FromEdges(48, 15, 76, 80), Line);

        Assert.Equal([RectD.FromEdges(52, 20, 64, 40), RectD.FromEdges(52, 55, 72, 75)], rows);
    }

    [Fact]
    public void OneLargeGlyphIsLeftWhole()
    {
        // A big 8 on a drawing is tall and narrow too, but has no gap to cut at.
        var eight = RectD.FromEdges(60, 20, 80, 110);

        Assert.Empty(TallStacks.Rows(Page(eight), Width, Height, eight, Line));
    }

    [Fact]
    public void AWordSetSidewaysIsLeftWhole()
    {
        // FUER written up the side of a drawing: four letters, each turned, three pixels apart.
        // The recogniser reads it correctly on its side; cutting it would make four loose letters.
        var letters = Enumerable.Range(0, 4).Select(i => RectD.FromEdges(52, 20 + i * 21, 70, 38 + i * 21)).ToArray();

        Assert.Empty(TallStacks.Rows(Page(letters), Width, Height, RectD.FromEdges(48, 15, 74, 110), Line));
    }

    [Fact]
    public void AOnePixelBreakInsideAGlyphIsNotAGap()
    {
        // The waist of an 8 can come apart by a pixel at low resolution.
        var marks = new[]
        {
            RectD.FromEdges(52, 20, 70, 29), RectD.FromEdges(52, 30, 70, 40),   // one glyph, split
            RectD.FromEdges(52, 55, 70, 75),
        };

        var rows = TallStacks.Rows(Page(marks), Width, Height, RectD.FromEdges(48, 15, 74, 80), Line);

        Assert.Equal([RectD.FromEdges(52, 20, 70, 40), RectD.FromEdges(52, 55, 70, 75)], rows);
    }

    [Fact]
    public void SpecksAreNotRows()
    {
        // A dot of noise between two rows would otherwise make a third, empty crop.
        var marks = new[]
        {
            RectD.FromEdges(52, 20, 70, 40), RectD.FromEdges(60, 46, 62, 48), RectD.FromEdges(52, 55, 70, 75),
        };

        var rows = TallStacks.Rows(Page(marks), Width, Height, RectD.FromEdges(48, 15, 74, 80), Line);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ABoxOverTheEdgeOfThePageIsClamped()
    {
        var marks = new[] { RectD.FromEdges(190, 250, 200, 270), RectD.FromEdges(190, 280, 200, 300) };

        var rows = TallStacks.Rows(Page(marks), Width, Height, RectD.FromEdges(185, 240, 210, 320), Line);

        Assert.Equal(marks, rows);
    }
}
