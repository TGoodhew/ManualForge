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
    private readonly IReadOnlyList<OcrLanguage> _languages;
    private readonly ILogger _logger;

    public PaddleOcrEngine(OcrEngineOptions options, ILogger<PaddleOcrEngine>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        Directory.CreateDirectory(options.ModelCachePath);

        var serviceOptions = new PaddleOcrServiceOptions
        {
            ModelCachePath = options.ModelCachePath,
            ExecutionProvider = options.ToExecutionProvider(),
            UseGpu = options.Accelerator != OcrAccelerator.Cpu,
            DeviceId = options.DeviceId,
            Download = new ModelDownloadOptions { Offline = options.OfflineModels },
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

        _languages = [OcrLanguage.English];

        Runtime = new OcrRuntimeSummary(
            _service.ActiveExecutionProvider.ToString(),
            _service.UseGpu,
            _service.GpuAccelerationHint,
            options.ModelCachePath);

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
        stopwatch.Stop();

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

        return new RecognisedPage(
            pageNumber,
            result.SourceWidth,
            result.SourceHeight,
            lines,
            result.ExecutionProvider.ToString(),
            stopwatch.Elapsed);
    }

    /// <summary>
    /// Converts PaddleOcrNet's min/max box into the origin-plus-size rectangle used elsewhere.
    /// Both are in image pixels with a top-left origin, so no flip is needed here.
    /// </summary>
    private static RectD ToRect(OcrBoundingBox box) =>
        RectD.FromEdges(box.MinX, box.MinY, box.MaxX, box.MaxY);

    public async ValueTask DisposeAsync() => await _service.DisposeAsync().ConfigureAwait(false);
}
