using System.Diagnostics;
using ManualForge.Core.Geometry;
using ManualForge.Core.Ocr;
using ManualForge.Core.Rendering;
using ManualForge.Core.Text;
using ManualForge.Core.Verification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ManualForge.Core.Pipeline;

public sealed class BuildOptions
{
    /// <summary>Process only these page numbers (1-based). Empty means every page.</summary>
    public IReadOnlyList<int> Pages { get; init; } = [];

    /// <summary>Do everything except write the output file.</summary>
    public bool DryRun { get; init; }

    /// <summary>Allow replacing an existing output file.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Write a plain-text dump alongside the PDF, for eyeballing what was recognised.</summary>
    public string? TextDumpPath { get; init; }

    /// <summary>
    /// Identity under which recognition is cached. Defaults to the source path, which is wrong
    /// whenever the source is a temporary file: a flattened or stripped document lives under a
    /// fresh directory on every attempt, so keying on it means the cache is never hit and resume
    /// silently does nothing. Callers that pre-process a document must pass the stable path of the
    /// document in the library.
    /// </summary>
    public string? CacheKey { get; init; }
}

public sealed record PageReport(
    int PageNumber,
    int PixelWidth,
    int PixelHeight,
    double EffectiveDpi,
    int Rotation,
    int WordsRecognised,
    int WordsWritten,
    int WordsSkipped,
    double MeanConfidence,
    TimeSpan RasterTime,
    TimeSpan OcrTime,
    TimeSpan WriteTime);

public sealed record BuildReport(
    string SourcePath,
    string? OutputPath,
    int SourcePageCount,
    IReadOnlyList<PageReport> Pages,
    OcrRuntimeSummary Runtime,
    TimeSpan TotalTime,
    IReadOnlyList<string> Warnings,
    int ResumedPages = 0)
{
    public int TotalWordsWritten => Pages.Sum(p => p.WordsWritten);
    public double PagesPerMinute => TotalTime.TotalMinutes <= 0 ? 0 : Pages.Count / TotalTime.TotalMinutes;
}

