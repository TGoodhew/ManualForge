using ManualForge.Core.Classification;
using ManualForge.Core.Ocr;
using ManualForge.Core.Pipeline;
using ManualForge.Core.Rendering;
using ManualForge.Core.State;
using ManualForge.Core.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualForge.Shell;

public sealed class LibraryServiceOptions
{
    public int Dpi { get; init; } = 300;

    public OcrAccelerator Accelerator { get; init; } = OcrAccelerator.Auto;

    public string? ModelCachePath { get; init; }

    /// <summary>Pages on the GPU at once. Null asks <see cref="GpuMemoryProbe"/> what will fit.</summary>
    public int? GpuConcurrency { get; init; }

    public int RasterWorkers { get; init; } = 2;
}

/// <summary>
/// The real engine room: classification without an OCR engine, and processing with one.
///
/// The engine is built on first use and kept, because loading the models takes about twenty seconds
/// and occupies the card. A survey never needs it, so opening the application and looking at a
/// folder costs nothing.
/// </summary>
public sealed class LibraryService(
    LibraryServiceOptions? options = null,
    ILoggerFactory? loggerFactory = null) : ILibraryService, IAsyncDisposable
{
    private readonly LibraryServiceOptions _options = options ?? new LibraryServiceOptions();
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    private readonly SemaphoreSlim _engineGate = new(1, 1);

    private PaddleOcrEngine? _engine;

    public GpuMemory? ReadGpu() => GpuMemoryProbe.TryRead();

    public async Task<IReadOnlyList<FileRecord>> SurveyAsync(
        string root,
        ClassificationPolicy policy,
        IProgress<DocumentClassification>? progress,
        CancellationToken cancellationToken)
    {
        var libraryOptions = OptionsFor(root, policy, dryRun: false);

        // Surveying is CPU work and there is a lot of it; keeping it off the caller's thread is the
        // whole reason this is async.
        return await Task.Run(
            () =>
            {
                var processor = new LibraryProcessor(
                    builder: null, new DocumentClassifier(), _loggerFactory.CreateLogger<LibraryProcessor>());
                return processor.Survey(libraryOptions, progress, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> TrimMissingAsync(string root, CancellationToken cancellationToken)
    {
        var libraryOptions = OptionsFor(root, new ClassificationPolicy(), dryRun: false);
        return await Task.Run(
            () =>
            {
                var processor = new LibraryProcessor(
                    builder: null, new DocumentClassifier(), _loggerFactory.CreateLogger<LibraryProcessor>());
                return processor.TrimMissing(libraryOptions);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FileOutcome>> RunAsync(
        string root,
        ClassificationPolicy policy,
        bool dryRun,
        IProgress<FileOutcome>? files,
        IProgress<PipelinePageProgress>? pages,
        CancellationToken cancellationToken)
    {
        var libraryOptions = OptionsFor(root, policy, dryRun);
        var engine = await EngineAsync(cancellationToken).ConfigureAwait(false);

        var rasteriser = new PageRasteriser(new RasterOptions { Dpi = _options.Dpi });
        var writer = new TextLayerWriter();

        using var pageCache = new SqlitePageOcrCache(LibraryProcessor.StatePathFor(libraryOptions));

        var builder = new SearchablePdfBuilder(
            engine, rasteriser, writer, _loggerFactory.CreateLogger<SearchablePdfBuilder>(), pageCache);

        var pipeline = new RecognitionPipeline(
            engine, rasteriser, pageCache, builder.SettingsFingerprint,
            _loggerFactory.CreateLogger<RecognitionPipeline>());

        var processor = new LibraryProcessor(
            builder, new DocumentClassifier(), _loggerFactory.CreateLogger<LibraryProcessor>(),
            pageCache, pipeline);

        var concurrency = _options.GpuConcurrency
            ?? (engine.Runtime.UsingGpu ? GpuMemoryProbe.ConcurrencyFor(GpuMemoryProbe.TryRead()) : 1);

        return await processor.RunAsync(
            libraryOptions,
            files,
            cancellationToken,
            new PipelineOptions
            {
                GpuConcurrency = Math.Max(1, concurrency),
                RasterWorkers = Math.Max(1, _options.RasterWorkers),
                Progress = pages,
            }).ConfigureAwait(false);
    }

    private LibraryOptions OptionsFor(string root, ClassificationPolicy policy, bool dryRun) => new()
    {
        Root = root,
        Policy = policy,
        DryRun = dryRun,
    };

    private async Task<PaddleOcrEngine> EngineAsync(CancellationToken cancellationToken)
    {
        if (_engine is not null)
            return _engine;

        await _engineGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _engine ??= await Task.Run(
                () => new PaddleOcrEngine(
                    new OcrEngineOptions
                    {
                        Accelerator = _options.Accelerator,
                        ModelCachePath = _options.ModelCachePath ?? new OcrEngineOptions().ModelCachePath,
                    },
                    _loggerFactory.CreateLogger<PaddleOcrEngine>()),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _engineGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
            await _engine.DisposeAsync().ConfigureAwait(false);

        _engineGate.Dispose();
    }
}
