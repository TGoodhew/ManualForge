using ManualForge.Core.Auditing;
using ManualForge.Core.Geometry;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// Reading order, which is what stops two notes standing side by side from being read as one.
/// </summary>
public sealed class ReadingOrderTests
{
    private sealed record Line(string Text, RectD Box);

    private static string Order(params Line[] lines) =>
        string.Join(" | ", ReadingOrder.Sort(lines, l => l.Box).Select(l => l.Text));

    /// <summary>A line 10 high at (x, y), as wide as its text is long.</summary>
    private static Line At(double x, double y, string text) =>
        new(text, new RectD(x, y, text.Length * 5, 10));

    [Fact]
    public void TwoNotesSideBySideAreNotInterleaved()
    {
        // Page 2-60 of the 54845A Programmer's Guide, which is the page this exists for. Detection
        // order runs across the page, so the recogniser hands these over interleaved and the result
        // reads as though AUX were the command restricted to the 54810/20.
        var interleaved = Order(
            At(0, 0, "The AUX"),
            At(300, 0, "The EXTernal"),
            At(0, 14, "command is only"),
            At(300, 14, "command is only"),
            At(0, 28, "available on the"),
            At(300, 28, "available on the"),
            At(0, 42, "54815/25/35/45/46."),
            At(300, 42, "54810/20."));

        Assert.Equal(
            "The AUX | command is only | available on the | 54815/25/35/45/46. | " +
            "The EXTernal | command is only | available on the | 54810/20.",
            interleaved);
    }

    [Fact]
    public void OneColumnIsLeftInReadingOrder()
    {
        Assert.Equal(
            "first | second | third",
            Order(At(0, 0, "first"), At(0, 14, "second"), At(0, 28, "third")));
    }

    [Fact]
    public void ATableIsReadAlongItsRowsRatherThanDownItsColumns()
    {
        // The risk in cutting columns apart: a table's cells are close together, and reading it
        // column by column would be worse than the problem being fixed. The gutter threshold is
        // deliberately wide enough that ordinary tabulation does not trigger one.
        Assert.Equal(
            "R1 | 1k | R2 | 750",
            Order(
                At(0, 0, "R1"), At(30, 0, "1k"),
                At(0, 14, "R2"), At(30, 14, "750")));
    }

    [Fact]
    public void AHeadingAboveTwoColumnsComesFirst()
    {
        Assert.Equal(
            "CHANnel Commands | left one | left two | right one | right two",
            Order(
                At(0, 0, "CHANnel Commands"),
                At(0, 40, "left one"),
                At(0, 54, "left two"),
                At(400, 40, "right one"),
                At(400, 54, "right two")));
    }

    [Fact]
    public void NothingToSortIsNotAnError()
    {
        Assert.Empty(ReadingOrder.Sort(Array.Empty<Line>(), l => l.Box));
        Assert.Equal("only", Order(At(0, 0, "only")));
    }

    [Fact]
    public void DegenerateBoxesAreLeftAloneRatherThanReordered()
    {
        // Zero-height boxes give no median line height to scale the thresholds by, and guessing one
        // would order the page by a number that means nothing.
        var lines = new[]
        {
            new Line("a", new RectD(0, 0, 10, 0)),
            new Line("b", new RectD(0, 5, 10, 0)),
            new Line("c", new RectD(0, 9, 10, 0)),
        };

        Assert.Equal(lines, ReadingOrder.Sort(lines, l => l.Box));
    }
}
