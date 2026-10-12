using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ManualForge.Core.Ocr;
using ManualForge.Core.Setup;

namespace ManualForge.Core.Tests;

/// <summary>
/// `manualforge setup gpu` (#26): NVIDIA's libraries fetched by manifest, checked by SHA-256, reduced
/// to their DLLs, and taken away again. Nothing here touches the network: the manifests are cut-down
/// copies of NVIDIA's own, and the archives are built in the test.
/// </summary>
public sealed class GpuLibrarySetupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "manualforge-gpu-" + Guid.NewGuid().ToString("N"));

    public GpuLibrarySetupTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // The shape of redistrib_13.4.2.json, as NVIDIA publishes it: sizes are strings.
    private const string CudaManifest = """
        {
          "release_label": "13.4.2",
          "cuda_cudart": {
            "name": "CUDA Runtime (cudart)", "license": "CUDA Toolkit", "license_path": "cuda_cudart/LICENSE.txt",
            "version": "13.4.92",
            "windows-x86_64": { "relative_path": "cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-13.4.92-archive.zip",
                                "sha256": "621A70C47782", "size": "2737397" },
            "windows-arm64": { "relative_path": "cuda_cudart/windows-arm64/x.zip", "sha256": "d3", "size": "1" }
          },
          "libcublas": {
            "name": "CUDA cuBLAS", "license": "CUDA Toolkit", "license_path": "libcublas/LICENSE.txt",
            "version": "13.8.0.4",
            "windows-x86_64": { "relative_path": "libcublas/windows-x86_64/libcublas-windows-x86_64-13.8.0.4-archive.zip",
                                "sha256": "0974318e9861", "size": "422415931" }
          }
        }
        """;

    private const string CudnnManifest = """
        {
          "cudnn": {
            "name": "NVIDIA CUDA Deep Neural Network library", "license": "cudnn", "license_path": "cudnn/LICENSE.txt",
            "version": "9.26.0.51",
            "windows-x86_64": {
              "cuda12": { "relative_path": "cudnn/windows-x86_64/cudnn-windows-x86_64-9.26.0.51_cuda12-archive.zip", "sha256": "b9bb", "size": "1901575860" },
              "cuda13": { "relative_path": "cudnn/windows-x86_64/cudnn-windows-x86_64-9.26.0.51_cuda13-archive.zip", "sha256": "f861", "size": "1279656673" }
            }
          }
        }
        """;

    [Fact]
    public void TheCudaManifestGivesEachComponentsWindowsArchive()
    {
        using var manifest = JsonDocument.Parse(CudaManifest);
        var packages = GpuLibrarySetup.FromCudaManifest(manifest, GpuLibraryPins.CudaRedist, ["cuda_cudart", "libcublas"]).ToList();

        var cudart = packages[0];
        Assert.Equal("13.4.92", cudart.Version);
        Assert.Equal("cuda", cudart.Folder);
        Assert.Equal(
            "https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-13.4.92-archive.zip",
            cudart.Url.ToString());
        Assert.Equal("621a70c47782", cudart.Sha256);
        Assert.Equal(2_737_397, cudart.Size);
        Assert.Equal("https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/LICENSE.txt", cudart.Licence.ToString());
        Assert.Equal("libcublas-windows-x86_64-13.8.0.4-archive.zip", packages[1].FileName);
    }

    [Fact]
    public void ACudaComponentMissingFromTheManifestIsAnError()
    {
        using var manifest = JsonDocument.Parse(CudaManifest);
        Assert.Throws<InvalidDataException>(
            () => GpuLibrarySetup.FromCudaManifest(manifest, GpuLibraryPins.CudaRedist, ["cuda_nvrtc"]).ToList());
    }

    [Fact]
    public void CudnnIsTheBuildForCuda13()
    {
        using var manifest = JsonDocument.Parse(CudnnManifest);
        var cudnn = GpuLibrarySetup.FromCudnnManifest(manifest, GpuLibraryPins.CudnnRedist, "cuda13");

        Assert.Equal("9.26.0.51", cudnn.Version);
        Assert.Equal("cudnn", cudnn.Folder);
        Assert.EndsWith("_cuda13-archive.zip", cudnn.Url.ToString(), StringComparison.Ordinal);
        Assert.Equal(1_279_656_673, cudnn.Size);
        Assert.Throws<InvalidDataException>(() => GpuLibrarySetup.FromCudnnManifest(manifest, GpuLibraryPins.CudnnRedist, "cuda14"));
    }

    [Fact]
    public void TheTensorRtWheelComesWithItsHashOrNotAtAll()
    {
        const string page = """
            <a href="tensorrt_cu13_libs-10.15.0.1-py3-none-win_amd64.whl#sha256=1111111111111111111111111111111111111111111111111111111111111111">old</a>
            <a href="tensorrt_cu13_libs-10.16.1.11-py3-none-manylinux_2_28_x86_64.whl#sha256=2222222222222222222222222222222222222222222222222222222222222222">linux</a>
            <a href="tensorrt_cu13_libs-10.16.1.11-py3-none-win_amd64.whl#sha256=96262C3E8C64A45ABD29AA3482D99480FAC6845BD420B6DE125699BB1AE365FF">windows</a>
            """;

        var wheel = GpuLibrarySetup.FromTensorRtIndex(page, GpuLibraryPins.TensorRtIndex, "10.16.1.11");

        Assert.Equal("tensorrt", wheel.Folder);
        Assert.Equal("https://pypi.nvidia.com/tensorrt-cu13-libs/tensorrt_cu13_libs-10.16.1.11-py3-none-win_amd64.whl", wheel.Url.ToString());
        Assert.Equal("96262c3e8c64a45abd29aa3482d99480fac6845bd420b6de125699bb1ae365ff", wheel.Sha256);

        const string unhashed = """<a href="tensorrt_cu13_libs-10.16.1.11-py3-none-win_amd64.whl">windows</a>""";
        Assert.Throws<InvalidDataException>(() => GpuLibrarySetup.FromTensorRtIndex(unhashed, GpuLibraryPins.TensorRtIndex, "10.16.1.11"));
        Assert.Throws<InvalidDataException>(() => GpuLibrarySetup.FromTensorRtIndex(page, GpuLibraryPins.TensorRtIndex, "10.17.0.0"));
    }

    [Theory]
    [InlineData("libcublas-windows-x86_64-13.8.0.4-archive/bin/x64/cublasLt64_13.dll", true)]
    [InlineData("cudnn-windows-x86_64-9.26.0.51_cuda13-archive/bin/x64/cudnn64_9.dll", true)]
    [InlineData("tensorrt_libs/nvinfer_10.dll", true)]
    [InlineData("libcublas-windows-arm64-13.8.0.4-archive/bin/arm64/cublasLt64_13.dll", false)]
    [InlineData("libcublas-windows-x86_64-13.8.0.4-archive/lib/x64/cublas.lib", false)]
    [InlineData("libcublas-windows-x86_64-13.8.0.4-archive/include/cublas.h", false)]
    [InlineData("libcublas-windows-x86_64-13.8.0.4-archive/LICENSE", false)]
    [InlineData("tensorrt_libs/__init__.py", false)]
    [InlineData("tensorrt_cu13_libs-10.16.1.11.dist-info/RECORD", false)]
    public void OnlyTheX64DllsAreKept(string entry, bool kept) => Assert.Equal(kept, GpuLibrarySetup.Keep(entry));

    // ---------------------------------------------------------------- installing, against a fake NVIDIA

    /// <summary>Serves the given archives by URL and counts what was asked for.</summary>
    private sealed class FakeNvidia(Dictionary<Uri, byte[]> files) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(files.TryGetValue(request.RequestUri!, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static byte[] Archive(params (string Path, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        }

        return memory.ToArray();
    }

    private static GpuLibraryPackage Package(string name, string folder, string file, byte[] bytes, string? sha = null) => new(
        name, "1.0", folder, new Uri("https://example.test/" + file),
        sha ?? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length,
        new Uri("https://example.test/LICENSE.txt"));

    private (GpuLibrarySetup Setup, FakeNvidia Nvidia, List<GpuLibraryPackage> Packages) Fixture(string? badSha = null)
    {
        var cudart = Archive(("cuda_cudart-archive/bin/x64/cudart64_13.dll", "cudart"), ("cuda_cudart-archive/include/cuda.h", "header"));
        var cublas = Archive(
            ("libcublas-archive/bin/x64/cublas64_13.dll", "cublas"),
            ("libcublas-archive/bin/x64/cublasLt64_13.dll", "cublasLt"),
            ("libcublas-archive/lib/x64/cublas.lib", "import library"));
        var cudnn = Archive(("cudnn-archive/bin/x64/cudnn64_9.dll", "cudnn"), ("cudnn-archive/bin/x64/cudnn_graph64_9.dll", "graph"));

        var packages = new List<GpuLibraryPackage>
        {
            Package("cudart", "cuda", "cudart.zip", cudart),
            Package("cublas", "cuda", "cublas.zip", cublas),
            Package("cudnn", "cudnn", "cudnn.zip", cudnn, badSha),
        };

        var nvidia = new FakeNvidia(new()
        {
            [packages[0].Url] = cudart,
            [packages[1].Url] = cublas,
            [packages[2].Url] = cudnn,
        });

        return (new GpuLibrarySetup(new HttpClient(nvidia), Path.Combine(_directory, "gpu")), nvidia, packages);
    }

    [Fact]
    public async Task TheDllsLandFlatInTheirFoldersAndNothingElseDoes()
    {
        var (setup, _, packages) = Fixture();
        var downloads = Path.Combine(_directory, "downloads");

        await setup.InstallAsync(packages, downloads, keepDownloads: false, progress: null, CancellationToken.None);

        Assert.Equal(
            ["cublas64_13.dll", "cublasLt64_13.dll", "cudart64_13.dll"],
            Directory.GetFiles(Path.Combine(setup.Root, "cuda")).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            ["cudnn64_9.dll", "cudnn_graph64_9.dll"],
            Directory.GetFiles(Path.Combine(setup.Root, "cudnn")).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray());

        // What the app's own search will find there, with nothing on PATH.
        var location = CudaLibraries.Locate(new CudaSearch(
            PathDirectories: [],
            CudaRoots: [Path.Combine(setup.Root, "cuda")],
            CudnnRoots: [Path.Combine(setup.Root, "cudnn")],
            FileExists: (directory, file) => File.Exists(Path.Combine(directory, file)),
            Subdirectories: directory => Directory.Exists(directory) ? Directory.GetDirectories(directory) : []));
        Assert.True(location.Complete);

        Assert.True(setup.IsCurrent(packages));
        Assert.Equal(3, setup.Installed().Count);
        Assert.Empty(Directory.GetFiles(downloads));   // archives go once their DLLs are in place
    }

    [Fact]
    public async Task OnlyAFolderThatIsNotCurrentIsFetchedAgain()
    {
        var (setup, _, packages) = Fixture();
        await setup.InstallAsync(packages, Path.Combine(_directory, "downloads"), false, null, CancellationToken.None);
        Assert.Empty(setup.NotCurrent(packages));

        // A newer cuDNN: CUDA's two archives are current and stay; cuDNN alone is fetched.
        var newer = Archive(("cudnn-archive/bin/x64/cudnn64_9.dll", "cudnn, newer"));
        var updated = packages.Take(2).Append(Package("cudnn", "cudnn", "cudnn-newer.zip", newer)).ToList();
        Assert.Equal(["cudnn"], setup.NotCurrent(updated).Select(p => p.Name));

        // The record keeps CUDA's entries when only cuDNN is installed again.
        var nvidia = new FakeNvidia(new() { [updated[2].Url] = newer });
        await new GpuLibrarySetup(new HttpClient(nvidia), setup.Root)
            .InstallAsync(setup.NotCurrent(updated), Path.Combine(_directory, "downloads"), false, null, CancellationToken.None);
        Assert.Single(nvidia.Requested);
        Assert.True(setup.IsCurrent(updated));
    }

    [Fact]
    public async Task ADownloadThatFailsItsHashInstallsNothing()
    {
        var (setup, _, packages) = Fixture(badSha: new string('0', 64));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            setup.InstallAsync(packages, Path.Combine(_directory, "downloads"), false, null, CancellationToken.None));

        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(setup.Root, "cuda")));   // checked before anything is extracted
        Assert.False(Directory.Exists(Path.Combine(setup.Root, "cudnn")));
        Assert.Empty(setup.Installed());
    }

    [Fact]
    public async Task ArchivesKeptInADownloadsFolderAreReusedNotFetchedAgain()
    {
        var (setup, nvidia, packages) = Fixture();
        var downloads = Path.Combine(_directory, "kept");

        await setup.InstallAsync(packages, downloads, keepDownloads: true, null, CancellationToken.None);
        Assert.Equal(3, nvidia.Requested.Count);

        await setup.InstallAsync(packages, downloads, keepDownloads: true, null, CancellationToken.None);
        Assert.Equal(3, nvidia.Requested.Count);
        Assert.Equal(3, Directory.GetFiles(downloads).Length);
    }

    [Fact]
    public async Task RemovingTakesAwayWhatSetupPutThereAndNothingElse()
    {
        var (setup, _, packages) = Fixture();
        await setup.InstallAsync(packages, Path.Combine(_directory, "downloads"), false, null, CancellationToken.None);
        var unrelated = Path.Combine(setup.Root, "notes.txt");
        File.WriteAllText(unrelated, "someone else's");

        var freed = setup.Remove().BytesFreed;

        Assert.True(freed > 0);
        Assert.False(Directory.Exists(Path.Combine(setup.Root, "cuda")));
        Assert.False(Directory.Exists(Path.Combine(setup.Root, "cudnn")));
        Assert.Empty(setup.Installed());
        Assert.True(File.Exists(unrelated));

        File.Delete(unrelated);
        await setup.InstallAsync(packages, Path.Combine(_directory, "downloads"), false, null, CancellationToken.None);
        setup.Remove();
        Assert.False(Directory.Exists(setup.Root));   // the root goes too once nothing else is in it
    }

    [Fact]
    public async Task AFileStillInUseIsNamedNotThrown()
    {
        // 11 Oct: a DLL loaded a moment before stayed locked, and removal stopped partway with an error.
        var (fixture, _, packages) = Fixture();
        var setup = new GpuLibrarySetup(new HttpClient(new FakeNvidia([])), fixture.Root) { RetryDelay = TimeSpan.FromMilliseconds(1) };
        await fixture.InstallAsync(packages, Path.Combine(_directory, "downloads"), false, null, CancellationToken.None);
        var locked = Path.Combine(setup.Root, "cuda", "cublasLt64_13.dll");

        GpuLibraryRemoval removal;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            removal = setup.Remove();

        Assert.Equal([locked], removal.Left);
        Assert.True(removal.BytesFreed > 0);
        Assert.False(File.Exists(Path.Combine(setup.Root, "cuda", "cudart64_13.dll")));   // the rest still went
        Assert.NotEmpty(setup.Installed());   // the record stays until everything is gone

        Assert.Empty(setup.Remove().Left);   // and a second try, with nothing holding it, finishes
        Assert.False(Directory.Exists(setup.Root));
    }

    [Theory]
    [InlineData("| NVIDIA-SMI 617.42                KMD Version: 617.42        CUDA UMD Version: 13.4     |", 13, 4)]
    [InlineData("| NVIDIA-SMI 560.94                 Driver Version: 560.94         CUDA Version: 12.6     |", 12, 6)]
    public void TheDriversCudaVersionIsReadFromEitherHeader(string header, int major, int minor)
        => Assert.Equal(new Version(major, minor), GpuMemoryProbe.ParseDriverCudaVersion(header));

    [Fact]
    public void NoHeaderNoVersion()
    {
        Assert.Null(GpuMemoryProbe.ParseDriverCudaVersion(null));
        Assert.Null(GpuMemoryProbe.ParseDriverCudaVersion("No devices were found"));
    }
}
