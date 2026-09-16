using ManualForge.Core.Classification;
using ManualForge.Core.Geometry;
using ManualForge.Core.Ocr;
using ManualForge.Core.Pdf;
using ManualForge.Core.Pipeline;
using ManualForge.Core.Rendering;
using ManualForge.Core.State;
using ManualForge.Core.Text;

namespace ManualForge.Core.Tests;

/// <summary>
/// Recognition that needs no models, no GPU and no network: it hands back the same three word
/// boxes for every page, positioned in image pixels exactly as PaddleOCR would.
///
/// Everything downstream of it is real — PDFium rasterises, PDFsharp writes, PdfPig reads back —
/// so these tests exercise the actual pipeline rather than a mock of it. Only the part that
/// depends on an 8 GB card is stood in for.
/// </summary>
internal sealed class FakeOcrEngine(Func<int, IReadOnlyList<RecognisedWord>>? words = null) : IOcrEngine
{
    private readonly Func<int, IReadOnlyList<RecognisedWord>> _words = words ?? (_ => SampleWords);

    /// <summary>Word boxes in image pixels, comfortably inside a letter page at 150 dpi.</summary>
    public static readonly IReadOnlyList<RecognisedWord> SampleWords =
    [
        new("HEWLETT", new RectD(150, 200, 260, 31), 0.98),
        new("PACKARD", new RectD(430, 200, 270, 31), 0.97),
        new("8340B", new RectD(150, 260, 200, 29), 0.95),
    ];

    public OcrRuntimeSummary Runtime { get; } = new("Fake", UsingGpu: false, null, "(no models)");

    /// <summary>How many pages actually went through recognition, as opposed to coming from cache.</summary>
    public int PagesRecognised => PagesSeen.Count;

    public List<int> PagesSeen { get; } = [];

    /// <summary>Runs before each page, so a test can interrupt or fail at a chosen point.</summary>
    public Action<int>? BeforePage { get; init; }

    /// <summary>
    /// Report a raster width other than the real one. The builder must notice and leave the page
    /// without a text layer rather than placing every box in the wrong coordinate space.
    /// </summary>
    public int MisreportPixelWidth { get; init; }

