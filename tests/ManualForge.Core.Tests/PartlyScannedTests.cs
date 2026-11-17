using ManualForge.Core.Classification;
using ManualForge.Core.Ocr;
using ManualForge.Core.Pipeline;
using ManualForge.Core.Rendering;
using ManualForge.Core.State;
using ManualForge.Core.Text;

namespace ManualForge.Core.Tests;

/// <summary>
/// A scan reissued with typeset pages in front of it. <c>85033C Operating and service.pdf</c> was
/// added to the library with twenty of its twenty-two pages unsearchable: its transition notice on
/// page 2 lifted the sampled average over the image-only threshold, so the whole file was called
/// GoodText and left alone, and the audit left its bare scans to the OCR path that had just
/// skipped them.
/// </summary>
public sealed class PartlyScannedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));

    private readonly List<IDisposable> _disposables = [];

    public PartlyScannedTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            try { disposable.Dispose(); } catch (Exception) { /* best effort */ }
        }

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private const int Dpi = 150;

    private string InRoot(params string[] parts) => Path.Combine([_root, .. parts]);

    private LibraryOptions NewOptions(IReadOnlyList<string>? documents = null, bool retrySkipped = false) => new()
    {
        Root = _root,
        Documents = documents ?? [],
        RetrySkipped = retrySkipped,
    };

    private static LibraryProcessor NewProcessor(FakeOcrEngine engine) =>
        new(new SearchablePdfBuilder(engine, new PageRasteriser(new RasterOptions { Dpi = Dpi }), new TextLayerWriter()),
            new DocumentClassifier());

    private LibraryProcessor NewPipelinedProcessor(FakeOcrEngine engine)
    {
        var cache = new SqlitePageOcrCache(LibraryProcessor.StatePathFor(NewOptions()));
        _disposables.Add(cache);

        var rasteriser = new PageRasteriser(new RasterOptions { Dpi = Dpi });
        var builder = new SearchablePdfBuilder(engine, rasteriser, new TextLayerWriter(), pageCache: cache);
        var pipeline = new RecognitionPipeline(engine, rasteriser, cache, builder.SettingsFingerprint);
        return new LibraryProcessor(builder, new DocumentClassifier(), pageCache: cache, pipeline: pipeline);
    }

    private static string TextOfPage(string path, int page)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        return string.Join(" ", document.GetPage(page).GetWords().Select(w => w.Text));
    }

    [Fact]
    public void TypesetPagesInFrontOfAScanMakeItPartlyScannedNotGoodText()
    {
        var path = TestPdf.PartlyScanned(InRoot("85033C.pdf"), typesetPages: 2, scannedPages: 4);

        var result = new DocumentClassifier().Classify(path);

        Assert.Equal(TextClass.PartlyScanned, result.Class);
        Assert.StartsWith("4 of 6 sampled pages are scans", result.Rationale, StringComparison.Ordinal);
        Assert.Equal(ClassAction.Ocr, new ClassificationPolicy().ActionFor(result.Class));
    }

    [Fact]
    public void OnlyTheScannedPagesAreFound()
    {
        var path = TestPdf.PartlyScanned(InRoot("85033C.pdf"), typesetPages: 2, scannedPages: 4);

        Assert.Equal([3, 4, 5, 6], ScannedPages.In(path));
    }

    [Fact]
    public void AScanWithASmallImageOnItIsNotTakenForAFullPageScan()
    {
        // The fixture every other test calls "scanned" draws its image over an eighth of the page:
        // a figure in a typeset page, as far as this rule is concerned.
        var path = TestPdf.Scanned(InRoot("figure.pdf"), pages: 1);

        Assert.Empty(ScannedPages.In(path));
    }

    [Fact]
    public void AFullyTypesetDocumentIsStillGoodText()
    {
        var path = TestPdf.PartlyScanned(InRoot("typeset.pdf"), typesetPages: 3, scannedPages: 0);

        Assert.Equal(TextClass.GoodText, new DocumentClassifier().Classify(path).Class);
    }

    [Fact]
    public void GarbledTextBesideScansStaysSuspect()
    {
        // What to do with a garbled layer is its own decision, and the default is to leave it.
        var metrics = new[]
        {
            TextMetricsCalculator.Measure(1, Enumerable.Repeat("T'|1e i(~s ~he l,ne v0l'~ ;; ~~ .. ,,", 40)
                .SelectMany(s => s.Split(' ')), glyphCount: 900),
            PageTextMetrics.Empty(2) with { IsScanWithoutText = true },
        };

        var result = new DocumentClassifier().Summarise("garbled.pdf", 2, metrics);

        Assert.Equal(TextClass.SuspectText, result.Class);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARunRecognisesOnlyTheScansAndLeavesTheTypesetPagesAlone(bool pipelined)
    {
        var path = TestPdf.PartlyScanned(InRoot("85033C.pdf"), typesetPages: 2, scannedPages: 4);
        var before = File.ReadAllBytes(path);
        var typeset = TextOfPage(path, 1);

        var engine = new FakeOcrEngine();
        var processor = pipelined ? NewPipelinedProcessor(engine) : NewProcessor(engine);
        var options = NewOptions();
        processor.Survey(options);
        var outcome = Assert.Single(processor.Run(options));

        Assert.Equal(FileStatus.Completed, outcome.Status);
        Assert.Equal(TextClass.PartlyScanned, outcome.TextClass);
        Assert.Equal([3, 4, 5, 6], engine.PagesSeen.Order());

        // The scans can now be searched, and the typeset pages read exactly as they did.
        for (var page = 3; page <= 6; page++)
            Assert.Contains("HEWLETT", TextOfPage(path, page), StringComparison.Ordinal);
        Assert.Equal(typeset, TextOfPage(path, 1));
        Assert.DoesNotContain("HEWLETT", TextOfPage(path, 2), StringComparison.Ordinal);

        // The original is kept as it always is.
        Assert.Equal(before, File.ReadAllBytes(InRoot("_Originals", "85033C.pdf")));
    }

    [Fact]
    public void HiddenTextOnTheTypesetPagesDoesNotStopTheScansBeingRead()
    {
        // The refusal of a second hidden layer is about the pages being written, and nothing is
        // written to these.
        var path = TestPdf.PartlyScanned(InRoot("ocrd-cover.pdf"), typesetPages: 2, scannedPages: 4, hiddenTypeset: true);

        var engine = new FakeOcrEngine();
        var processor = NewProcessor(engine);
        var options = NewOptions();
        processor.Survey(options);
        var outcome = Assert.Single(processor.Run(options));

        Assert.Equal(FileStatus.Completed, outcome.Status);
        Assert.Equal([3, 4, 5, 6], engine.PagesSeen.Order());
        Assert.DoesNotContain("HEWLETT", TextOfPage(path, 1), StringComparison.Ordinal);
    }

    [Fact]
    public void ScansWithNoLetteringOnThemLeaveTheFileAsItWasWithoutFailingIt()
    {
        // A full-page photograph in a typeset manual is a scan without text, and has none to give.
        var path = TestPdf.PartlyScanned(InRoot("photo.pdf"), typesetPages: 2, scannedPages: 4);
        var before = File.ReadAllBytes(path);

        var processor = NewProcessor(new FakeOcrEngine(_ => []));
        var options = NewOptions();
        processor.Survey(options);
        var outcome = Assert.Single(processor.Run(options));

        Assert.Equal(FileStatus.Skipped, outcome.Status);
        Assert.Contains("hold no lettering", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(InRoot("_Originals", "photo.pdf")));
    }

    [Fact]
    public void RetryingSkippedFilesCanBeLimitedToTheFilesNamed()
    {
        // So that one file skipped by an older classifier can be looked at again without the whole
        // library being reclassified and queued.
        var named = TestPdf.PartlyScanned(InRoot("named.pdf"), typesetPages: 3, scannedPages: 0);
        var other = TestPdf.PartlyScanned(InRoot("other.pdf"), typesetPages: 3, scannedPages: 0);

        var processor = NewProcessor(new FakeOcrEngine());
        processor.Survey(NewOptions());

        using (var store = LibraryProcessor.OpenStore(NewOptions()))
        {
            Assert.Equal(FileStatus.Skipped, store.Find(named)!.Status);
            Assert.Equal(FileStatus.Skipped, store.Find(other)!.Status);

            Assert.Equal(1, store.ResetSkipped([named]));
            Assert.Equal(FileStatus.Discovered, store.Find(named)!.Status);
            Assert.Equal(FileStatus.Skipped, store.Find(other)!.Status);
        }
    }
}
