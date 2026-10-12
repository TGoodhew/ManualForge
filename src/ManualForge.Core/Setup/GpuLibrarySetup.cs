using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ManualForge.Core.Ocr;

namespace ManualForge.Core.Setup;

/// <summary>
/// The NVIDIA libraries the GPU path loads, at the versions every measurement was made with (#26).
///
/// <para>
/// Downloaded from NVIDIA, never carried: the installer stays small, and nothing NVIDIA licenses is
/// redistributed by us. Each comes from NVIDIA's per-component archives, whose manifests publish a
/// SHA-256 for every file, rather than from the toolkit installer, which needs administrator rights and
/// can replace a newer display driver with an older one. The archives hold the toolkit's own DLLs, byte
/// for byte, and NVRTC is not among them because nothing loads it
/// (docs/measurements/installer-gpu-libraries.md).
/// </para>
/// </summary>
public static class GpuLibraryPins
{
    public static readonly Uri CudaRedist = new("https://developer.download.nvidia.com/compute/cuda/redist/");

    /// <summary>The CUDA release whose manifest names the archives: cudart 13.4.92, cuBLAS 13.8.0.4.</summary>
    public const string CudaManifestVersion = "13.4.2";

    /// <summary>What ONNX Runtime's CUDA provider imports: the runtime, and cuBLAS with cuBLASLt.</summary>
    public static readonly IReadOnlyList<string> CudaComponents = ["cuda_cudart", "libcublas"];

    public static readonly Uri CudnnRedist = new("https://developer.download.nvidia.com/compute/cudnn/redist/");

    /// <summary>cuDNN 9.26.0.51, the build for CUDA 13.</summary>
    public const string CudnnManifestVersion = "9.26.0";

    public const string CudnnVariant = "cuda13";

    /// <summary>NVIDIA's own package index, which links each wheel with its SHA-256.</summary>
    public static readonly Uri TensorRtIndex = new("https://pypi.nvidia.com/tensorrt-cu13-libs/");

    public const string TensorRtVersion = "10.16.1.11";

    public static readonly Uri TensorRtLicence =
        new("https://docs.nvidia.com/deeplearning/tensorrt/latest/reference/sla.html");

    /// <summary>The oldest CUDA the display driver must support: everything here is built for CUDA 13.</summary>
    public static readonly Version MinimumDriverCuda = new(13, 0);
}

/// <summary>One archive, as NVIDIA publishes it, and the folder its DLLs go to.</summary>
/// <param name="Folder"><c>cuda</c>, <c>cudnn</c> or <c>tensorrt</c>, under the libraries' root.</param>
/// <param name="Licence">Where NVIDIA publishes the licence it comes under.</param>
public sealed record GpuLibraryPackage(
    string Name, string Version, string Folder, Uri Url, string Sha256, long? Size, Uri Licence)
{
    public string FileName => Url.Segments[^1];
}

/// <summary>A package's DLLs as they stand on disk, recorded so a later run knows what is there.</summary>
public sealed record InstalledGpuLibrary(string Name, string Version, string Folder, string Sha256);

/// <summary>What removing freed, and any file that could not be deleted.</summary>
public sealed record GpuLibraryRemoval(long BytesFreed, IReadOnlyList<string> Left);

/// <summary>How far a download has got.</summary>
public sealed record GpuDownloadProgress(string Name, long Received, long? Total);

/// <summary>
/// Fetches the pinned NVIDIA libraries into <see cref="CudaLibraries.ChainedLibrariesRoot"/>, checks
/// each against its published SHA-256, keeps only the DLLs, and takes them away again.
/// </summary>
public sealed class GpuLibrarySetup(HttpClient http, string root)
{
    private const string RecordFile = "installed.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The folders this owns under <see cref="Root"/>, and nothing else.</summary>
    public static readonly IReadOnlyList<string> Folders = ["cuda", "cudnn", "tensorrt"];

    public string Root { get; } = root;

    /// <summary>The packages to fetch, read from NVIDIA's manifests and index.</summary>
    public async Task<IReadOnlyList<GpuLibraryPackage>> ResolveAsync(bool includeTensorRt, CancellationToken cancellationToken)
    {
        var packages = new List<GpuLibraryPackage>();

        using (var cuda = JsonDocument.Parse(await http.GetStringAsync(
                   new Uri(GpuLibraryPins.CudaRedist, $"redistrib_{GpuLibraryPins.CudaManifestVersion}.json"), cancellationToken)))
        {
            packages.AddRange(FromCudaManifest(cuda, GpuLibraryPins.CudaRedist, GpuLibraryPins.CudaComponents));
        }

        using (var cudnn = JsonDocument.Parse(await http.GetStringAsync(
                   new Uri(GpuLibraryPins.CudnnRedist, $"redistrib_{GpuLibraryPins.CudnnManifestVersion}.json"), cancellationToken)))
        {
            packages.Add(FromCudnnManifest(cudnn, GpuLibraryPins.CudnnRedist, GpuLibraryPins.CudnnVariant));
        }

        if (includeTensorRt)
        {
            var index = await http.GetStringAsync(GpuLibraryPins.TensorRtIndex, cancellationToken);
            packages.Add(FromTensorRtIndex(index, GpuLibraryPins.TensorRtIndex, GpuLibraryPins.TensorRtVersion));
        }

        return packages;
    }