    public Task<RecognisedPage> RecognisePageAsync(
        byte[] imageBytes, int pageNumber, CancellationToken cancellationToken = default)
    {
        BeforePage?.Invoke(pageNumber);
        cancellationToken.ThrowIfCancellationRequested();

        PagesSeen.Add(pageNumber);

        var (width, height) = PngSize(imageBytes);
        var page = _words(pageNumber);

        var lines = page.Count == 0
            ? (IReadOnlyList<RecognisedLine>)[]
            : [new RecognisedLine(
                string.Join(' ', page.Select(w => w.Text)),
                RectD.Bound(page.SelectMany(w => new[]
                {
                    new PointD(w.BoxPx.Left, w.BoxPx.Top),
                    new PointD(w.BoxPx.Right, w.BoxPx.Bottom),
                })),
                page.Average(w => w.Confidence),
                page)];

        return Task.FromResult(new RecognisedPage(
            pageNumber,
            MisreportPixelWidth != 0 ? MisreportPixelWidth : width,
            height,
            lines,
            "Fake",
            TimeSpan.Zero));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Width and height straight out of the PNG's IHDR, so the size reported is the real one.</summary>
    private static (int Width, int Height) PngSize(byte[] png)
    {
        static int Be(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
        return (Be(png, 16), Be(png, 20));
    }
}

/// <summary>
/// The replace-in-place sequence, end to end.
///
/// This is the part of the application with something to lose: it moves files in a library the
/// user cannot reconstruct. The property under test throughout is the same one — <b>nothing in the
/// library is touched until a verified replacement exists</b> — so most of these tests check what
/// happened to the bytes on disk rather than what the code returned.
/// </summary>
public class LibraryProcessorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));

    private readonly List<IDisposable> _disposables = [];

    public LibraryProcessorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            try { disposable.Dispose(); } catch (Exception) { /* best effort */ }
        }

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // 150 dpi keeps the suite quick; nothing under test depends on the resolution, and the sample
    // word boxes are sized for it.
    private const int Dpi = 150;

    private LibraryOptions NewOptions(ClassificationPolicy? policy = null) => new()
    {
        Root = _root,
        Policy = policy ?? new ClassificationPolicy(),
    };

    private (LibraryProcessor Processor, FakeOcrEngine Engine) NewProcessor(
        FakeOcrEngine? engine = null, IPageOcrCache? cache = null)
    {
        engine ??= new FakeOcrEngine();

        var builder = new SearchablePdfBuilder(
            engine,
            new PageRasteriser(new RasterOptions { Dpi = Dpi }),
            new TextLayerWriter(),
            pageCache: cache);

        return (new LibraryProcessor(builder, new DocumentClassifier(), pageCache: cache), engine);
    }

    private SqlitePageOcrCache NewCache()
    {
        var cache = new SqlitePageOcrCache(Path.Combine(_root, "_Originals", "cache.db"));
        _disposables.Add(cache);
        return cache;
    }

    private string InRoot(params string[] parts) => Path.Combine([_root, .. parts]);

    private static string ExtractText(string path)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        return string.Join(" ", document.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
    }

    /// <summary>Every class routed to the same action, for tests about a path rather than a threshold.</summary>
    private static ClassificationPolicy PolicyOf(ClassAction action)
        => new(Enum.GetValues<TextClass>().ToDictionary(c => c, _ => action));

    // ---------------------------------------------------------------- the happy path

    [Fact]
    public void TheOriginalIsMovedAsideAndTheSearchableCopyTakesItsPlace()
    {
        var path = TestPdf.Scanned(InRoot("hp", "8340B.pdf"), pages: 2);
        var before = File.ReadAllBytes(path);

        var options = NewOptions();
        var (processor, engine) = NewProcessor();
        processor.Survey(options);
        var outcome = Assert.Single(processor.Run(options));

        Assert.Equal(FileStatus.Completed, outcome.Status);
        Assert.Null(outcome.Error);
        Assert.Equal(6, outcome.WordsWritten);
        Assert.Equal(2, engine.PagesRecognised);
        Assert.False(outcome.WasFlattened);

        // The original is kept byte for byte, in a tree mirroring where it came from.
        var original = InRoot("_Originals", "hp", "8340B.pdf");
        Assert.True(File.Exists(original));
        Assert.Equal(before, File.ReadAllBytes(original));

        // And the file at the original path is now searchable.
        Assert.Contains("HEWLETT", ExtractText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void TheReplacementKeepsThePageCountAndTheScannedImage()
    {
        var path = TestPdf.Scanned(InRoot("scan.pdf"), pages: 3);
        var before = PdfFingerprintOf(path);

        var options = NewOptions();
        var (processor, _) = NewProcessor();
        processor.Survey(options);
        processor.Run(options);

        // A text layer must change nothing else: same pages, same geometry, same image streams.
        Assert.Empty(PdfFlattener.Compare(before, PdfFingerprintOf(path)));
    }

    private static IReadOnlyList<PageImageFingerprint> PdfFingerprintOf(string path)
        => PdfFlattener.Fingerprint(path);

    [Fact]
    public void TheCompletedFileIsRecordedWithItsNewFingerprint()
    {
        var path = TestPdf.Scanned(InRoot("scan.pdf"), pages: 1);

        var options = NewOptions();
        var (processor, _) = NewProcessor();
        processor.Survey(options);
        processor.Run(options);

        using var store = LibraryProcessor.OpenStore(options);
        var record = store.Find(path)!;

        Assert.Equal(FileStatus.Completed, record.Status);
        Assert.Equal(InRoot("_Originals", "scan.pdf"), record.OriginalPath);

        // Recording the fingerprint of the file that was replaced rather than the one left behind
        // made restoring an original match a stale entry, so it was never reprocessed.
        Assert.Equal(FileFingerprint.Of(path), record.Fingerprint);
    }

    [Fact]
    public void ASecondRunOverAFinishedLibraryDoesNothing()
    {
        TestPdf.Scanned(InRoot("scan.pdf"), pages: 2);

        var options = NewOptions();
        var (processor, engine) = NewProcessor();
        processor.Survey(options);
        Assert.Single(processor.Run(options));

        processor.Survey(options);
        Assert.Empty(processor.Run(options));
        Assert.Equal(2, engine.PagesRecognised);
    }

    // ---------------------------------------------------------------- nothing is touched unless it verifies

    [Fact]
    public void AnOutputWithNoTextLayerIsRefusedAndTheSourceIsLeftAlone()
    {
        var path = TestPdf.Scanned(InRoot("scan.pdf"), pages: 1);
        var before = File.ReadAllBytes(path);

        var options = NewOptions();
        var (processor, _) = NewProcessor(new FakeOcrEngine(_ => []));
        processor.Survey(options);
        var outcome = Assert.Single(processor.Run(options));

        Assert.Equal(FileStatus.Failed, outcome.Status);
        Assert.Contains("no text layer", outcome.Error!, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(InRoot("_Originals", "scan.pdf")));
    }

    [Fact]
    public void ARasterSizeTheEngineDisagreesWithLeavesThePageUntouched()
    {
        var path = TestPdf.Scanned(InRoot("scan.pdf"), pages: 1);
        var before = File.ReadAllBytes(path);

        var options = NewOptions();
        var (processor, _) = NewProcessor(new FakeOcrEngine { MisreportPixelWidth = 999 });
        processor.Survey(options);
        var outcome = Assert.Single(processor.Run(options));

        // Placing boxes from a differently sized raster would put every word in the wrong place,
        // and nothing downstream could tell. Skipping the page is the only safe answer.
        Assert.Equal(FileStatus.Failed, outcome.Status);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ARecognitionFailureLeavesTheSourceWhereItWas()
    {
        var path = TestPdf.Scanned(InRoot("scan.pdf"), pages: 2);
        var before = File.ReadAllBytes(path);

        var engine = new FakeOcrEngine
        {
            BeforePage = page =>
            {
                if (page == 2)
                    throw new InvalidOperationException("the card fell over");
            },
        };

        var options = NewOptions();
        var (processor, _) = NewProcessor(engine);
        processor.Survey(options);
        var outcome = Assert.Single(processor.Run(options));

        Assert.Equal(FileStatus.Failed, outcome.Status);
        Assert.Contains("the card fell over", outcome.Error!, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(InRoot("_Originals", "scan.pdf")));
    }

    [Fact]
    public void AFileThatCannotBeOpenedFailsWithoutStoppingTheRun()
    {
        var good = TestPdf.Scanned(InRoot("good.pdf"), pages: 1);
        var broken = TestPdf.Scanned(InRoot("broken.pdf"), pages: 1);

        var options = NewOptions();
        var (processor, _) = NewProcessor();
        processor.Survey(options);

        // Corrupted after the survey, so the run is what has to cope with it.
        File.WriteAllText(broken, "this is not a PDF at all");

        var outcomes = processor.Run(options);

        Assert.Equal(FileStatus.Failed, outcomes.Single(o => o.Path == broken).Status);
        Assert.Equal(FileStatus.Completed, outcomes.Single(o => o.Path == good).Status);
    }

    [Fact]
    public void ADryRunReplacesNothing()
    {
        var path = TestPdf.Scanned(InRoot("scan.pdf"), pages: 1);
        var before = File.ReadAllBytes(path);

        var options = new LibraryOptions { Root = _root, DryRun = true };
        var (processor, engine) = NewProcessor();
        processor.Survey(options);
        var outcome = Assert.Single(processor.Run(options));

        Assert.Equal(FileStatus.Classified, outcome.Status);
        Assert.Equal(3, outcome.WordsWritten);
        Assert.Equal(1, engine.PagesRecognised);

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(InRoot("_Originals", "scan.pdf")));
    }

    // ---------------------------------------------------------------- files that refuse modification

    [Fact]
    public void AnOwnerPasswordIsFlattenedAndThenProcessed()
    {
        var path = TestPdf.Scanned(InRoot("locked.pdf"), pages: 1, ownerPassword: "secret");

        var options = NewOptions(PolicyOf(ClassAction.Ocr));
        var (processor, _) = NewProcessor();
        processor.Survey(options);
        var outcome = Assert.Single(processor.Run(options));

        Assert.Equal(FileStatus.Completed, outcome.Status);
        Assert.True(outcome.WasFlattened);

        // Flattening drops the owner password, which is the point: the result is a file anything
        // can open and edit.
        Assert.False(PdfInspector.Inspect(path).HasEncryptDictionary);
        Assert.Contains("HEWLETT", ExtractText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void ASignedFileIsProcessedByDefaultAndTheInvalidationIsReported()
    {
        var path = TestPdf.Signed(InRoot("signed.pdf"));
        Assert.True(PdfInspector.Inspect(path).HasSignature);

        var options = NewOptions(PolicyOf(ClassAction.Ocr));
        var (processor, _) = NewProcessor();
        processor.Survey(options);
        var outcome = Assert.Single(processor.Run(options));

        Assert.Equal(FileStatus.Completed, outcome.Status);
        Assert.True(outcome.SignatureInvalidated);
        Assert.True(File.Exists(InRoot("_Originals", "signed.pdf")));
    }

    [Fact]
    public void RefuseSignedFilesLeavesASignedFileAlone()
    {
        var path = TestPdf.Signed(InRoot("signed.pdf"));
        var before = File.ReadAllBytes(path);

        var options = new LibraryOptions
        {
            Root = _root,
            Policy = PolicyOf(ClassAction.Ocr),
            RefuseSignedFiles = true,
        };

        var (processor, engine) = NewProcessor();
        processor.Survey(options);
        var outcome = Assert.Single(processor.Run(options));

        Assert.Equal(FileStatus.Skipped, outcome.Status);
        Assert.False(outcome.SignatureInvalidated);
        Assert.Equal(0, engine.PagesRecognised);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // ---------------------------------------------------------------- strip and redo

    [Fact]
    public void AnExistingTextLayerIsRemovedBeforeTheNewOneIsWritten()
    {
        var path = TestPdf.ScannedWithText(InRoot("oldocr.pdf"), "Thc oId 0CR rcsu1t", pages: 1);
        Assert.Contains("Thc", ExtractText(path), StringComparison.Ordinal);

        var options = NewOptions(PolicyOf(ClassAction.StripAndRedo));
        var (processor, _) = NewProcessor();
        processor.Survey(options);
        var outcome = Assert.Single(processor.Run(options));

        Assert.Equal(FileStatus.Completed, outcome.Status);

        // Both layers surviving would be worse than either alone: searches would match the old bad
        // text as readily as the new good text.
        var text = ExtractText(path);
        Assert.Contains("HEWLETT", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Thc", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- duplicates

    [Fact]
    public void IdenticalCopiesAreRecognisedOnceAndCopiedToTheRest()
    {
        var primary = TestPdf.Scanned(InRoot("8340B Service Manual.pdf"), pages: 2);
        var copy = InRoot("staging", "08340-90243.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(primary, copy);

        var options = NewOptions();
        var (processor, engine) = NewProcessor();
        processor.Survey(options);
        var outcomes = processor.Run(options);

        // Two pages, not four: the copy takes the primary's result.
        Assert.Equal(2, engine.PagesRecognised);
        Assert.All(outcomes, o => Assert.Equal(FileStatus.Completed, o.Status));

        // Both paths open searchable, and both keep their own original.
        Assert.Equal(File.ReadAllBytes(primary), File.ReadAllBytes(copy));
        Assert.Contains("HEWLETT", ExtractText(copy), StringComparison.Ordinal);
        Assert.True(File.Exists(InRoot("_Originals", "8340B Service Manual.pdf")));
        Assert.True(File.Exists(InRoot("_Originals", "staging", "08340-90243.pdf")));
    }

    [Fact]
    public void ADuplicateWhosePrimaryIsNotReadyWaitsRatherThanFailing()
    {
        var path = TestPdf.Scanned(InRoot("copy.pdf"), pages: 1);
        var before = File.ReadAllBytes(path);

        var options = NewOptions();
        var (processor, engine) = NewProcessor();
        processor.Survey(options);

        using (var store = LibraryProcessor.OpenStore(options))
        {
            store.SetContentIdentity(path, "deadbeef", duplicateOf: InRoot("never-processed.pdf"));
            store.SetAction(path, ClassAction.CopyFromDuplicate);
        }

        var outcome = Assert.Single(processor.Run(options));

        // Left outstanding, not failed: a later run may yet produce the primary.
        Assert.Equal(FileStatus.Classified, outcome.Status);
        Assert.Contains("Waiting for its primary", outcome.Error!, StringComparison.Ordinal);
        Assert.Equal(0, engine.PagesRecognised);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ADuplicateWithNoPrimaryRecordedFails()
    {
        var path = TestPdf.Scanned(InRoot("copy.pdf"), pages: 1);

        var options = NewOptions();
        var (processor, _) = NewProcessor();
        processor.Survey(options);

        using (var store = LibraryProcessor.OpenStore(options))
            store.SetAction(path, ClassAction.CopyFromDuplicate);

        var outcome = Assert.Single(processor.Run(options));

        Assert.Equal(FileStatus.Failed, outcome.Status);
        Assert.Contains("no primary recorded", outcome.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADryRunCopiesNothingToADuplicate()
    {
        // At the same depth the descriptive name is the primary, so the part-numbered twin is the
        // one that waits to be copied.
        var primary = TestPdf.Scanned(InRoot("8340B Service Manual.pdf"), pages: 1);
        var copy = InRoot("08340-90243.pdf");
        File.Copy(primary, copy);
        var before = File.ReadAllBytes(copy);

        // Produce the primary and stop there, leaving the copy outstanding.
        var justThePrimary = new LibraryOptions { Root = _root, Limit = 1 };
        var (processor, _) = NewProcessor();
        processor.Survey(justThePrimary);
        Assert.Equal(primary, Assert.Single(processor.Run(justThePrimary)).Path);

        var dryRun = new LibraryOptions { Root = _root, DryRun = true };
        var outcome = Assert.Single(processor.Run(dryRun));
        Assert.Equal(FileStatus.Classified, outcome.Status);
        Assert.Equal(before, File.ReadAllBytes(copy));
    }

    // ---------------------------------------------------------------- resume

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnInterruptedDocumentResumesFromTheRecognitionItAlreadyHas(bool needsFlattening)
    {
        // The flattened case is the one that regressed. A document that refuses modification is
        // read from a temporary copy under a fresh GUID directory on every attempt, so caching
        // against the file being read rather than against the document's place in the library
        // silently disables resume altogether — and every unit test still passes.
        TestPdf.Scanned(InRoot("long.pdf"), pages: 4, ownerPassword: needsFlattening ? "secret" : null);

        var options = NewOptions(PolicyOf(ClassAction.Ocr));
        var cache = NewCache();

        using var cancellation = new CancellationTokenSource();
        var first = new FakeOcrEngine
        {
            BeforePage = page =>
            {
                if (page == 3)
                    cancellation.Cancel();
            },
        };

        var (interrupted, _) = NewProcessor(first, cache);
        interrupted.Survey(options);
        Assert.ThrowsAny<OperationCanceledException>(() => interrupted.Run(options, cancellationToken: cancellation.Token));
        Assert.Equal(2, first.PagesRecognised);

        var (resumed, second) = NewProcessor(cache: cache);
        var outcome = Assert.Single(resumed.Run(options));

        Assert.Equal(FileStatus.Completed, outcome.Status);
        Assert.Equal(12, outcome.WordsWritten);
        Assert.Equal(needsFlattening, outcome.WasFlattened);

        // Pages 1 and 2 came from the cache; only 3 and 4 needed the engine.
        Assert.Equal([3, 4], second.PagesSeen);
    }

    [Fact]
    public void FinishingADocumentReleasesItsCachedRecognition()
    {
        var path = TestPdf.Scanned(InRoot("scan.pdf"), pages: 1);

        var options = NewOptions();
        var cache = NewCache();
        var (processor, _) = NewProcessor(cache: cache);

        processor.Survey(options);
        processor.Run(options);

        // The cache holds the documents in flight, not the library.
        Assert.Null(cache.TryGet(path, 1, "dpi=150;grey=True;provider=Fake;conf=0.3"));
    }

    // ---------------------------------------------------------------- surveying and discovery

    [Fact]
    public void DiscoveryLeavesTheOriginalsTreeAndTheBaselineOutOfIt()
    {
        TestPdf.Scanned(InRoot("scan.pdf"), pages: 1);
        TestPdf.Scanned(InRoot("_Originals", "kept.pdf"), pages: 1);
        TestPdf.Scanned(InRoot("BASELINE", "acrobat.pdf"), pages: 1);

        var (processor, _) = NewProcessor();
        var found = processor.Discover(NewOptions()).ToArray();

        // Processing an original would loop forever; processing the baseline would overwrite the
        // very thing it exists to be compared against.
        Assert.Equal([InRoot("scan.pdf")], found);
    }

    [Fact]
    public void RecordsWhoseFilesHaveGoneAreMarkedMissing()
    {
        var path = TestPdf.Scanned(InRoot("scan.pdf"), pages: 1);
        TestPdf.Scanned(InRoot("stays.pdf"), pages: 1);

        var options = NewOptions();
        var (processor, _) = NewProcessor();
        processor.Survey(options);

        File.Delete(path);
        var records = processor.Survey(options);

        Assert.Equal(FileStatus.Missing, records.Single(r => r.Path == path).Status);
        Assert.DoesNotContain(processor.Run(options), o => o.Path == path);
    }

    [Fact]
    public void ASurveyClassifiesWithoutTouchingAnything()
    {
        var path = TestPdf.Scanned(InRoot("scan.pdf"), pages: 2);
        var before = File.ReadAllBytes(path);

        var seen = new List<DocumentClassification>();
        var (processor, engine) = NewProcessor();
        var records = processor.Survey(NewOptions(), new Progress<DocumentClassification>(seen.Add));

        var record = Assert.Single(records);
        Assert.Equal(TextClass.ImageOnly, record.TextClass);
        Assert.Equal(ClassAction.Ocr, record.Action);
        Assert.Equal(2, record.PageCount);
        Assert.Equal(0, engine.PagesRecognised);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ASurveyOnlyProcessorRefusesToRun()
    {
        TestPdf.Scanned(InRoot("scan.pdf"), pages: 1);
        var processor = new LibraryProcessor(builder: null, new DocumentClassifier());

        processor.Survey(NewOptions());
        var ex = Assert.Throws<InvalidOperationException>(() => processor.Run(NewOptions()));
        Assert.Contains("surveying only", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RetrySkippedBringsBackFilesThatWerePassedOver()
    {
        TestPdf.Scanned(InRoot("scan.pdf"), pages: 1);

        var skipEverything = NewOptions(PolicyOf(ClassAction.Skip));
        var (processor, _) = NewProcessor();
        processor.Survey(skipEverything);
        Assert.Empty(processor.Run(skipEverything));

        var reconsider = new LibraryOptions
        {
            Root = _root,
            Policy = PolicyOf(ClassAction.Ocr),
            RetrySkipped = true,
        };

        processor.Survey(reconsider);
        Assert.Equal(FileStatus.Completed, Assert.Single(processor.Run(reconsider)).Status);
    }

    // ---------------------------------------------------------------- ordering and limits

    [Fact]
    public void TheLimitStopsAfterThatManyFiles()
    {
        TestPdf.Scanned(InRoot("a.pdf"), pages: 1);
        TestPdf.Scanned(InRoot("b.pdf"), pages: 1);
        TestPdf.Scanned(InRoot("c.pdf"), pages: 1);

        var options = new LibraryOptions { Root = _root, Limit = 2 };
        var (processor, _) = NewProcessor();
        processor.Survey(options);

        Assert.Equal(2, processor.Run(options).Count);
    }

    [Fact]
    public void SmallestFirstGetsUsefulOutputSoonest()
    {
        var big = TestPdf.Scanned(InRoot("big.pdf"), pages: 6);
        var small = TestPdf.Scanned(InRoot("small.pdf"), pages: 1);
        Assert.True(new FileInfo(small).Length < new FileInfo(big).Length);

        var options = new LibraryOptions { Root = _root, SmallestFirst = true };
        var (processor, _) = NewProcessor();
        processor.Survey(options);

        Assert.Equal(small, processor.Run(options)[0].Path);
    }

    [Fact]
    public void ProgressIsReportedPerFile()
    {
        TestPdf.Scanned(InRoot("a.pdf"), pages: 1);
        TestPdf.Scanned(InRoot("b.pdf"), pages: 1);

        var options = NewOptions();
        var (processor, _) = NewProcessor();
        processor.Survey(options);

        var reported = new List<FileOutcome>();
        var outcomes = processor.Run(options, new Progress<FileOutcome>(reported.Add));

        // Progress<T> posts asynchronously, so give it a moment to drain before comparing.
        SpinWait.SpinUntil(() => reported.Count == outcomes.Count, TimeSpan.FromSeconds(5));
        Assert.Equal(outcomes.Count, reported.Count);
    }

    // ---------------------------------------------------------------- paths

    [Theory]
    [InlineData("scan.pdf", "_Originals/scan.pdf")]
    [InlineData("hp/8340B/service.pdf", "_Originals/hp/8340B/service.pdf")]
    public void TheOriginalsTreeMirrorsTheSourceStructure(string relative, string expected)
    {
        var options = NewOptions();
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(
            Path.Combine(_root, expected.Replace('/', Path.DirectorySeparatorChar)),
            LibraryProcessor.OriginalsPathFor(options, path));
    }

    [Fact]
    public void TheStateDatabaseLivesWithTheOriginalsUnlessToldOtherwise()
    {
        Assert.Equal(
            InRoot("_Originals", "manualforge.db"),
            LibraryProcessor.StatePathFor(NewOptions()));

        Assert.Equal(
            @"D:\elsewhere.db",
            LibraryProcessor.StatePathFor(new LibraryOptions { Root = _root, StatePath = @"D:\elsewhere.db" }));
    }
}