/// <summary>
/// Turns one scanned PDF into a searchable one: rasterise, recognise, overlay, verify.
///
/// The source file is opened read-only and never written to. The page's own content — above all
/// the scanned image, which is usually a CCITT G4 stream that has survived forty years — is
/// carried through untouched; only a new content stream and a font resource are added.
/// </summary>
public sealed class SearchablePdfBuilder(
    IOcrEngine ocrEngine,
    PageRasteriser rasteriser,
    TextLayerWriter textLayerWriter,
    ILogger<SearchablePdfBuilder>? logger = null,
    IPageOcrCache? pageCache = null)
{
    private readonly IOcrEngine _ocrEngine = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
    private readonly PageRasteriser _rasteriser = rasteriser ?? throw new ArgumentNullException(nameof(rasteriser));
    private readonly TextLayerWriter _textLayerWriter = textLayerWriter ?? throw new ArgumentNullException(nameof(textLayerWriter));
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly IPageOcrCache _pageCache = pageCache ?? NullPageOcrCache.Instance;

    /// <summary>
    /// Identifies the settings a cached page was recognised under. Reusing results produced at a
    /// different resolution would put every word box in the wrong coordinate space, and nothing
    /// downstream could tell.
    /// </summary>
    public string SettingsFingerprint =>
        $"dpi={_rasteriser.Options.Dpi};grey={_rasteriser.Options.Grayscale};" +
        $"provider={_ocrEngine.Runtime.ExecutionProvider};conf={_textLayerWriter.Options.MinimumConfidence}";

    /// <summary>OCR results kept from the last build, so verification can compare against them.</summary>
    public IReadOnlyDictionary<int, PageInput> LastPageInputs => _lastPageInputs;

    private readonly Dictionary<int, PageInput> _lastPageInputs = [];

    /// <summary>What a page was handed to the writer, kept so verification can check it back.</summary>
    public sealed record PageInput(PageGeometry Geometry, IReadOnlyList<RecognisedWord> Words);

    public async Task<BuildReport> BuildAsync(
        string sourcePath,
        string outputPath,
        BuildOptions? options = null,
        IProgress<PageReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        options ??= new BuildOptions();

        var full = Path.GetFullPath(sourcePath);
        var outputFull = Path.GetFullPath(outputPath);
        if (string.Equals(full, outputFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to write the output over the source file.");
        if (!options.DryRun && !options.Overwrite && File.Exists(outputFull))
            throw new InvalidOperationException($"Output already exists: {outputFull}. Pass --overwrite to replace it.");

        _lastPageInputs.Clear();
        var warnings = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        // Recognition is cached against the document's stable identity, not against whatever
        // temporary file this attempt happens to be reading from.
        var cacheKey = Path.GetFullPath(options.CacheKey ?? full);

        var sourceBytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);

        using var document = OpenForModification(sourceBytes, full);
        var pageCount = document.PageCount;

        var rasterPageCount = PageRasteriser.GetPageCount(full);
        if (rasterPageCount != pageCount)
        {
            warnings.Add(
                $"Page count disagreement: PDFsharp sees {pageCount}, PDFium sees {rasterPageCount}. " +
                "Using the smaller of the two.");
            pageCount = Math.Min(pageCount, rasterPageCount);
        }

        var font = new InvisibleFont(document);
        var pageNumbers = options.Pages.Count > 0
            ? options.Pages.Where(p => p >= 1 && p <= pageCount).Distinct().Order().ToArray()
            : Enumerable.Range(1, pageCount).ToArray();

        if (options.Pages.Count > 0 && pageNumbers.Length != options.Pages.Count)
            warnings.Add($"Some requested pages are outside the document's 1-{pageCount} range and were ignored.");

        var reports = new List<PageReport>(pageNumbers.Length);
        var resumedPages = 0;
        var textDump = options.TextDumpPath is null ? null : new List<string>();

        foreach (var pageNumber in pageNumbers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = document.Pages[pageNumber - 1];

            var rasterWatch = Stopwatch.StartNew();
            using var raster = _rasteriser.Render(sourceBytes, pageNumber - 1);
            rasterWatch.Stop();

            var geometry = CreateGeometry(page, raster.Width, raster.Height, pageNumber);

            if (Math.Abs(geometry.EffectiveDpiX - raster.RequestedDpi) > 2)
            {
                warnings.Add(
                    $"Page {pageNumber}: rasterised at an effective {geometry.EffectiveDpiX:F1} dpi " +
                    $"rather than the requested {raster.RequestedDpi}; positions are scaled from the " +
                    "actual raster size, so alignment is unaffected.");
            }

            var ocrWatch = Stopwatch.StartNew();

            // A page recognised on an earlier attempt is reused rather than recognised again. This
            // is what makes an interrupted document cost the page in flight rather than the whole
            // document: recognition is the expensive half, assembling the PDF is not.
            var cached = _pageCache.TryGet(cacheKey, pageNumber, SettingsFingerprint);
            RecognisedWord[] words;
            var recognisedWordCount = 0;
            var meanConfidence = 0.0;
            var fromCache = cached is not null;

            if (cached is not null)
            {
                words = cached.Where(w => w.IsUsable).ToArray();
                recognisedWordCount = cached.Count;
                meanConfidence = cached.Count == 0 ? 0 : cached.Average(w => w.Confidence);
                resumedPages++;
            }
            else
            {
                var recognised = await _ocrEngine
                    .RecognisePageAsync(raster.EncodePng(), pageNumber, cancellationToken)
                    .ConfigureAwait(false);

                if (recognised.PixelWidth != 0 && recognised.PixelWidth != raster.Width)
                {
                    warnings.Add(
                        $"Page {pageNumber}: the OCR engine reports a {recognised.PixelWidth}x{recognised.PixelHeight} " +
                        $"source but the raster is {raster.Width}x{raster.Height}. Word boxes would be misplaced, " +
                        "so the page was left without a text layer.");
                    continue;
                }

                words = recognised.Words.Where(w => w.IsUsable).ToArray();
                recognisedWordCount = recognised.WordCount;
                meanConfidence = recognised.MeanConfidence;

                // Persisted before the page is written, so a crash between the two loses nothing.
                _pageCache.Save(cacheKey, pageNumber, SettingsFingerprint, words);
            }

            ocrWatch.Stop();

            var writeWatch = Stopwatch.StartNew();
            var layerResult = _textLayerWriter.WritePage(page, geometry, words, font);
            writeWatch.Stop();

            // Record the words that were actually emitted, not the ones that were offered: the
            // writer drops some, and verification has to line up with the content stream.
            _lastPageInputs[pageNumber] = new PageInput(geometry, layerResult.Written);

            textDump?.Add($"--- page {pageNumber} ---");
            if (textDump is not null)
                textDump.AddRange(words.Select(w => w.Text));

            var report = new PageReport(
                pageNumber,
                raster.Width,
                raster.Height,
                geometry.EffectiveDpiX,
                geometry.Rotation,
                recognisedWordCount,
                layerResult.WordsWritten,
                layerResult.WordsSkipped,
                meanConfidence,
                rasterWatch.Elapsed,
                ocrWatch.Elapsed,
                writeWatch.Elapsed);

            reports.Add(report);
            progress?.Report(report);

            _logger.LogInformation(
                "Page {Page}/{Total}: {Written} words written, {Skipped} skipped, {Dpi:F0} dpi, " +
                "raster {Raster}ms, ocr {Ocr}ms{Resumed}",
                pageNumber, pageNumbers.Length, layerResult.WordsWritten, layerResult.WordsSkipped,
                geometry.EffectiveDpiX, rasterWatch.ElapsedMilliseconds, ocrWatch.ElapsedMilliseconds,
                fromCache ? " (reused from an earlier attempt)" : "");
        }

        font.Finalise();

        // PDFsharp locks the in-memory document once it is saved, and reading page counts or any
        // other member afterwards throws. Take what the report needs while it is still readable.
        var savedPageCount = document.PageCount;

        string? written = null;
        if (!options.DryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputFull)!);
            // Write to a temporary file first so an interrupted run cannot leave a half-written
            // PDF sitting where a finished one is expected.
            var temporary = outputFull + ".partial";
            document.Save(temporary);
            File.Move(temporary, outputFull, overwrite: true);
            written = outputFull;

            if (options.TextDumpPath is not null && textDump is not null)
                await File.WriteAllLinesAsync(options.TextDumpPath, textDump, cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();

        return new BuildReport(
            full,
            written,
            savedPageCount,
            reports,
            _ocrEngine.Runtime,
            stopwatch.Elapsed,
            warnings,
            resumedPages);
    }

    private static PdfDocument OpenForModification(byte[] bytes, string path)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
        }
        catch (PdfSharp.Pdf.IO.PdfReaderException ex)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(path)}' cannot be opened for modification: {ex.Message}. " +
                "Owner passwords and digital signatures block modification; flattening them is phase 2.",
                ex);
        }
    }

    /// <summary>
    /// Builds the image-to-user-space mapping for a page. PDFium rasterises the crop box with
    /// /Rotate applied, so both have to come back out here.
    /// </summary>
    public static PageGeometry CreateGeometry(PdfPage page, int pixelWidth, int pixelHeight, int pageNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(page);

        // The ReadOnly accessors matter more than they look. PDFsharp's ordinary MediaBox and
        // CropBox getters *materialise* the entry when it is absent, so merely reading
        // page.CropBox on a page that has no crop box writes /CropBox [0 0 0 0] into the document.
        // That is an invalid rectangle: PDFium ignores it, but PdfPig honours it and reports the
        // page as zero-sized, which throws every extracted text coordinate out by a page
        // dimension. Reading a page must not change it.
        var media = page.MediaBoxReadOnly;
        var crop = page.CropBoxReadOnly;

        // A crop box is optional, and a degenerate one appears often enough in old scans that it
        // is worth falling back rather than failing.
        var box = crop is { Width: > 0, Height: > 0 } ? crop : media;
        if (box is not { Width: > 0, Height: > 0 })
            throw new InvalidOperationException($"Page {pageNumber} has no usable MediaBox or CropBox.");

        return new PageGeometry(
            Math.Min(box.X1, box.X2),
            Math.Min(box.Y1, box.Y2),
            Math.Abs(box.Width),
            Math.Abs(box.Height),
            page.Rotate,
            pixelWidth,
            pixelHeight);
    }

    /// <summary>
    /// Re-opens the written file with PdfPig and checks it against the source and against what the
    /// writer was asked to produce.
    /// </summary>
    public IReadOnlyList<PageVerification> Verify(string outputPath, int expectedPageCount)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(outputPath);
        if (document.NumberOfPages != expectedPageCount)
        {
            throw new InvalidOperationException(
                $"Verification failed: the output has {document.NumberOfPages} pages, the source had {expectedPageCount}.");
        }

        var results = new List<PageVerification>();
        foreach (var (pageNumber, input) in _lastPageInputs.OrderBy(kv => kv.Key))
        {
            var page = document.GetPage(pageNumber);
            results.Add(TextLayerVerifier.VerifyPage(
                page, input.Words, input.Geometry, _textLayerWriter.Options.BaselineOffsetFraction));
        }

        return results;
    }
}
