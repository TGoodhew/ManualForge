using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualForge.Core.Ocr;

/// <summary>Where a search for the CUDA and cuDNN runtimes got to.</summary>
/// <param name="CudaDirectory">The directory holding cuBLAS and the CUDA runtime, or null.</param>
/// <param name="CudnnDirectory">The directory holding cuDNN 9, or null.</param>
/// <param name="Missing">
/// The libraries still unaccounted for once <c>PATH</c> and anything found have been considered.
/// Empty means the CUDA provider has everything it imports.
/// </param>
/// <param name="AlreadyOnPath">
/// Everything was resolvable before the search, so nothing was added. The ordinary case on a
/// machine set up by hand, and worth distinguishing from a successful search: it means the
/// environment is right rather than that we rescued it.
/// </param>
/// <param name="TensorRtDirectory">The directory holding TensorRT 10, when it was found off PATH.</param>
/// <param name="TensorRtOnPath">TensorRT 10 was already resolvable on PATH.</param>
/// <param name="TensorRtLookedFor">TensorRT was searched for, so its absence is worth saying.</param>
public sealed record CudaLibraryLocation(
    string? CudaDirectory,
    string? CudnnDirectory,
    IReadOnlyList<string> Missing,
    bool AlreadyOnPath,
    string? TensorRtDirectory = null,
    bool TensorRtOnPath = false,
    bool TensorRtLookedFor = false)
{
    public bool Complete => Missing.Count == 0;

    /// <summary>
    /// TensorRT can be used: optional, and only worth anything on top of a complete CUDA install,
    /// which it runs on. Its absence costs speed, never correctness (#53).
    /// </summary>
    public bool HasTensorRt => Complete && (TensorRtOnPath || TensorRtDirectory is not null);

    /// <summary>A line for the log and the <c>gpu</c> command. Never null, never a stack trace.</summary>
    public string Describe() => DescribeCuda() + DescribeTensorRt();

    private string DescribeTensorRt() =>
        !Complete || !TensorRtLookedFor ? ""
        : TensorRtOnPath ? "; TensorRT already on PATH"
        : TensorRtDirectory is not null ? $"; TensorRT at {TensorRtDirectory}"
        : "; no TensorRT (plain CUDA)";

    private string DescribeCuda()
    {
        if (AlreadyOnPath)
            return "already on PATH";

        var found = new List<string>(2);
        if (CudaDirectory is not null)
            found.Add($"CUDA at {CudaDirectory}");
        if (CudnnDirectory is not null)
            found.Add($"cuDNN at {CudnnDirectory}");

        if (Complete)
            return found.Count == 0 ? "already on PATH" : "located " + string.Join(", ", found);

        var missing = "missing " + string.Join(", ", Missing);
        return found.Count == 0 ? missing : "located " + string.Join(", ", found) + "; " + missing;
    }
}

/// <summary>
/// Finds the CUDA and cuDNN DLLs and puts them where the loader will look, so that the GPU path
/// does not depend on whoever set the machine up having got <c>PATH</c> right.
///
/// This exists because the failure it prevents is silent. ONNX Runtime 1.30's CUDA provider
/// hard-imports <c>cublasLt64_13.dll</c>; when the loader cannot find it, nothing throws — the
/// provider simply does not register, execution falls back to CPU, and the only symptom is that
/// the run takes an order of magnitude longer. A tool launched by double-clicking, which inherits
/// whatever environment Explorer had, is exactly where that goes unnoticed.
///
/// The search looks for the files rather than assuming where they sit, which is deliberate. The
/// directory holding the DLLs is not the one you land in, on either dependency and for different
/// reasons: CUDA 13 moved its Windows binaries from <c>bin</c> down into <c>bin\x64</c>, and the
/// cuDNN zip extracts into a nested folder three levels below where it lands. Both mistakes look
/// identical from the outside — a directory that exists and holds no DLLs — so a hard-coded path
/// is a liability even when it is written down carefully, and it was not: the path recorded in the
/// README was a CUDA 12 layout that no longer existed.
/// </summary>
public static class CudaLibraries
{
    /// <summary>
    /// What ONNX Runtime 1.30's CUDA provider imports. CUDA 12 carries the same libraries under a
    /// <c>_12</c> suffix and will not satisfy these, which is why the major version is written into
    /// the file names rather than discovered.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredLibraries =
    [
        "cublas64_13.dll",
        "cublasLt64_13.dll",
        "cudart64_13.dll",
        "cudnn64_9.dll",
        "cudnn_graph64_9.dll",
    ];