    /// <summary>The Windows x64 archive of each named component in a CUDA redistributable manifest.</summary>
    internal static IEnumerable<GpuLibraryPackage> FromCudaManifest(JsonDocument manifest, Uri baseUri, IEnumerable<string> components)
    {
        foreach (var component in components)
        {
            if (!manifest.RootElement.TryGetProperty(component, out var entry))
                throw new InvalidDataException($"The CUDA manifest has no {component}.");

            yield return Package(entry, entry.GetProperty("windows-x86_64"), "cuda", baseUri);
        }
    }

    /// <summary>cuDNN's Windows x64 archive for one CUDA major version.</summary>
    internal static GpuLibraryPackage FromCudnnManifest(JsonDocument manifest, Uri baseUri, string variant)
    {
        if (!manifest.RootElement.TryGetProperty("cudnn", out var entry))
            throw new InvalidDataException("The cuDNN manifest has no cudnn.");

        var windows = entry.GetProperty("windows-x86_64");
        if (!windows.TryGetProperty(variant, out var build))
            throw new InvalidDataException($"The cuDNN manifest has no Windows build for {variant}.");

        return Package(entry, build, "cudnn", baseUri);
    }

    private static GpuLibraryPackage Package(JsonElement entry, JsonElement archive, string folder, Uri baseUri)
    {
        var size = archive.TryGetProperty("size", out var s)
            ? s.ValueKind == JsonValueKind.Number ? s.GetInt64()
              : long.TryParse(s.GetString(), out var parsed) ? parsed : (long?)null
            : null;

        return new GpuLibraryPackage(
            entry.GetProperty("name").GetString() ?? folder,
            entry.GetProperty("version").GetString() ?? "?",
            folder,
            new Uri(baseUri, archive.GetProperty("relative_path").GetString()),
            archive.GetProperty("sha256").GetString()!.ToLowerInvariant(),
            size,
            new Uri(baseUri, entry.GetProperty("license_path").GetString()));
    }

    /// <summary>
    /// The TensorRT wheel for this version, from NVIDIA's package index page, which links each file with
    /// <c>#sha256=</c> and its hash. A wheel listed without one is refused: it could not be checked.
    /// </summary>
    internal static GpuLibraryPackage FromTensorRtIndex(string html, Uri index, string version)
    {
        var wheel = $"tensorrt_cu13_libs-{version}-py3-none-win_amd64.whl";
        var link = Regex.Match(html, $"href=\"(?<url>[^\"#]*{Regex.Escape(wheel)})#sha256=(?<sha>[0-9a-fA-F]{{64}})\"");
        if (!link.Success)
            throw new InvalidDataException($"{wheel} is not listed with a SHA-256 at {index}.");

        return new GpuLibraryPackage(
            "TensorRT", version, "tensorrt",
            new Uri(index, link.Groups["url"].Value),
            link.Groups["sha"].Value.ToLowerInvariant(),
            null,
            GpuLibraryPins.TensorRtLicence);
    }

