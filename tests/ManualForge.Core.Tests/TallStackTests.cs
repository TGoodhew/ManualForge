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

    [Theory]
    [InlineData(5, 5, 18, true)]    // a parts-list column, every row beside a part number
    [InlineData(3, 4, 18, true)]    // NNNOI: one row short in a column of four
    [InlineData(2, 3, 18, false)]   // short column, one row off: not enough to go on
    [InlineData(2, 4, 12, false)]   // -C3- set sideways on a drawing: two rows off
    [InlineData(1, 2, 54, false)]   // IWI CABLE: half off, rows taller than a line
    [InlineData(2, 2, 36, false)]   // every row on a text row, but each taller than a line
    [InlineData(2, 2, 33, true)]    // a hair over the line, as a tight table's rows can be
    public void ATablesRowsLineUpWithItsText(int onTextRows, int rows, double medianRow, bool table)
    {
        Assert.Equal(table, TallStacks.BelongsToTable(onTextRows, rows, medianRow, Line));
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
    public void ATableRuleInsideTheBoxDoesNotJoinTheRows()
    {
        // Page 60 of the table book (#50): the detector's box for a column of A2s takes in the
        // table's vertical rule beside it. The rule is inked on every pixel row, so the column read
        // as one run with no gap, and stayed NNNNN. Its rows are the same with the rule as without.
        var marks = Enumerable.Range(0, 5).Select(i => RectD.FromEdges(52, 20 + i * 35, 70, 40 + i * 35)).ToArray();
        var rule = RectD.FromEdges(46, 0, 49, Height);

        var rows = TallStacks.Rows(Page([rule, .. marks]), Width, Height, RectD.FromEdges(45, 15, 74, 200), Line);

        Assert.Equal(marks, rows);
    }

    [Fact]
    public void ARulesRaggedEdgeIsLeftOutWithIt()
    {
        // As scanned on page 60: the rule's last pixel column is inked only partway down - 85%, then
        // 29% - and that part on its own still bridged the gaps between the rows.
        var marks = Enumerable.Range(0, 5).Select(i => RectD.FromEdges(55, 20 + i * 35, 70, 40 + i * 35)).ToArray();
        var rule = RectD.FromEdges(44, 0, 49, Height);
        var ragged = RectD.FromEdges(49, 30, 50, 150);

        var rows = TallStacks.Rows(Page([rule, ragged, .. marks]), Width, Height, RectD.FromEdges(43, 15, 74, 200), Line);

        Assert.Equal(marks, rows);
    }

    [Fact]
    public void ARuleOnBothSidesIsLeftOutToo()
    {
        var marks = Enumerable.Range(0, 3).Select(i => RectD.FromEdges(52, 20 + i * 35, 70, 40 + i * 35)).ToArray();

        var rows = TallStacks.Rows(
            Page([RectD.FromEdges(46, 0, 48, Height), RectD.FromEdges(73, 0, 75, Height), .. marks]),
            Width, Height, RectD.FromEdges(45, 15, 76, 130), Line);

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
    public void WordsInARowSitOnTheirOwnInk()
    {
        // A2 at 52-64 and R45 at 70-90. The recogniser's centres come from collapsed boxes - one
        // off to the right of A2 - but they still tell which ink belongs to which word.
        var row = RectD.FromEdges(52, 20, 90, 40);
        var ink = Page(RectD.FromEdges(52, 20, 64, 40), RectD.FromEdges(70, 20, 90, 40));

        var boxes = TallStacks.WordBoxes(ink, Width, row, [63.5, 81]);

        Assert.Equal([RectD.FromEdges(52, 20, 64, 40), RectD.FromEdges(70, 20, 90, 40)], boxes);
    }

    [Fact]
    public void AWordWithNoInkInItsShareKeepsTheShare()
    {
        var row = RectD.FromEdges(52, 20, 90, 40);

        var boxes = TallStacks.WordBoxes(Page(RectD.FromEdges(52, 20, 64, 40)), Width, row, [58, 80]);

        Assert.Equal(RectD.FromEdges(52, 20, 64, 40), boxes[0]);
        // The cut falls in the middle of the blank run between the centres, 64 to 80.
        Assert.Equal(RectD.FromEdges(72.5, 20, 90, 40), boxes[1]);
    }

    [Fact]
    public void ABoxOverTheEdgeOfThePageIsClamped()
    {
        var marks = new[] { RectD.FromEdges(190, 250, 200, 270), RectD.FromEdges(190, 280, 200, 300) };

        var rows = TallStacks.Rows(Page(marks), Width, Height, RectD.FromEdges(185, 240, 210, 320), Line);

        Assert.Equal(marks, rows);
    }
}