    /// <summary>The file whose presence identifies a CUDA binary directory. The one that failed.</summary>
    private const string CudaProbe = "cublasLt64_13.dll";

    /// <summary>The file whose presence identifies a cuDNN binary directory.</summary>
    private const string CudnnProbe = "cudnn64_9.dll";

    /// <summary>
    /// What ONNX Runtime 1.30's TensorRT provider imports from TensorRT 10 (#53). Both, from the same
    /// directory: a folder with the core library and not the ONNX parser is a partial install, and
    /// attaching the provider fails on it exactly as it does with nothing at all.
    /// </summary>
    public static readonly IReadOnlyList<string> TensorRtLibraries = ["nvinfer_10.dll", "nvonnxparser_10.dll"];

    /// <summary>
    /// How far below a root to look. Four covers the toolkit root down to v13.4/bin/x64, and
    /// C:\Tools down to the nested cuDNN archive's bin/x64, with one level to spare. Bounded
    /// because a root given by mistake must cost a moment, not a walk of the volume.
    /// </summary>
    private const int MaxDepth = 4;

    /// <summary>Hard cap on directories examined, for the same reason.</summary>
    private const int MaxDirectoriesVisited = 4096;

    private static readonly Lock Gate = new();
    private static CudaLibraryLocation? _applied;

    /// <summary>
    /// Locates the libraries and prepends what it found to the process <c>PATH</c>, so that the
    /// loader finds them when ONNX Runtime asks. Idempotent: the search runs once per process and
    /// later callers get the same answer.
    /// </summary>
    /// <remarks>
    /// <c>PATH</c> rather than <c>AddDllDirectory</c>. The latter is the tidier API but only takes
    /// effect once <c>SetDefaultDllDirectories</c> has been called, and that changes the search
    /// order for every load in the process, including ones this code knows nothing about — PDFium,
    /// SQLite, the Windows App SDK. Prepending to <c>PATH</c> adds a place to look without taking
    /// any away. On Windows .NET sets the real process environment block, so the loader sees it.
    /// </remarks>
    public static CudaLibraryLocation Ensure(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        lock (Gate)
        {
            if (_applied is not null)
                return _applied;

            if (!OperatingSystem.IsWindows())
            {
                _applied = new CudaLibraryLocation(null, null, [], AlreadyOnPath: true);
                return _applied;
            }

            var location = Locate(FromEnvironment());

            if (!location.AlreadyOnPath || location.TensorRtDirectory is not null)
            {
                var added = new[] { location.CudaDirectory, location.CudnnDirectory, location.TensorRtDirectory }
                    .Where(directory => !string.IsNullOrEmpty(directory))
                    .ToArray();

                if (added.Length > 0)
                {
                    var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    Environment.SetEnvironmentVariable(
                        "PATH", string.Join(Path.PathSeparator, added.Append(path)));
                }
            }

            if (location.AlreadyOnPath)
            {
                logger.LogDebug("CUDA libraries were already resolvable; PATH left alone.");
            }
            else if (location.Complete)
            {
                logger.LogInformation(
                    "CUDA libraries {Location}, added to the process PATH.", location.Describe());
            }
            else
            {
                logger.LogWarning(
                    "The GPU path will not load: {Location}. OCR will run on CPU, which is roughly ten "
                    + "times slower. Install CUDA 13 and cuDNN 9 for CUDA 13, or pass --engine cpu "
                    + "to choose CPU deliberately.",
                    location.Describe());
            }

            _applied = location;
            return _applied;
        }
    }

