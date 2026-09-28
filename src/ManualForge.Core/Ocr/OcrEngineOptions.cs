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
    /// The largest page the recogniser will accept, in pixels. Kept equal to
    /// <see cref="Rendering.RasterOptions.MaxPixels"/>: when the two disagree a page can be
    /// rasterised and then refused, and the refusal arrives after the work is done.
    /// </summary>
    public long MaxImagePixels { get; init; } = 120_000_000;

    /// <summary>
    /// Run PP-OCRv5's larger detection and recognition networks instead of the default ones.
    ///
    /// <para>
    /// "Mobile" and "server" name the model's size, not the machine it runs on: the mobile pack is
    /// built to be small enough for constrained deployment, and the server pack is roughly 3-5x
    /// larger and more accurate. Nothing here ever chose, so every page in this library has been
    /// recognised by the small one on a desktop with a CUDA card. Issue #22.
    /// </para>
    /// </summary>
    public bool UseServerModels { get; init; }

    /// <summary>
    /// Check the page-orientation classifier before believing it.
    ///
    /// <para>
    /// PaddleOcrNet runs a whole-page classifier before detection and turns the page by whatever it
    /// says. On these scans it is wrong far more often than it is right: it called 26 of 100
    /// upright parts lists rotated, and 39 of 100 typical pages, every one of them checked by eye.
    /// A page turned by 90 degrees before detection reads as vertical stacks of characters, and on
    /// page 60 of the table book that left 273 words of 819. Yet on a page that really is sideways
    /// it is right every time, and without it a fold-out loses up to 43% of its text.
    /// </para>
    ///
    /// <para>
    /// So when it claims a rotation, the page is read again as it stands and the reading with more
    /// confidently recognised characters is kept. The second pass costs only on the pages the
    /// classifier fires on. False trusts the classifier outright, which is how every page before
    /// this was recognised. Issue #22; docs/measurements/page-orientation.md.
    /// </para>
    /// </summary>
    public bool VerifyPageOrientation { get; init; } = true;

    /// <summary>
    /// Read the lone characters the text detector never boxed.
    ///
    /// <para>
    /// The detector answers weakly to one glyph with nothing beside it, so the check-digit and
    /// quantity columns of a parts list come back empty: on 100 captioned tables it found 3,653
    /// of Acrobat's 8,100 lone characters, and no detector setting moved that by more than 1%. With
    /// this on, ink standing clear of every word, in a column of such ink on a row of text, is
    /// read on its own; that took the tables to 4,048, and added about one stray character per
    /// ordinary page. Issue #22; docs/measurements/single-characters.md.
    /// </para>
    /// </summary>
    public bool RescueOrphanGlyphs { get; init; } = true;

    /// <summary>
    /// Cut words the detector boxed across several rows of a table back into rows, and read each.
    ///
    /// <para>
    /// A column of short aligned entries - designator prefixes, check digits - is sometimes boxed
    /// as one tall region and read on its side as <c>NNNNN</c> or <c>mmmmm</c>. Such a word is cut
    /// at the blank rows of its own ink and each row read on its own crop. Issue #22;
    /// docs/measurements/single-characters.md.
    /// </para>
    /// </summary>
    public bool SplitTallStacks { get; init; } = true;

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
/// <param name="CudaLibraries">
/// What the search for the CUDA and cuDNN DLLs found, or null on a CPU run where it was not asked.
/// Reported next to the provider because the two answer different questions: the provider says
/// what was used, and this says whether there was anything to use.
/// </param>
public sealed record OcrRuntimeSummary(
    string ExecutionProvider,
    bool UsingGpu,
    string? AccelerationHint,
    string ModelCachePath,
    string? CudaLibraries = null,
    string SettingsFingerprint = "");
