using ManualForge.Core.Ocr;

namespace ManualForge.Core.Tests;

/// <summary>
/// The failure this guards against is silent. When the loader cannot find
/// <c>cublasLt64_13.dll</c>, ONNX Runtime does not throw — the CUDA provider declines to register,
/// the run falls back to CPU, and the only symptom is that it takes ten times longer. So the cases
/// worth testing are not "does it find a correct install", which is easy, but the wrong-looking
/// ones: a layout that moved between CUDA versions, an archive left one folder deeper than
/// expected, and half an installation.
///
/// Every case here is a fake directory tree. Nothing on the machine running these tests is read,
/// which is the point — no test machine has CUDA on it.
/// </summary>
public class CudaLibraryTests
{
    private const string CudaDll = "cublasLt64_13.dll";
    private const string CudnnDll = "cudnn64_9.dll";

    /// <summary>
    /// A directory tree described as a map from directory to the files in it. Directories with no
    /// files of interest still need to appear, because the search walks through them to reach the
    /// ones that matter — which is the whole difficulty being modelled.
    /// </summary>
    private sealed class FakeTree
    {
        private readonly Dictionary<string, HashSet<string>> _files = new(StringComparer.OrdinalIgnoreCase);

        public FakeTree Add(string directory, params string[] files)
        {
            if (!_files.TryGetValue(directory, out var set))
                _files[directory] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
                set.Add(file);

            // Every ancestor exists too, or the walk down to this directory cannot happen.
            for (var parent = Parent(directory); parent is not null; parent = Parent(parent))
            {
                if (!_files.ContainsKey(parent))
                    _files[parent] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return this;
        }

        public bool FileExists(string directory, string file)
            => _files.TryGetValue(directory, out var set) && set.Contains(file);

        public IReadOnlyList<string> Subdirectories(string directory)
            => _files.Keys
                .Where(candidate => string.Equals(Parent(candidate), directory, StringComparison.OrdinalIgnoreCase))
                .ToList();

        private static string? Parent(string directory)
        {
            var index = directory.LastIndexOf('\\');
            return index <= 2 ? null : directory[..index];
        }
    }

    private static CudaSearch Search(
        FakeTree tree,
        IEnumerable<string>? path = null,
        IEnumerable<string>? cudaRoots = null,
        IEnumerable<string>? cudnnRoots = null)
        => new(
            PathDirectories: (path ?? []).ToList(),
            CudaRoots: (cudaRoots ?? ["C:\\Program Files\\NVIDIA GPU Computing Toolkit\\CUDA"]).ToList(),
            CudnnRoots: (cudnnRoots ?? ["C:\\Tools"]).ToList(),
            FileExists: tree.FileExists,
            Subdirectories: tree.Subdirectories);

    /// <summary>A complete, correctly laid out CUDA 13 and cuDNN 9 install.</summary>
    private static FakeTree CompleteInstall() => new FakeTree()
        .Add(
            "C:\\Program Files\\NVIDIA GPU Computing Toolkit\\CUDA\\v13.4\\bin\\x64",
            "cublas64_13.dll", "cublasLt64_13.dll", "cudart64_13.dll")
        .Add(
            "C:\\Tools\\cudnn-9.26.0.51\\cudnn-windows-x86_64-9.26.0.51_cuda13-archive\\bin\\x64",
            "cudnn64_9.dll", "cudnn_graph64_9.dll");

    [Fact]
    public void AnEnvironmentThatIsAlreadyRightIsLeftAlone()
    {
        // The ordinary case on a machine set up by hand. It matters that this is distinguishable
        // from a successful search: one says the environment is correct, the other says we
        // rescued it, and only the second is worth telling anybody about.
        var tree = CompleteInstall();
        var path = new[]
        {
            "C:\\Program Files\\NVIDIA GPU Computing Toolkit\\CUDA\\v13.4\\bin\\x64",
            "C:\\Tools\\cudnn-9.26.0.51\\cudnn-windows-x86_64-9.26.0.51_cuda13-archive\\bin\\x64",
        };

        var location = CudaLibraries.Locate(Search(tree, path));

        Assert.True(location.AlreadyOnPath);
        Assert.True(location.Complete);
        Assert.Null(location.CudaDirectory);
        Assert.Null(location.CudnnDirectory);
        Assert.Equal("already on PATH", location.Describe());
    }

    [Fact]
    public void ItFindsTheInnerBinX64ThatCuda13MovedTo()
    {
        // CUDA 12.x put its Windows DLLs directly in bin; 13 moved them into bin\x64 and added an
        // arm64 sibling. Nothing on the machine says so, and the parent directory still exists,
        // so a path written down from the old layout points somewhere real and empty.
        var location = CudaLibraries.Locate(Search(CompleteInstall()));

        Assert.False(location.AlreadyOnPath);
        Assert.True(location.Complete);
        Assert.Equal(
            "C:\\Program Files\\NVIDIA GPU Computing Toolkit\\CUDA\\v13.4\\bin\\x64",
            location.CudaDirectory);
    }