    /// <summary>
    /// The search itself, with everything it touches passed in so it can be tested on a machine
    /// that has no CUDA on it — which includes every machine the test suite runs on.
    /// </summary>
    public static CudaLibraryLocation Locate(CudaSearch search)
    {
        ArgumentNullException.ThrowIfNull(search);

        // TensorRT is looked for whatever CUDA's state, because CUDA being on PATH already says
        // nothing about where TensorRT is. Found or not, it never makes CUDA incomplete.
        var tensorRtOnPath = TensorRtLibraries.All(library => ResolvableIn(search.PathDirectories, library, search));
        var tensorRt = tensorRtOnPath ? null : FirstDirectoryContainingAll(search.TensorRtRoots ?? [], TensorRtLibraries, search);

        if (RequiredLibraries.All(library => ResolvableIn(search.PathDirectories, library, search)))
            return new CudaLibraryLocation(null, null, [], AlreadyOnPath: true, tensorRt, tensorRtOnPath, search.TensorRtRoots is not null);

        var cuda = FirstDirectoryContaining(search.CudaRoots, CudaProbe, search);
        var cudnn = FirstDirectoryContaining(search.CudnnRoots, CudnnProbe, search);

        // What is still missing is judged against PATH *and* what was just found, because the two
        // halves arrive independently: a machine can carry a working CUDA install and no cuDNN at
        // all, and saying which is the whole point of reporting rather than merely failing.
        var resolvable = new List<string>(search.PathDirectories);
        if (cuda is not null)
            resolvable.Add(cuda);
        if (cudnn is not null)
            resolvable.Add(cudnn);

        var missing = RequiredLibraries
            .Where(library => !ResolvableIn(resolvable, library, search))
            .ToList();

        return new CudaLibraryLocation(cuda, cudnn, missing, AlreadyOnPath: false, tensorRt, tensorRtOnPath, search.TensorRtRoots is not null);
    }

    /// <summary>The first directory under the roots holding every one of the files.</summary>
    private static string? FirstDirectoryContainingAll(
        IReadOnlyList<string> roots,
        IReadOnlyList<string> fileNames,
        CudaSearch search)
    {
        var found = FirstDirectoryContaining(roots, fileNames[0], search);
        return found is not null && fileNames.All(file => search.FileExists(found, file)) ? found : null;
    }

    private static bool ResolvableIn(IEnumerable<string> directories, string library, CudaSearch search)
        => directories.Any(directory => search.FileExists(directory, library));

    private static string? FirstDirectoryContaining(
        IReadOnlyList<string> roots,
        string fileName,
        CudaSearch search)
    {
        var budget = MaxDirectoriesVisited;

        foreach (var root in roots)
        {
            var found = BreadthFirst(root, fileName, search, ref budget);
            if (found is not null)
                return found;
        }

        return null;
    }

    /// <summary>
    /// Directories holding another processor architecture's build of the same libraries.
    ///
    /// <para>
    /// CUDA 13 installs an arm64 copy of every one of these DLLs beside the x64 one, under the same
    /// file names. Handing the loader the wrong architecture fails, and it fails the same silent way
    /// a missing file does — the provider declines to register and everything runs on the CPU at a
    /// tenth of the speed. Descending name order happens to put <c>x64</c> before <c>arm64</c>, so
    /// this was working by luck; skipping them outright is the part that is actually true.
    /// </para>
    /// </summary>
    private static bool IsForeignArchitecture(string directoryName)
    {
        var mine = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "win32",
            _ => "x64",
        };

        foreach (var known in new[] { "x64", "arm64", "aarch64", "win32", "x86" })
        {
            if (string.Equals(directoryName, known, StringComparison.OrdinalIgnoreCase))
                return !string.Equals(known, mine, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Breadth-first so that a shallower answer wins, and newest-first within a level so that
    /// v13.4 is preferred to v13.0 and a later cuDNN to an earlier one. Descending ordinal order is
    /// a crude version comparison, but it never has to choose between major versions: the file name
    /// being looked for carries the major version already.
    /// </summary>
    private static string? BreadthFirst(string root, string fileName, CudaSearch search, ref int budget)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;

        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0 && budget > 0)
        {
            var (directory, depth) = queue.Dequeue();
            budget--;

            if (search.FileExists(directory, fileName))
                return directory;

            if (depth >= MaxDepth)
                continue;

            var children = search.Subdirectories(directory)
                .Where(child => !IsForeignArchitecture(Path.GetFileName(child)))
                .OrderByDescending(child => child, StringComparer.OrdinalIgnoreCase);

            foreach (var child in children)
                queue.Enqueue((child, depth + 1));
        }

        return null;
    }

    /// <summary>
    /// Where the installer puts the NVIDIA libraries it downloads (#26): <c>cuda</c>, <c>cudnn</c> and
    /// <c>tensorrt</c> folders of DLLs, outside the program folder and off <c>PATH</c>, so they never
    /// collide with a toolkit installed for other work and uninstalling removes them cleanly.
    /// <c>MANUALFORGE_GPU_LIBRARIES</c> points somewhere else, for testing a set of libraries
    /// without installing it.
    /// </summary>
    public static string ChainedLibrariesRoot =>
        Environment.GetEnvironmentVariable("MANUALFORGE_GPU_LIBRARIES") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManualForge", "gpu");

