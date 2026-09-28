using ManualForge.Core.Geometry;
using ManualForge.Core.Ocr;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// The text detector leaves a parts list's check digits and quantities unboxed: one glyph in a
/// ruled cell, with nothing beside it on its line. These tests build that page out of rectangles -
/// a part number and a description on six rows, 30 px tall, and whatever ink each case needs
/// between them - and check which ink is offered to the recogniser.
///
/// <para>
/// The failures the filters exist for were all seen on real pages: a word's own first letter
/// re-read beside it (<c>CAPACITOR-FXD C</c>), a table's rules, and strokes of a schematic read as
/// <c>1</c>, <c>V</c> and <c>t</c>.
/// </para>
/// </summary>
public sealed class OrphanGlyphTests
{
    private const int Width = 600, Height = 400, Row = 30;

    private static int RowTop(int i) => 20 + i * 40;

    private static readonly IReadOnlyList<RecognisedLine> PartsList =
        Enumerable.Range(0, 6).Select(i =>
        {
            var part = RectD.FromEdges(20, RowTop(i), 140, RowTop(i) + Row);
            var description = RectD.FromEdges(260, RowTop(i), 400, RowTop(i) + Row);
            return new RecognisedLine(
                "0160-3622 CAPACITOR-FXD",
                RectD.FromEdges(20, RowTop(i), 400, RowTop(i) + Row),
                0.99,
                [new RecognisedWord("0160-3622", part, 0.99), new RecognisedWord("CAPACITOR-FXD", description, 0.99)]);
        }).ToList();

    private static bool[] Page(params RectD[] marks)
    {
        var ink = new bool[Width * Height];
        foreach (var m in marks)
            for (int y = (int)m.Top; y < (int)m.Bottom; y++)
                for (int x = (int)m.Left; x < (int)m.Right; x++)
                    ink[y * Width + x] = true;
        return ink;
    }

    /// <summary>A glyph 10 px wide and 20 tall, centred on row <paramref name="i"/>.</summary>
    private static RectD Glyph(int left, int i) => RectD.FromEdges(left, RowTop(i) + 5, left + 10, RowTop(i) + 25);

    private static RectD[] Column(int left, params int[] rows) => rows.Select(i => Glyph(left, i)).ToArray();

    [Fact]
    public void RescuingIsTheDefault()
    {
        // The documented default, pinned: a flag wired backwards once went unnoticed for two
        // whole ingests because nothing asserted which way round it was.
        Assert.True(new OcrEngineOptions().RescueOrphanGlyphs);
    }

    [Fact]
    public void ACheckDigitColumnIsFound()
    {
        var found = OrphanGlyphs.Find(Page(Column(180, 0, 1, 2, 3, 4, 5)), Width, Height, PartsList);

        Assert.Equal(6, found.Count);
        Assert.All(found, c => Assert.Equal(180, c.Left));
        Assert.Equal(Enumerable.Range(0, 6).Select(i => (double)RowTop(i) + 5), found.Select(c => c.Top).Order());
    }

    [Fact]
    public void AWordsOwnLastLetterIsNotAnOrphan()
    {
        // Word boxes run narrower than the ink, so a word's last letter can sit just outside its
        // box. Five pixels from the part number's edge is well inside the clearance.
        var found = OrphanGlyphs.Find(Page(Column(145, 0, 1, 2, 3, 4, 5)), Width, Height, PartsList);

        Assert.Empty(found);
    }

    [Fact]
    public void ATableRuleIsNotAGlyph()
    {
        var rule = RectD.FromEdges(220, 0, 222, 300);

        Assert.Empty(OrphanGlyphs.Find(Page(rule), Width, Height, PartsList));
        Assert.Equal(6, OrphanGlyphs.Find(Page([rule, .. Column(180, 0, 1, 2, 3, 4, 5)]), Width, Height, PartsList).Count);
    }

    [Fact]
    public void AGlyphOnItsOwnIsLeftAlone()
    {
        // Strokes of a drawing are scattered; a table's orphans line up. Two in a column is not
        // enough - each needs two others.
        var found = OrphanGlyphs.Find(Page([.. Column(180, 0, 3), Glyph(210, 1)]), Width, Height, PartsList);

        Assert.Empty(found);
    }

    [Fact]
    public void InkOffEveryRowOfTextIsLeftAlone()
    {
        // A column of marks below the table, where no word shares their row.
        var marks = new[] { 300, 330, 360 }.Select(y => RectD.FromEdges(180, y, 190, y + 20)).ToArray();

        Assert.Empty(OrphanGlyphs.Find(Page(marks), Width, Height, PartsList));
    }

    [Fact]
    public void GlyphsSideBySideAreReadTogether()
    {
        // A quantity of 10: two glyphs three pixels apart are one candidate, not two.
        var tens = Enumerable.Range(0, 3).SelectMany(i => new[] { Glyph(180, i), Glyph(193, i) }).ToArray();

        var found = OrphanGlyphs.Find(Page(tens), Width, Height, PartsList);

        Assert.Equal(3, found.Count);
        Assert.All(found, c => Assert.Equal((180.0, 203.0), (c.Left, c.Right)));
    }

    [Fact]
    public void WithoutEnoughTextToMeasureALineNothingIsRescued()
    {
        var found = OrphanGlyphs.Find(Page(Column(180, 0, 1, 2, 3, 4, 5)), Width, Height, PartsList.Take(4).ToList());

        Assert.Empty(found);
    }

    [Fact]
    public void ANarrowCropIsWidenedSidewaysOnly()
    {
        // A lone 1, four pixels wide: read from a crop that narrow it comes back as 4.
        var one = RectD.FromEdges(100, 50, 104, 70);

        var crop = OrphanGlyphs.CropFor(one, Row, Width, Height);

        Assert.Equal(OrphanGlyphs.MinimumWidthFraction * Row, crop.Width, 6);
        Assert.Equal(102, (crop.Left + crop.Right) / 2, 6);
        Assert.Equal(20 + 2 * OrphanGlyphs.PaddingFraction * Row, crop.Height, 6);
    }

    [Fact]
    public void ACropStaysOnThePage()
    {
        var crop = OrphanGlyphs.CropFor(RectD.FromEdges(0, 0, 4, 20), Row, Width, Height);

        Assert.Equal(0, crop.Left);
        Assert.Equal(0, crop.Top);
    }

    [Theory]
    [InlineData("7", 0.98, true)]
    [InlineData("10", 0.95, true)]
    [InlineData("7", 0.85, false)]   // below the bar
    [InlineData("|", 0.99, false)]   // a rule, not a character
    [InlineData(".", 0.99, false)]
    [InlineData(" ", 0.99, false)]
    public void OnlyConfidentLettersAndDigitsAreKept(string text, double confidence, bool kept)
    {
        Assert.Equal(kept, OrphanGlyphs.Keep(text, confidence));
    }
}