    [Fact]
    public void TheArm64CopyOfTheSameLibrariesIsNotAnAnswer()
    {
        // CUDA 13 installs an arm64 build of every one of these DLLs beside the x64 build, under
        // the same file names, and on a real 13.4 install a plain recursive search finds arm64
        // first. Handing an x64 process those fails exactly the way a missing file does: the
        // provider declines, nothing throws, and the work runs on the CPU at a tenth of the speed.
        //
        // Only x64 is offered here, so a search that has no opinion about architecture returns the
        // wrong directory and this test fails. Descending name order used to make it come out
        // right by luck.
        var tree = new FakeTree()
            .Add(
                "C:\\Program Files\\NVIDIA GPU Computing Toolkit\\CUDA\\v13.4\\bin\\arm64",
                "cublas64_13.dll", "cublasLt64_13.dll", "cudart64_13.dll")
            .Add(
                "C:\\Tools\\cudnn-9.26.0.51\\cudnn-windows-x86_64-9.26.0.51_cuda13-archive\\bin\\arm64",
                "cudnn64_9.dll", "cudnn_graph64_9.dll");

        var location = CudaLibraries.Locate(Search(tree));

        Assert.False(location.Complete);
        Assert.Null(location.CudaDirectory);
        Assert.Null(location.CudnnDirectory);
    }

    [Fact]
    public void ItFindsCudnnThreeFoldersBelowWhereTheZipLands()
    {
        var location = CudaLibraries.Locate(Search(CompleteInstall()));

        Assert.Equal(
            "C:\\Tools\\cudnn-9.26.0.51\\cudnn-windows-x86_64-9.26.0.51_cuda13-archive\\bin\\x64",
            location.CudnnDirectory);
    }

    [Fact]
    public void TheWrongMajorVersionIsNotAnInstall()
    {
        // A CUDA 12 toolkit is a complete, working, entirely useless install: ONNX Runtime 1.30
        // imports the _13 libraries by name, so 12 cannot satisfy it however well it is set up.
        // Reporting "missing" here rather than pointing at v12.6 is the difference between a
        // person installing CUDA 13 and a person wondering why a directory that plainly exists is
        // being ignored.
        var tree = new FakeTree().Add(
            "C:\\Program Files\\NVIDIA GPU Computing Toolkit\\CUDA\\v12.6\\bin",
            "cublas64_12.dll", "cublasLt64_12.dll", "cudart64_12.dll");

        var location = CudaLibraries.Locate(Search(tree));

        Assert.Null(location.CudaDirectory);
        Assert.False(location.Complete);
        Assert.Contains("cublasLt64_13.dll", location.Missing);
    }

    [Fact]
    public void TheNewestInstallWinsWhenSeveralArePresent()
    {
        var tree = new FakeTree()
            .Add(
                "C:\\Program Files\\NVIDIA GPU Computing Toolkit\\CUDA\\v13.0\\bin\\x64",
                "cublas64_13.dll", "cublasLt64_13.dll", "cudart64_13.dll")
            .Add(
                "C:\\Program Files\\NVIDIA GPU Computing Toolkit\\CUDA\\v13.4\\bin\\x64",
                "cublas64_13.dll", "cublasLt64_13.dll", "cudart64_13.dll");

        var location = CudaLibraries.Locate(Search(tree));

        Assert.Equal(
            "C:\\Program Files\\NVIDIA GPU Computing Toolkit\\CUDA\\v13.4\\bin\\x64",
            location.CudaDirectory);
    }

    [Fact]
    public void AnExplicitlySelectedToolkitBeatsWhateverIsNewestOnDisk()
    {
        // CUDA_PATH is what the installer sets to mark the active toolkit, and somebody who has
        // pointed it at an older one has said something we should not quietly overrule.
        var tree = new FakeTree()
            .Add(
                "C:\\Program Files\\NVIDIA GPU Computing Toolkit\\CUDA\\v13.9\\bin\\x64",
                "cublas64_13.dll", "cublasLt64_13.dll", "cudart64_13.dll")
            .Add("D:\\cuda\\v13.4\\bin\\x64", "cublas64_13.dll", "cublasLt64_13.dll", "cudart64_13.dll");

        var location = CudaLibraries.Locate(Search(
            tree,
            cudaRoots: ["D:\\cuda\\v13.4", "C:\\Program Files\\NVIDIA GPU Computing Toolkit\\CUDA"]));

        Assert.Equal("D:\\cuda\\v13.4\\bin\\x64", location.CudaDirectory);
    }

    [Fact]
    public void HalfAnInstallSaysWhichHalfIsMissing()
    {
        // The two dependencies are downloaded separately and this is the likeliest state to be in
        // partway through setting a machine up. "Missing cuDNN" sends somebody to the right
        // download; "the GPU did not work" does not.
        var tree = new FakeTree().Add(
            "C:\\Program Files\\NVIDIA GPU Computing Toolkit\\CUDA\\v13.4\\bin\\x64",
            "cublas64_13.dll", "cublasLt64_13.dll", "cudart64_13.dll");

        var location = CudaLibraries.Locate(Search(tree));

        Assert.NotNull(location.CudaDirectory);
        Assert.Null(location.CudnnDirectory);
        Assert.False(location.Complete);
        Assert.Equal([CudnnDll, "cudnn_graph64_9.dll"], location.Missing);
        Assert.Contains("missing cudnn64_9.dll", location.Describe());
        Assert.Contains("CUDA at", location.Describe());
    }