    /// <summary>
    /// The real machine: the installer's own copies, <c>PATH</c>, the environment variables the CUDA
    /// installer sets, and the places these two things are conventionally put.
    /// </summary>
    internal static CudaSearch FromEnvironment()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var toolkit = Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA");
        var chained = ChainedLibrariesRoot;

        // The installer's own copies first (#26). They are the versions it pinned and the ones its
        // measurements were made with, and they are there only because nothing else was found when it
        // ran - so a toolkit installed since then for other work does not change what is read.
        // CUDA_PATH points at the active install and CUDA_PATH_V13_4 and friends at every install
        // present. Taking them next means an explicitly selected toolkit beats whatever happens to be
        // newest on disk.
        var cudaRoots = new List<string> { Path.Combine(chained, "cuda") };
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key
                && key.StartsWith("CUDA_PATH", StringComparison.OrdinalIgnoreCase)
                && entry.Value is string value
                && !string.IsNullOrWhiteSpace(value))
            {
                cudaRoots.Add(value);
            }
        }

        cudaRoots.Add(toolkit);

        var cudnnRoots = new List<string> { Path.Combine(chained, "cudnn") };
        if (Environment.GetEnvironmentVariable("CUDNN_PATH") is { Length: > 0 } cudnnPath)
            cudnnRoots.Add(cudnnPath);
        cudnnRoots.Add(Path.Combine(programFiles, "NVIDIA", "CUDNN"));
        cudnnRoots.Add(Path.Combine(Path.GetPathRoot(programFiles) ?? @"C:\", "Tools"));

        // Some installs copy cuDNN in beside the CUDA binaries, so the toolkit is worth a look too.
        cudnnRoots.AddRange(cudaRoots);

        // TensorRT: tools/setup-cuda.ps1 puts it in C:\Tools beside cuDNN. NVIDIA's own zip has no
        // fixed home, so TENSORRT_PATH can name one.
        var tensorRtRoots = new List<string> { Path.Combine(chained, "tensorrt") };
        if (Environment.GetEnvironmentVariable("TENSORRT_PATH") is { Length: > 0 } tensorRtPath)
            tensorRtRoots.Add(tensorRtPath);
        tensorRtRoots.Add(Path.Combine(Path.GetPathRoot(programFiles) ?? @"C:\", "Tools"));
        tensorRtRoots.Add(Path.Combine(programFiles, "NVIDIA", "TensorRT"));

        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return new CudaSearch(
            PathDirectories: pathDirectories,
            CudaRoots: cudaRoots,
            CudnnRoots: cudnnRoots,
            TensorRtRoots: tensorRtRoots,
            FileExists: static (directory, file) =>
            {
                try { return File.Exists(Path.Combine(directory, file)); }
                catch (Exception) { return false; }
            },
            Subdirectories: static directory =>
            {
                try { return Directory.Exists(directory) ? Directory.GetDirectories(directory) : []; }
                catch (Exception) { return []; }
            });
    }

    /// <summary>Discards the cached result. Tests only.</summary>
    internal static void Forget()
    {
        lock (Gate)
            _applied = null;
    }
}

/// <summary>
/// Everything the search reads, passed in rather than reached for, so that the interesting cases —
/// a CUDA 12 layout, a cuDNN zip left unextracted, one half present and the other absent — can be
/// set up as data instead of as an install.
/// </summary>
/// <param name="PathDirectories">The directories already on <c>PATH</c>.</param>
/// <param name="CudaRoots">Where to start looking for CUDA, in order of preference.</param>
/// <param name="CudnnRoots">Where to start looking for cuDNN, in order of preference.</param>
/// <param name="FileExists">Whether a directory holds a named file.</param>
/// <param name="Subdirectories">A directory's immediate children; empty when it does not exist.</param>
/// <param name="TensorRtRoots">Where to start looking for TensorRT 10; none means it is not looked for.</param>
public sealed record CudaSearch(
    IReadOnlyList<string> PathDirectories,
    IReadOnlyList<string> CudaRoots,
    IReadOnlyList<string> CudnnRoots,
    Func<string, string, bool> FileExists,
    Func<string, IReadOnlyList<string>> Subdirectories,
    IReadOnlyList<string>? TensorRtRoots = null);
