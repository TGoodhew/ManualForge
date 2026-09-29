using System.Diagnostics;
using ManualForge.Core.Geometry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;

namespace ManualForge.Core.Ocr;

public interface IOcrEngine : IAsyncDisposable
{
    OcrRuntimeSummary Runtime { get; }

    Task<RecognisedPage> RecognisePageAsync(
        byte[] imageBytes,
        int pageNumber,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// PaddleOCR (DB detection + SVTR/CRNN recognition) on ONNX Runtime, wrapped so the rest of the
/// application sees plain word boxes in image pixels.
///
/// Word boxes come from the recogniser's per-character CTC timesteps, which is the only way to
/// get sub-line positions out of a CRNN. They are the reason the text layer can align at word
/// level rather than line level.
/// </summary>
public sealed class PaddleOcrEngine : IOcrEngine
{
    private readonly PaddleOcrService _service;
    private readonly RecognitionOptions _recognitionOptions;
    private readonly RecognitionOptions _uprightOptions;
    private readonly bool _verifyOrientation;
    private readonly bool _rescueOrphans;
    private readonly bool _splitStacks;
    private readonly double _dropScore;
    private readonly RecognitionOptions _orphanOptions;
    private readonly IReadOnlyList<OcrLanguage> _languages;
    private readonly ILogger _logger;

    public PaddleOcrEngine(OcrEngineOptions options, ILogger<PaddleOcrEngine>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        Directory.CreateDirectory(options.ModelCachePath);

        // Before the service is constructed, because constructing it is what loads the CUDA
        // provider, and a DLL the loader has already failed to find is not looked for again.
        var cudaLibraries = options.Accelerator == OcrAccelerator.Cpu
            ? null
            : CudaLibraries.Ensure(_logger).Describe();

        var serviceOptions = new PaddleOcrServiceOptions
        {
            ModelCachePath = options.ModelCachePath,
            ExecutionProvider = options.ToExecutionProvider(),
            UseGpu = options.Accelerator != OcrAccelerator.Cpu,
            DeviceId = options.DeviceId,
            Download = new ModelDownloadOptions { Offline = options.OfflineModels },
            IntraOpNumThreads = options.CpuThreads,
            InterOpNumThreads = options.CpuThreads is null ? null : 1,
            // Matched to the rasteriser's own ceiling. The library's default is 100M, ours is
            // 120M, and a page between the two rasterises happily and is then refused - which on
            // a 600 dpi run over a book of fold-outs killed the whole job at page 117. The guard
            // exists against decompression bombs from untrusted input; these are the user's own
            // manuals, already capped before they reach here.
            MaxImagePixels = options.MaxImagePixels,
            DetectionModel = options.UseServerModels ? OcrModelVariant.Server : OcrModelVariant.Mobile,
            RecognitionModel = options.UseServerModels ? OcrModelVariant.Server : OcrModelVariant.Mobile,
        };

        _service = new PaddleOcrService(serviceOptions, logger: null);

        _recognitionOptions = new RecognitionOptions
        {
            // Lines carry their constituent words, which is the shape the text-layer writer wants.
            Grouping = TextGrouping.Line,
            ReturnWordBoxes = true,
            BatchSize = options.BatchSize,
            DropScore = options.DropScore,
            Preprocessing = new PreprocessingOptions
            {
                Deskew = options.Deskew,
                Denoise = options.Denoise,
            },
        };

        _uprightOptions = _recognitionOptions with { UseDocOrientation = false };
        _verifyOrientation = options.VerifyPageOrientation;
        _rescueOrphans = options.RescueOrphanGlyphs;
        _splitStacks = options.SplitTallStacks;
        _dropScore = options.DropScore;

        // One crop per candidate, already cut to the glyph or table row, so nothing here may turn,
        // pad or regroup it: a lone character gives the text-line classifier nothing to go on, and
        // padding pulls a table's rules into the crop.
        _orphanOptions = new RecognitionOptions
        {
            Grouping = TextGrouping.Word,
            ReturnWordBoxes = true,
            BatchSize = options.BatchSize,
            DropScore = 0,
            UseDocOrientation = false,
            UseTextLineOrientation = false,
            CropPadding = 0,
        };

        _languages = [OcrLanguage.English];

        Runtime = new OcrRuntimeSummary(
            _service.ActiveExecutionProvider.ToString(),
            _service.UseGpu,
            _service.GpuAccelerationHint,
            options.ModelCachePath,
            cudaLibraries,
            // Everything here changes what recognition returns, so it has to reach the page cache.
            $"server={options.UseServerModels};deskew={options.Deskew};denoise={options.Denoise};" +
            $"drop={options.DropScore};orientation={(options.VerifyPageOrientation ? "verified" : "trusted")};" +
            $"orphans={(options.RescueOrphanGlyphs ? "rescued" : "left")};" +
            $"stacks={(options.SplitTallStacks ? "split" : "left")}");

        _logger.LogInformation(
            "OCR engine ready: provider {Provider}, GPU {UsingGpu}, models in {ModelCache}",
            Runtime.ExecutionProvider, Runtime.UsingGpu, Runtime.ModelCachePath);
    }

    public OcrRuntimeSummary Runtime { get; }

    public async Task<RecognisedPage> RecognisePageAsync(
        byte[] imageBytes,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        var stopwatch = Stopwatch.StartNew();
        var result = await _service
            .ExtractTextFromImage(imageBytes, _languages, _recognitionOptions, cancellationToken)
            .ConfigureAwait(false);
        var lines = ToLines(result);

        if (_verifyOrientation && result.DetectedOrientation != 0)
        {
            var upright = await _service
                .ExtractTextFromImage(imageBytes, _languages, _uprightOptions, cancellationToken)
                .ConfigureAwait(false);
            var uprightLines = ToLines(upright);

            int turned = ConfidentCharacters(lines), asItStands = ConfidentCharacters(uprightLines);
            if (asItStands >= turned)
            {
                _logger.LogDebug(
                    "Page {Page}: classifier said {Degrees} degrees; read as it stands instead ({Upright} confident characters against {Turned})",
                    pageNumber, result.DetectedOrientation, asItStands, turned);
                result = upright;
                lines = uprightLines;
            }
        }

        // Not on a page the classifier turned and was believed: its boxes then belong to the
        // turned page, and the ink being searched is the page as it stands.
        if ((_splitStacks || _rescueOrphans) && result.DetectedOrientation == 0 && OrphanGlyphs.LineHeight(lines) is not null)
        {
            using var page = InkPage.Load(imageBytes);
            // Stacks first: a stack's box covers its column, and the orphan search has to see
            // the rows it really holds.
            if (_splitStacks)
                lines = await SplitStacksAsync(page, lines, cancellationToken).ConfigureAwait(false);
            if (_rescueOrphans)
                lines.AddRange(await RescueOrphansAsync(page, lines, cancellationToken).ConfigureAwait(false));
        }
        stopwatch.Stop();

        return new RecognisedPage(
            pageNumber,
            result.SourceWidth,
            result.SourceHeight,
            lines,
            result.ExecutionProvider.ToString(),
            stopwatch.Elapsed);
    }

    /// <summary>
    /// How much of a reading the recogniser was sure of: letters and digits in words at 0.8 or
    /// above. A page read the wrong way round comes back long but unsure - vertical stacks read
    /// as <c>NNNNN</c> and <c>55555</c> - so length alone would favour it.
    /// </summary>
    /// <remarks>
    /// Checked on both kinds of error. Three clean pages turned by 90, 180 and 270 degrees kept
    /// the classifier's reading all nine times; on the table book it overruled the classifier on
    /// 24 of the 26 pages it fired on, and all 26 were upright.
    /// </remarks>
    internal static int ConfidentCharacters(IEnumerable<RecognisedLine> lines) =>
        lines.SelectMany(l => l.Words)
            .Where(w => w.Confidence >= 0.8)
            .Sum(w => w.Text.Count(char.IsLetterOrDigit));

    /// <summary>
    /// Reads the lone characters the detector left unboxed, each from a crop of its own. See
    /// <see cref="OrphanGlyphs"/> for which ink qualifies.
    /// </summary>
    /// <remarks>
    /// Each reading is placed on the glyph's own ink box rather than the recogniser's word box:
    /// word positions come from the recogniser's timesteps, and across a single character those
    /// collapse to a sliver a fraction of a point tall.
    /// </remarks>
    private async Task<List<RecognisedLine>> RescueOrphansAsync(
        InkPage page,
        IReadOnlyList<RecognisedLine> found,
        CancellationToken cancellationToken)
    {
        if (OrphanGlyphs.LineHeight(found) is not { } lineHeight)
            return [];

        var candidates = OrphanGlyphs.Find(
            page.Ink, page.Width, page.Height, found,
            (c, why) => _logger.LogDebug(
                "Orphan at {Left:F0},{Top:F0} {Width:F0}x{Height:F0} px dropped: {Why}", c.Left, c.Top, c.Width, c.Height, why));
        if (candidates.Count == 0)
            return [];

        var crops = candidates.Select(c => OrphanGlyphs.CropFor(c, lineHeight, page.Width, page.Height)).ToList();
        var rescued = new List<RecognisedLine>();
        foreach (var (k, line) in await ReadCropsAsync(page, crops, cancellationToken).ConfigureAwait(false))
        {
            var text = line.Text.Trim();
            if (!OrphanGlyphs.Keep(text, line.Confidence))
            {
                _logger.LogDebug(
                    "Orphan at {Left:F0},{Top:F0} {Width:F0}x{Height:F0} px dropped: read '{Text}' at {Confidence:F2}",
                    candidates[k].Left, candidates[k].Top, candidates[k].Width, candidates[k].Height, text, line.Confidence);
                continue;
            }

            var box = candidates[k];
            rescued.Add(new RecognisedLine(text, box, line.Confidence, [new RecognisedWord(text, box, line.Confidence)]));
        }

        _logger.LogDebug(
            "{Candidates} orphan glyph candidates, {Kept} kept", candidates.Count, rescued.Count);
        return rescued;
    }

    /// <summary>
    /// Replaces each stack - a column of table rows boxed and read as one word - with its rows,
    /// each read on its own. See <see cref="TallStacks"/>.
    /// </summary>
    /// <remarks>
    /// A row read as one word is placed on the row's ink. Several words share the row out between
    /// them (<see cref="TallStacks.WordBoxes"/>): across a short crop the recogniser's own boxes
    /// collapse to a sliver, and the text layer sizes its glyphs from the box. A stack whose rows
    /// cannot be found or read is left as it was.
    /// </remarks>
    private async Task<List<RecognisedLine>> SplitStacksAsync(
        InkPage page,
        List<RecognisedLine> lines,
        CancellationToken cancellationToken)
    {
        if (OrphanGlyphs.LineHeight(lines) is not { } lineHeight)
            return lines;

        // By the time a stack has been read, it is several words each claiming part of the
        // column, and nothing says they were one region. So the regions are asked for again -
        // the detector alone - but only on a page that shows the symptom.
        if (!lines.SelectMany(l => l.Words).Any(w => w.BoxPx.Height > 2 * lineHeight && w.BoxPx.Height >= w.BoxPx.Width))
            return lines;

        var regions = (await _service
            .DetectRegionsAsync(page.Image, _recognitionOptions, cancellationToken)
            .ConfigureAwait(false))
            .Select(r => ToRect(r.BoundingBox))
            .ToList();
        var regionHeights = regions.Where(r => r.Width > r.Height).Select(r => r.Height).Order().ToList();
        double rowHeight = regionHeights.Count >= 5 ? regionHeights[regionHeights.Count / 2] : lineHeight;

        // Horizontal words of ordinary height: what a stack's rows have to line up with. A table's
        // stacked column sits beside its part numbers; a label set sideways on a drawing does not.
        var rowWords = lines.SelectMany(l => l.Words).Select(w => w.BoxPx)
            .Where(b => b.Width > b.Height && b.Height > 0.3 * rowHeight && b.Height <= 2 * rowHeight)
            .ToList();

        var cutRegions = new List<RectD>();
        var rows = new List<(RectD Box, int Region)>();
        foreach (var region in regions.Where(r => TallStacks.IsStack(r, rowHeight)))
        {
            var found = TallStacks.Rows(page.Ink, page.Width, page.Height, region, rowHeight, out var verdict);
            int onRows = found.Count(r => OrphanGlyphs.OnTextRow(r, rowWords, rowHeight));
            if (found.Count > 0 && 2 * onRows < found.Count)
                verdict = $"off the text rows: {onRows} of {found.Count}";
            _logger.LogDebug(
                "Stack at {Left:F0},{Top:F0} {Width:F0}x{Height:F0} px, line {Line:F0} px: {Verdict}",
                region.Left, region.Top, region.Width, region.Height, rowHeight, verdict);
            if (verdict != "cut")
                continue;
            rows.AddRange(found.Select(r => (r, cutRegions.Count)));
            cutRegions.Add(RectD.FromEdges(region.Left - 2, region.Top - 2, region.Right + 2, region.Bottom + 2));
        }

        if (cutRegions.Count == 0)
            return lines;

        var crops = rows.Select(r => OrphanGlyphs.CropFor(r.Box, rowHeight, page.Width, page.Height)).ToList();
        var readings = new List<(int Region, RecognisedLine Line)>();
        foreach (var (k, line) in await ReadCropsAsync(page, crops, cancellationToken).ConfigureAwait(false))
        {
            if (line.Confidence < _dropScore || !line.Text.Any(char.IsLetterOrDigit))
                continue;

            var row = rows[k].Box;
            var read = (line.Words ?? [])
                .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .OrderBy(w => w.BoundingBox.MinX + w.BoundingBox.MaxX)
                .ToList();
            List<RecognisedWord> words;
            if (read.Count <= 1)
            {
                words = [new RecognisedWord(line.Text.Trim(), row, line.Confidence)];
            }
            else
            {
                var boxes = TallStacks.WordBoxes(
                    page.Ink, page.Width, row, read.Select(w => (w.BoundingBox.MinX + w.BoundingBox.MaxX) / 2).ToList());
                words = read.Select((w, i) => new RecognisedWord(w.Text, boxes[i], w.Confidence)).ToList();
            }

            readings.Add((rows[k].Region, new RecognisedLine(line.Text.Trim(), row, line.Confidence, words)));
        }

        // A region is replaced only when at least half its rows came back readable. Otherwise
        // what it held stays as it was read: removing words is only worth it for words in return.
        var replaced = Enumerable.Range(0, cutRegions.Count)
            .Where(i => 2 * readings.Count(r => r.Region == i) >= rows.Count(r => r.Region == i))
            .ToHashSet();
        var stackBoxes = replaced.Select(i => cutRegions[i]).ToList();
        var split = readings.Where(r => replaced.Contains(r.Region)).Select(r => r.Line).ToList();

        bool InStack(RecognisedWord w)
        {
            double cx = (w.BoxPx.Left + w.BoxPx.Right) / 2, cy = (w.BoxPx.Top + w.BoxPx.Bottom) / 2;
            return stackBoxes.Any(s => cx >= s.Left && cx <= s.Right && cy >= s.Top && cy <= s.Bottom);
        }

        var stacks = new HashSet<RecognisedWord>(
            lines.SelectMany(l => l.Words).Where(InStack), ReferenceEqualityComparer.Instance);

        _logger.LogDebug(
            "{Cut} stacked regions cut into {Rows} rows, {Read} read; {Replaced} replaced, taking {Words} words",
            cutRegions.Count, rows.Count, readings.Count, replaced.Count, stacks.Count);

        if (replaced.Count == 0)
            return lines;

        var kept = new List<RecognisedLine>(lines.Count + split.Count);
        foreach (var line in lines)
        {
            if (!line.Words.Any(stacks.Contains))
            {
                kept.Add(line);
                continue;
            }

            // The stack was chained into a line with the rows beside it; what is left of that
            // line is those rows' words, and its box has to shrink to them.
            var rest = line.Words.Where(w => !stacks.Contains(w)).ToList();
            if (rest.Count > 0)
                kept.Add(new RecognisedLine(
                    string.Join(' ', rest.Select(w => w.Text)),
                    RectD.FromEdges(rest.Min(w => w.BoxPx.Left), rest.Min(w => w.BoxPx.Top), rest.Max(w => w.BoxPx.Right), rest.Max(w => w.BoxPx.Bottom)),
                    line.Confidence,
                    rest));
        }

        kept.AddRange(split);
        return kept;
    }

    /// <summary>
    /// Recognises each crop on its own, detection skipped, and pairs every reading with the index
    /// of the crop it came from.
    /// </summary>
    private async Task<List<(int Crop, OcrLine Line)>> ReadCropsAsync(
        InkPage page,
        IReadOnlyList<RectD> crops,
        CancellationToken cancellationToken)
    {
        var polygons = crops.Select(r => (IReadOnlyList<OcrPoint>)
        [
            new OcrPoint(r.Left, r.Top), new OcrPoint(r.Right, r.Top),
            new OcrPoint(r.Right, r.Bottom), new OcrPoint(r.Left, r.Bottom),
        ]);

        var read = await _service
            .RecognizeRegionsAsync(page.Image, polygons, _languages, _orphanOptions, cancellationToken)
            .ConfigureAwait(false);

        var paired = new List<(int, OcrLine)>(read.Lines.Count);
        foreach (var line in read.Lines)
        {
            double cx = (line.BoundingBox.MinX + line.BoundingBox.MaxX) / 2;
            double cy = (line.BoundingBox.MinY + line.BoundingBox.MaxY) / 2;
            int k = -1;
            for (int i = 0; i < crops.Count && k < 0; i++)
                if (cx >= crops[i].Left && cx <= crops[i].Right && cy >= crops[i].Top && cy <= crops[i].Bottom)
                    k = i;
            if (k >= 0)
                paired.Add((k, line));
        }

        return paired;
    }

    /// <summary>The page as the recogniser takes it, and as a map of where it is dark.</summary>
    private sealed class InkPage : IDisposable
    {
        private InkPage(EasyImageSharp.Image<EasyImageSharp.PixelFormats.Rgb24> image, bool[] ink)
        {
            Image = image;
            Ink = ink;
        }

        public EasyImageSharp.Image<EasyImageSharp.PixelFormats.Rgb24> Image { get; }
        public bool[] Ink { get; }
        public int Width => Image.Width;
        public int Height => Image.Height;

        public static InkPage Load(byte[] imageBytes)
        {
            var image = EasyImageSharp.Image.Load<EasyImageSharp.PixelFormats.Rgb24>(imageBytes);
            int width = image.Width, height = image.Height;
            var ink = new bool[width * height];
            image.ProcessPixelRows(pixels =>
            {
                for (int y = 0; y < height; y++)
                {
                    var row = pixels.GetRowSpan(y);
                    for (int x = 0; x < width; x++)
                        ink[y * width + x] = row[x].R < 128;
                }
            });
            return new InkPage(image, ink);
        }

        public void Dispose() => Image.Dispose();
    }

    private static List<RecognisedLine> ToLines(OcrResult result)
    {
        var lines = new List<RecognisedLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var words = new List<RecognisedWord>(line.Words?.Count ?? 0);

            if (line.Words is { Count: > 0 })
            {
                foreach (var word in line.Words)
                {
                    var box = ToRect(word.BoundingBox);
                    if (box.IsDegenerate)
                        continue;
                    words.Add(new RecognisedWord(word.Text, box, word.Confidence));
                }
            }
            else
            {
                // No word boxes came back for this line — a short line, or a recogniser that
                // could not resolve timesteps. Fall back to the whole line as one run, which is
                // still positionally correct, just coarser for selection.
                var lineBox = ToRect(line.BoundingBox);
                if (!lineBox.IsDegenerate && !string.IsNullOrWhiteSpace(line.Text))
                    words.Add(new RecognisedWord(line.Text, lineBox, line.Confidence));
            }

            lines.Add(new RecognisedLine(line.Text, ToRect(line.BoundingBox), line.Confidence, words));
        }

        return lines;
    }

    /// <summary>
    /// Converts PaddleOcrNet's min/max box into the origin-plus-size rectangle used elsewhere.
    /// Both are in image pixels with a top-left origin, so no flip is needed here.
    /// </summary>
    private static RectD ToRect(OcrBoundingBox box) =>
        RectD.FromEdges(box.MinX, box.MinY, box.MaxX, box.MaxY);

    public async ValueTask DisposeAsync() => await _service.DisposeAsync().ConfigureAwait(false);

}
