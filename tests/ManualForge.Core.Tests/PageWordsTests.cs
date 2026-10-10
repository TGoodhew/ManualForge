using ManualForge.Core.Indexing;
using ManualForge.Core.Text;
using UglyToad.PdfPig;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// Words as a reader groups them (#47). PdfPig's default grouping cut a word wherever its letters were
/// tracked apart by a fraction of a point, which is how FrameMaker sets type: 9,935 of the library's
/// pages were indexed as <c>A g ile nt Te chn o log i e s</c>.
/// </summary>
public sealed class PageWordsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "manualforge-words-" + Guid.NewGuid().ToString("N"));

    public PageWordsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>A page whose only text is <paramref name="content"/>, in 11 pt Helvetica.</summary>
    private string Page(string content) =>
        TestPdf.ScannedWithContent(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".pdf"), content);

    private static string[] Words(string path)
    {
        using var document = PdfDocument.Open(path);
        return PageWords.Of(document.GetPage(1)).Select(w => w.Text).ToArray();
    }

    // Every letter set on its own, 0.22 pt apart: TJ's -20 is twenty thousandths of 11 pt.
    private const string Tracked =
        "BT /TestF1 11 Tf 72 700 Td [(A) -20 (g) -20 (i) -20 (l) -20 (e) -20 (n) -20 (t)] TJ ( ) Tj " +
        "[(T) -20 (e) -20 (c) -20 (h) -20 (n) -20 (o) -20 (l) -20 (o) -20 (g) -20 (i) -20 (e) -20 (s)] TJ ET\n";

    [Fact]
    public void TrackedLettersStayOneWord()
    {
        Assert.Equal(["Agilent", "Technologies"], Words(Page(Tracked)));
    }

    /// <summary>
    /// "Technologies" from E4418-90066 page 5, letter for letter as PdfPig measured it: 9.8 pt New
    /// Century Schoolbook whose glyph boxes come out flat - no height - and gaps between letters of
    /// -0.32 to +0.24 pt. The default grouping, which scales its tolerance by the glyph box, splits at
    /// every gap above nothing; that is the whole of #47.
    /// </summary>
    [Fact]
    public void LettersWithFlatGlyphBoxesStayOneWord()
    {
        (string Value, double Start, double End)[] measured =
        [
            ("T", 230.40, 236.97), ("e", 236.65, 241.57), ("c", 241.69, 246.06), ("h", 246.05, 252.06),
            ("n", 252.05, 258.07), ("o", 258.30, 263.22), ("l", 263.34, 266.44), ("o", 266.43, 271.35),
            ("g", 271.22, 276.51), ("i", 276.75, 279.85), ("e", 279.87, 284.79), ("s", 284.91, 289.47),
        ];
        const double baseline = 488.406;

        var font = new UglyToad.PdfPig.PdfFonts.FontDetails("NewCenturySchlbk-Roman", false, 400, false);
        var letters = measured.Select((m, i) =>
        {
            var flat = new UglyToad.PdfPig.Core.PdfRectangle(m.Start, baseline, m.End, baseline);
            return new UglyToad.PdfPig.Content.Letter(
                m.Value, flat, flat,
                new UglyToad.PdfPig.Core.PdfPoint(m.Start, baseline), new UglyToad.PdfPig.Core.PdfPoint(m.End, baseline),
                m.End - m.Start, 1, font, UglyToad.PdfPig.Core.TextRenderingMode.Fill,
                UglyToad.PdfPig.Graphics.Colors.GrayColor.Black, UglyToad.PdfPig.Graphics.Colors.GrayColor.Black,
                9.8, i);
        }).ToArray();

        Assert.Equal(["Technologies"], GapWordExtractor.Instance.GetWords(letters).Select(w => w.Text));

        // The bug, pinned: what the default made of the same letters.
        Assert.True(UglyToad.PdfPig.Util.DefaultWordExtractor.Instance.GetWords(letters).Count() > 1);
    }

    [Fact]
    public void AGapWiderThanKerningIsASpaceEvenWithoutOne()
    {
        // 4.4 pt, with no space character: a table cell boundary, or a typesetter that never writes one.
        var path = Page("BT /TestF1 11 Tf 72 700 Td [(Model) -400 (8340B)] TJ ET\n");

        Assert.Equal(["Model", "8340B"], Words(path));
    }

    [Fact]
    public void AWordSetSidewaysIsStillOneWord()
    {
        // Rotated a quarter turn: the letters advance up the page, not across it.
        var path = Page("BT /TestF1 11 Tf 0 1 -1 0 300 300 Tm [(S) -20 (i) -20 (d) -20 (e)] TJ ET\n");

        Assert.Equal(["Side"], Words(path));
    }

    [Fact]
    public void ANewLineIsANewWord()
    {
        var path = Page("BT /TestF1 11 Tf 72 700 Td (first) Tj 0 -14 Td (second) Tj ET\n");

        Assert.Equal(["first", "second"], Words(path));
    }

    [Fact]
    public void TheIndexHoldsTheWordsWhole()
    {
        var path = Page(Tracked);

        var text = Assert.Single(LibraryIndexer.ExtractPages(path)).Text;

        Assert.Contains("Agilent Technologies", text, StringComparison.Ordinal);
    }
}