    /// <summary>What a previous run left, or nothing.</summary>
    public IReadOnlyList<InstalledGpuLibrary> Installed()
    {
        try
        {
            var path = Path.Combine(Root, RecordFile);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<InstalledGpuLibrary>>(File.ReadAllText(path), Json) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Whether every one of these packages is already in place, as these exact archives.</summary>
    public bool IsCurrent(IEnumerable<GpuLibraryPackage> packages)
    {
        var installed = Installed().Select(i => i.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return packages.All(p => installed.Contains(p.Sha256)
                                 && Directory.Exists(Path.Combine(Root, p.Folder))
                                 && Directory.EnumerateFiles(Path.Combine(Root, p.Folder), "*.dll").Any());
    }

    /// <summary>
    /// The packages whose folder is not already in place as these exact archives. A folder is installed
    /// whole, so it is all or nothing: adding TensorRT to current CUDA and cuDNN downloads TensorRT
    /// alone, rather than 1.7 GB again (#78).
    /// </summary>
    public IReadOnlyList<GpuLibraryPackage> NotCurrent(IEnumerable<GpuLibraryPackage> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        return packages.GroupBy(p => p.Folder).Where(folder => !IsCurrent(folder)).SelectMany(folder => folder).ToArray();
    }

    /// <summary>
    /// Downloads each package into <paramref name="downloads"/>, or reuses one already there with the
    /// right hash, checks it, and puts its DLLs in place. A folder is replaced whole, and only once
    /// every archive for it has been checked and extracted, so a failure leaves the last good set.
    /// </summary>
    public async Task InstallAsync(
        IReadOnlyList<GpuLibraryPackage> packages,
        string downloads,
        bool keepDownloads,
        IProgress<GpuDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packages);
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(Root);

        var archives = new Dictionary<GpuLibraryPackage, string>();
        foreach (var package in packages)
            archives[package] = await FetchAsync(package, downloads, progress, cancellationToken).ConfigureAwait(false);

        foreach (var folder in packages.GroupBy(p => p.Folder))
        {
            var target = Path.Combine(Root, folder.Key);
            var partial = target + ".partial";
            if (Directory.Exists(partial))
                Directory.Delete(partial, recursive: true);
            Directory.CreateDirectory(partial);

            foreach (var package in folder)
                ExtractDlls(archives[package], partial);

            var previous = target + ".previous";
            if (Directory.Exists(previous))
                Directory.Delete(previous, recursive: true);
            if (Directory.Exists(target))
                Directory.Move(target, previous);
            Directory.Move(partial, target);
            if (Directory.Exists(previous))
                Directory.Delete(previous, recursive: true);
        }

        // Kept beside the libraries: which archives they came from, so a re-run knows they are current
        // and an update knows what it is replacing. A folder not fetched this time keeps its entry.
        var record = Installed()
            .Where(i => !packages.Any(p => p.Folder == i.Folder))
            .Concat(packages.Select(p => new InstalledGpuLibrary(p.Name, p.Version, p.Folder, p.Sha256)))
            .ToList();
        File.WriteAllText(Path.Combine(Root, RecordFile), JsonSerializer.Serialize(record, Json));

        if (!keepDownloads)
        {
            foreach (var archive in archives.Values)
                TryDelete(archive);
        }
    }

    private async Task<string> FetchAsync(
        GpuLibraryPackage package, string downloads, IProgress<GpuDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var path = Path.Combine(downloads, package.FileName);
        if (File.Exists(path) && string.Equals(await Sha256Of(path, cancellationToken), package.Sha256, StringComparison.OrdinalIgnoreCase))
            return path;

        var partial = path + ".part";
        using (var response = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? package.Size;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = File.Create(partial);
            var buffer = new byte[1 << 20];
            long received = 0;
            var reported = 0L;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                if (received - reported >= 16 << 20)
                {
                    reported = received;
                    progress?.Report(new GpuDownloadProgress(package.Name, received, total));
                }
            }

            progress?.Report(new GpuDownloadProgress(package.Name, received, total));
        }

        var actual = await Sha256Of(partial, cancellationToken);
        if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(partial);
            throw new InvalidDataException(
                $"{package.FileName} does not match the SHA-256 NVIDIA publishes for it ({actual} against {package.Sha256}). Nothing was installed.");
        }

        File.Move(partial, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Whether an archive entry is a DLL ManualForge's GPU path can load: an x64 library from a CUDA or
    /// cuDNN archive's <c>bin/x64</c>, or one of the TensorRT wheel's. Headers, import libraries,
    /// licences and other architectures are left in the archive.
    /// </summary>
    internal static bool Keep(string entryPath)
    {
        var path = entryPath.Replace('\\', '/');
        if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.Contains("arm64", StringComparison.OrdinalIgnoreCase) || path.Contains("aarch64", StringComparison.OrdinalIgnoreCase))
            return false;

        return path.Contains("/bin/x64/", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("tensorrt_libs/", StringComparison.OrdinalIgnoreCase);
    }

    internal static int ExtractDlls(string archive, string folder)
    {
        var count = 0;
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries.Where(e => Keep(e.FullName)))
        {
            entry.ExtractToFile(Path.Combine(folder, entry.Name), overwrite: true);
            count++;
        }

        if (count == 0)
            throw new InvalidDataException($"{Path.GetFileName(archive)} holds no DLLs where they were expected.");

        return count;
    }

    /// <summary>
    /// Takes away what this put here: the three library folders and the record of them. The root goes
    /// too if nothing else is in it.
    ///
    /// <para>
    /// It does not stop at a file it cannot delete. A DLL loaded a moment ago can stay locked for a
    /// second or two after its process ends, while the virus scanner looks at it, which is how the
    /// first try on 11 October failed partway. Each file is tried a few times, and whatever is still
    /// there at the end is named rather than thrown, so an uninstaller can say what is left.
    /// </para>
    /// </summary>
    public GpuLibraryRemoval Remove()
    {
        if (!Directory.Exists(Root))
            return new GpuLibraryRemoval(0, []);

        long freed = 0;
        var left = new List<string>();
        foreach (var folder in Folders.SelectMany(f => new[] { f, f + ".partial", f + ".previous" }))
        {
            var path = Path.Combine(Root, folder);
            if (!Directory.Exists(path))
                continue;

            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).ToList())
            {
                var length = file.Length;
                if (DeleteWithRetries(file.FullName))
                    freed += length;
                else
                    left.Add(file.FullName);
            }

            if (!Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).Any(File.Exists))
            {
                try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        if (left.Count == 0)
        {
            TryDelete(Path.Combine(Root, RecordFile));
            if (!Directory.EnumerateFileSystemEntries(Root).Any())
                Directory.Delete(Root);
        }

        return new GpuLibraryRemoval(freed, left);
    }

    /// <summary>How long to wait before trying a locked file again; shorter in tests.</summary>
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    private bool DeleteWithRetries(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(RetryDelay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private static async Task<string> Sha256Of(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
