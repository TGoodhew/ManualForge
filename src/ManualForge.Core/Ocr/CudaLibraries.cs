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
public sealed record CudaLibraryLocation(
    string? CudaDirectory,
    string? CudnnDirectory,
    IReadOnlyList<string> Missing,
    bool AlreadyOnPath)
{
    public bool Complete => Missing.Count == 0;

    /// <summary>A line for the log and the <c>gpu</c> command. Never null, never a stack trace.</summary>
    public string Describe()
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

            if (!location.AlreadyOnPath)
            {
                var added = new[] { location.CudaDirectory, location.CudnnDirectory }
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
                    + "times slower. Install CUDA 13 and cuDNN 9 for CUDA 13, or pass --accelerator cpu "
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

        if (RequiredLibraries.All(library => ResolvableIn(search.PathDirectories, library, search)))
            return new CudaLibraryLocation(null, null, [], AlreadyOnPath: true);

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

        return new CudaLibraryLocation(cuda, cudnn, missing, AlreadyOnPath: false);
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
                .OrderByDescending(child => child, StringComparer.OrdinalIgnoreCase);

            foreach (var child in children)
                queue.Enqueue((child, depth + 1));
        }

        return null;
    }

    /// <summary>
    /// The real machine: <c>PATH</c>, the environment variables the CUDA installer sets, and the
    /// places these two things are conventionally put.
    /// </summary>
    private static CudaSearch FromEnvironment()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var toolkit = Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA");

        // CUDA_PATH points at the active install and CUDA_PATH_V13_4 and friends at every install
        // present. Taking them first means an explicitly selected toolkit beats whatever happens to
        // be newest on disk.
        var cudaRoots = new List<string>();
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

        var cudnnRoots = new List<string>();
        if (Environment.GetEnvironmentVariable("CUDNN_PATH") is { Length: > 0 } cudnnPath)
            cudnnRoots.Add(cudnnPath);
        cudnnRoots.Add(Path.Combine(programFiles, "NVIDIA", "CUDNN"));
        cudnnRoots.Add(Path.Combine(Path.GetPathRoot(programFiles) ?? @"C:\", "Tools"));

        // Some installs copy cuDNN in beside the CUDA binaries, so the toolkit is worth a look too.
        cudnnRoots.AddRange(cudaRoots);

        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return new CudaSearch(
            PathDirectories: pathDirectories,
            CudaRoots: cudaRoots,
            CudnnRoots: cudnnRoots,
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
public sealed record CudaSearch(
    IReadOnlyList<string> PathDirectories,
    IReadOnlyList<string> CudaRoots,
    IReadOnlyList<string> CudnnRoots,
    Func<string, string, bool> FileExists,
    Func<string, IReadOnlyList<string>> Subdirectories);