    [Fact]
    public void OneHalfOnPathAndTheOtherFoundIsStillComplete()
    {
        // PATH and the search are not alternatives. A machine can carry a correct CUDA entry from
        // the installer and an unregistered cuDNN sitting in a folder, and that combination has to
        // come out complete or the search is just a second way to be wrong.
        var location = CudaLibraries.Locate(Search(
            CompleteInstall(),
            path: ["C:\\Program Files\\NVIDIA GPU Computing Toolkit\\CUDA\\v13.4\\bin\\x64"]));

        Assert.False(location.AlreadyOnPath);
        Assert.True(location.Complete);
        Assert.NotNull(location.CudnnDirectory);
    }

    [Fact]
    public void NothingInstalledIsReportedRatherThanGuessedAt()
    {
        var location = CudaLibraries.Locate(Search(new FakeTree()));

        Assert.Null(location.CudaDirectory);
        Assert.Null(location.CudnnDirectory);
        Assert.False(location.Complete);
        Assert.Equal(CudaLibraries.RequiredLibraries, location.Missing);
        Assert.StartsWith("missing ", location.Describe());
    }

    [Fact]
    public void ARootThatDoesNotExistIsNotAnError()
    {
        // C:\Tools is a convention, not a guarantee, and most machines will not have one.
        var tree = new FakeTree().Add(
            "C:\\Program Files\\NVIDIA GPU Computing Toolkit\\CUDA\\v13.4\\bin\\x64",
            "cublas64_13.dll", "cublasLt64_13.dll", "cudart64_13.dll");

        var location = CudaLibraries.Locate(Search(tree, cudnnRoots: ["Q:\\nowhere", "C:\\Tools"]));

        Assert.NotNull(location.CudaDirectory);
        Assert.Null(location.CudnnDirectory);
    }

    [Fact]
    public void ItDoesNotDescendForever()
    {
        // A root given by mistake — a drive root, a home directory — must cost a moment rather
        // than a walk of the volume. The bound is on depth, so a deep tree is abandoned rather
        // than searched, and the DLL planted at the bottom here is deliberately never found.
        var tree = new FakeTree();
        var deep = "C:\\Tools";
        for (var i = 0; i < 12; i++)
        {
            deep += "\\level" + i;
            tree.Add(deep);
        }

        tree.Add(deep, CudnnDll);

        var location = CudaLibraries.Locate(Search(tree));

        Assert.Null(location.CudnnDirectory);
    }

    [Fact]
    public void AShallowerAnswerWinsOverADeeperOne()
    {
        // Breadth-first, so a cuDNN unpacked straight into its root is preferred to one nested
        // inside a copy of the archive left behind beside it.
        var tree = new FakeTree()
            .Add("C:\\Tools\\cudnn\\bin\\x64", CudnnDll, "cudnn_graph64_9.dll")
            .Add("C:\\Tools\\cudnn\\bin\\x64\\old\\archive\\bin\\x64", CudnnDll, "cudnn_graph64_9.dll");

        var location = CudaLibraries.Locate(Search(tree));

        Assert.Equal("C:\\Tools\\cudnn\\bin\\x64", location.CudnnDirectory);
    }

    [Fact]
    public void TheDescriptionIsAlwaysSomethingAPersonCanActOn()
    {
        // This string goes in the log and in the gpu command's output, so it is the whole of what
        // a person gets when the GPU silently is not there.
        foreach (var location in new[]
                 {
                     CudaLibraries.Locate(Search(new FakeTree())),
                     CudaLibraries.Locate(Search(CompleteInstall())),
                     CudaLibraries.Locate(Search(CompleteInstall(), path:
                     [
                         "C:\\Program Files\\NVIDIA GPU Computing Toolkit\\CUDA\\v13.4\\bin\\x64",
                         "C:\\Tools\\cudnn-9.26.0.51\\cudnn-windows-x86_64-9.26.0.51_cuda13-archive\\bin\\x64",
                     ])),
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(location.Describe()));
        }
    }

    [Fact]
    public void EnsureIsSafeOnAMachineWithNoCudaAtAll()
    {
        // The test machines have none, so this exercises the real environment path end to end and
        // asserts only that it answers rather than throwing or hanging.
        CudaLibraries.Forget();
        try
        {
            var location = CudaLibraries.Ensure();
            Assert.False(string.IsNullOrWhiteSpace(location.Describe()));

            // Idempotent: the second call is the cached answer, not a second walk of the disk.
            Assert.Same(location, CudaLibraries.Ensure());
        }
        finally
        {
            CudaLibraries.Forget();
        }
    }
}
