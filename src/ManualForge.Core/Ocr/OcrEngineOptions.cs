using PaddleOcrNet.Services;

namespace ManualForge.Core.Ocr;

/// <summary>Which ONNX Runtime execution provider to try. Mirrors PaddleOcrNet's own enum.</summary>
public enum OcrAccelerator
{
    /// <summary>Let the library pick: CUDA if usable, else DirectML, else CPU.</summary>
    Auto,

    /// <summary>
    /// NVIDIA CUDA. Fastest here, but ONNX Runtime 1.30 hard-imports cublas64_13.dll, so it needs
    /// the CUDA <b>13</b> runtime and cuDNN 9 for CUDA 13. A CUDA 12 install will not load.
    /// </summary>
    Cuda,

    /// <summary>DirectML. Slower than CUDA but works on any DX12 GPU with no extra runtime.</summary>
    DirectMl,

    /// <summary>CPU only.</summary>
    Cpu,
}

public sealed class OcrEngineOptions
{
    /// <summary>Where downloaded ONNX models are cached between runs.</summary>
    public string ModelCachePath { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManualForge", "models");

    public OcrAccelerator Accelerator { get; init; } = OcrAccelerator.Auto;

    public int DeviceId { get; init; } = 0;

    /// <summary>
    /// Recognition batch size. Sized against VRAM at startup rather than fixed, because an 8 GB
    /// card shared with a desktop session has considerably less than 8 GB actually free.
    /// </summary>
    public int BatchSize { get; init; } = 8;

    /// <summary>Deskew the page before recognition. The main lever on 1950s-80s scan quality.</summary>
    public bool Deskew { get; init; } = true;

    /// <summary>Despeckle before recognition.</summary>
    public bool Denoise { get; init; } = true;

    /// <summary>
    /// Drop recognition results below this score inside the engine. Kept low here so that the
    /// text-layer writer, which knows about page geometry, makes the final call.
    /// </summary>
    public double DropScore { get; init; } = 0.30;

    /// <summary>Never hit the network for models; fail if they are not already cached.</summary>
    public bool OfflineModels { get; init; } = false;

    /// <summary>
    /// Cap on the threads ONNX Runtime uses for a single operator. Null leaves it to the runtime,
    /// which takes every core.
    ///
    /// That default is right for a CPU-only run and wrong whenever anything else needs a core at
    /// low latency. A CPU engine running beside the GPU one is the case that matters: the GPU path
    /// averages only 27% of this machine's 24 threads, but it needs them in short bursts between
    /// its GPU phases, and an uncapped CPU engine makes those bursts queue behind it.
    /// </summary>
    public int? CpuThreads { get; init; }

    internal OcrExecutionProvider ToExecutionProvider() => Accelerator switch
    {
        OcrAccelerator.Cuda => OcrExecutionProvider.Cuda,
        OcrAccelerator.DirectMl => OcrExecutionProvider.DirectMl,
        OcrAccelerator.Cpu => OcrExecutionProvider.Cpu,
        _ => OcrExecutionProvider.Auto,
    };
}

/// <summary>What the engine actually resolved to at runtime, for logging and the UI.</summary>
public sealed record OcrRuntimeSummary(
    string ExecutionProvider,
    bool UsingGpu,
    string? AccelerationHint,
    string ModelCachePath);
