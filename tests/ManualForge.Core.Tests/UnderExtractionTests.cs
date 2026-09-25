using ManualForge.Core.Auditing;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// The detector's two halves, tested against each other: it has to find a page whose figure is
/// drawn rather than set, and it has to leave alone a page that is simply full of lines.
///
/// <para>
/// Both matter equally. A detector with no recall misses the failure it exists for; a detector with
/// no precision flags a hundred thousand pages, triggers a re-OCR nobody has time for, and teaches
/// everyone to ignore the report — which costs more than not having written it.
/// </para>
/// </summary>
public sealed class UnderExtractionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "manualforge-doctor-" + Guid.NewGuid().ToString("N"));

    public UnderExtractionTests() => Directory.CreateDirectory(_directory);

    private string Path_(string name) => System.IO.Path.Combine(_directory, name);

    [Fact]
    public void APageOfProseCarryingAFigureIsStillLookedAt()
    {
        // The gap that cost half the recall. Plenty of prose, so the "few characters" limb does not
        // fire; an image rather than vectors, so the "many paths" limb does not either. Before the
        // image-coverage limb existed this page was never rendered, and a page that is never
        // rendered cannot be flagged whatever the thresholds say.
        var prose = string.Join(' ', Enumerable.Repeat("the instrument responds to this command", 60));
        var path = TestPdf.ScannedWithText(Path_("prose-with-figure.pdf"), prose);

        var page = Assert.Single(new UnderExtractionDetector().Audit(path).Pages);

        Assert.NotEqual(InkAnalysis.NotRendered, page.Ink);
        Assert.True(page.CharactersDecoded > 900, "the page is supposed to be prose-heavy, or it proves nothing");
        Assert.True(page.PathPaintOperations < 40, "and it is supposed to paint no paths, or it proves nothing");
    }

    [Fact]
    public void WithoutThatLimbTheSamePageIsNeverRendered()
    {
        var prose = string.Join(' ', Enumerable.Repeat("the instrument responds to this command", 60));
        var path = TestPdf.ScannedWithText(Path_("prose-with-figure-off.pdf"), prose);

        // 1.0 is the two-limb gate the 17 September corpus audit was made with. Keeping it reachable
        // is what lets that audit's numbers be reproduced rather than merely remembered.
        var options = new DoctorOptions { RenderAtOrAboveImageCoverage = 1.0 };
        var page = Assert.Single(new UnderExtractionDetector(options).Audit(path).Pages);

        Assert.Equal(InkAnalysis.NotRendered, page.Ink);
    }

    [Fact]
    public void FlagsAPageWhoseFigureIsDrawnRatherThanSet()
    {
        var path = TestPdf.DrawnFigure(Path_("drawn.pdf"), "CHANnel Commands", labels: 150);

        var audit = new UnderExtractionDetector().Audit(path);

        Assert.Null(audit.Error);
        var page = Assert.Single(audit.Pages);
        Assert.Equal(PageVerdict.UnderExtracted, page.Verdict);
        Assert.True(page.EstimatedRecoverableCharacters > 0);

        // And it says why, in numbers somebody can argue with.
        Assert.True(page.Ink.UncoveredInkFraction > 0);
        Assert.True(page.Ink.GlyphLikeBlobs >= 100, $"only {page.Ink.GlyphLikeBlobs} blobs found");
    }

    [Fact]
    public void DoesNotFlagAPageThatIsOnlyRules()
    {
        // Far more ink than the drawn-figure page above, and nothing on it to recover.
        var path = TestPdf.RuledPage(Path_("ruled.pdf"), "Table 4-2. Performance Limits");

        var audit = new UnderExtractionDetector().Audit(path);

        var page = Assert.Single(audit.Pages);
        Assert.Equal(PageVerdict.Fine, page.Verdict);
        Assert.True(
            page.Ink.InkFraction > 0.01,
            "the test page is supposed to be ink-heavy, or it proves nothing");
    }

    [Fact]
    public void DoesNotFlagAnOrdinaryTypesetPage()
    {
        var path = TestPdf.TypesetOnly(
            Path_("typeset.pdf"), "Set the FREQUENCY control fully clockwise and observe the output.");

        var audit = new UnderExtractionDetector().Audit(path);

        Assert.Equal(PageVerdict.Fine, Assert.Single(audit.Pages).Verdict);
    }

    [Fact]
    public void ReportsAScannedPageSeparatelyFromAnUnderExtractedOne()
    {
        // The existing classify-and-OCR path owns this case. Lumping tens of thousands of scanned
        // pages in with the finding would bury it.
        var path = TestPdf.Scanned(Path_("scan.pdf"), pages: 1);

        var audit = new UnderExtractionDetector().Audit(path);

        var page = Assert.Single(audit.Pages);
        Assert.NotEqual(PageVerdict.UnderExtracted, page.Verdict);
        Assert.Equal(0, audit.FlaggedPageCount);
    }

    [Fact]
    public void ADocumentThatIsMostlyDrawnIsToRepair_OneWithAFewFiguresIsNot()
    {
        var mostly = TestPdf.DrawnFigure(Path_("mostly.pdf"), "CHANnel Commands", labels: 150, pages: 4);
        var few = Path_("few.pdf");

        // Twenty sound pages and one figure page: 5%, under the 10% the report uses to decide
        // whether a document is broken or merely illustrated.
        TestPdf.TypesetOnly(few, "Set the FREQUENCY control fully clockwise.", pages: 20);

        var detector = new UnderExtractionDetector();

        Assert.Equal(DocumentVerdict.UnderExtracted, detector.Audit(mostly).Verdict);
        Assert.Equal(DocumentVerdict.Sound, detector.Audit(few).Verdict);
    }

    [Fact]
    public void TheSameMarksAreDrawnOnOnePageAndInsideAnImageOnAnother()
    {
        // Identical ink, identical missing text, two different problems. A drawn figure never had a
        // text layer and no amount of re-OCRing the document would have found it; lettering inside
        // an image is a scan whose recogniser missed it. They are worth telling apart because on a
        // real corpus the second outnumbers the first by two orders of magnitude, and reporting
        // them as one list buries the finding this whole exercise is about.
        var drawn = TestPdf.DrawnFigure(Path_("drawn.pdf"), "CHANnel Commands", labels: 150);
        var scanned = TestPdf.DrawnFigure(
            Path_("scanned.pdf"), "CHANnel Commands", labels: 150, behindAnImage: true);

        var detector = new UnderExtractionDetector();

        var drawnPage = Assert.Single(detector.Audit(drawn).Pages);
        Assert.Equal(PageVerdict.UnderExtracted, drawnPage.Verdict);
        Assert.Equal(PageKind.Drawn, drawnPage.Kind);
        Assert.True(drawnPage.IsDrawn);

        var scannedPage = Assert.Single(detector.Audit(scanned).Pages);
        Assert.Equal(PageVerdict.UnderExtracted, scannedPage.Verdict);
        Assert.Equal(PageKind.Raster, scannedPage.Kind);
        Assert.False(scannedPage.IsDrawn);
    }

    [Fact]
    public void ADocumentOfScannedGapsIsNotOnTheSameListAsOneThatIsDrawn()
    {
        var drawn = TestPdf.DrawnFigure(Path_("drawn.pdf"), "CHANnel Commands", labels: 150, pages: 4);
        var scanned = TestPdf.DrawnFigure(
            Path_("scanned.pdf"), "CHANnel Commands", labels: 150, pages: 4, behindAnImage: true);

        var detector = new UnderExtractionDetector();

        Assert.Equal(DocumentVerdict.UnderExtracted, detector.Audit(drawn).Verdict);
        Assert.Equal(DocumentVerdict.ScannedGaps, detector.Audit(scanned).Verdict);
    }

    [Fact]
    public void ThresholdsAreConfigurable()
    {
        var path = TestPdf.DrawnFigure(Path_("tunable.pdf"), "CHANnel Commands", labels: 150);

        var strict = new UnderExtractionDetector(new DoctorOptions { MinimumGlyphLikeBlobs = 10_000 });
        Assert.Equal(PageVerdict.Fine, Assert.Single(strict.Audit(path).Pages).Verdict);

        var loose = new UnderExtractionDetector(new DoctorOptions { MinimumGlyphLikeBlobs = 1 });
        Assert.Equal(PageVerdict.UnderExtracted, Assert.Single(loose.Audit(path).Pages).Verdict);
    }

    [Fact]
    public void PageRangesReadTheWayAPersonWritesThem()
    {
        Assert.Equal("19-23,30,32-37", PageRanges.Format([19, 20, 21, 22, 23, 30, 32, 33, 34, 35, 36, 37]));
        Assert.Equal("7", PageRanges.Format([7]));
        Assert.Equal("", PageRanges.Format([]));
        Assert.Equal([19, 20, 21, 30], PageRanges.Parse("19-21,30"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception)
        {
            // A locked temp file is not a test failure.
        }
    }
}
