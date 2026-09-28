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

        _languages = [OcrLanguage.English];

        Runtime = new OcrRuntimeSummary(
            _service.ActiveExecutionProvider.ToString(),
            _service.UseGpu,
            _service.GpuAccelerationHint,
            options.ModelCachePath,
            cudaLibraries,
            // Everything here changes what recognition returns, so it has to reach the page cache.
            $"server={options.UseServerModels};deskew={options.Deskew};denoise={options.Denoise};" +
            $"drop={options.DropScore};orientation={(options.VerifyPageOrientation ? "verified" : "trusted")}");

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
