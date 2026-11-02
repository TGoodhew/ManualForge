using ManualForge.Core.Auditing;
using ManualForge.Core.Pipeline;
using ManualForge.Shell;

namespace ManualForge.Core.Tests;

/// <summary>
/// Adding manuals to the library from Explorer (#89): what is copied, what is recognised as already
/// there, and what a name already taken becomes. Nothing in the library may be overwritten.
/// </summary>
public sealed class LibraryAdderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));

    private readonly string _library;
    private readonly string _downloads;

    public LibraryAdderTests()
    {
        _library = Path.Combine(_directory, "Manuals");
        _downloads = Path.Combine(_directory, "Downloads");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_downloads);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Download(string name, string content) => Write(Path.Combine(_downloads, name), content);

    private string InLibrary(string relative, string content) => Write(Path.Combine(_library, relative), content);

    private IReadOnlyList<AddResult> Add(params string[] sources) =>
        new LibraryAdder().Add(new LibraryOptions { Root = _library }, sources);

    [Fact]
    public void ANewManualIsCopiedIntoTheRootAndTheDownloadIsLeftWhereItWas()
    {
        var source = Download("HP 8563E Service.pdf", "%PDF a manual");

        var result = Assert.Single(Add(source));

        Assert.Equal(AddKind.Added, result.Kind);
        Assert.Equal(Path.Combine(_library, "HP 8563E Service.pdf"), result.LibraryPath);
        Assert.False(result.Renamed);
        Assert.Equal("%PDF a manual", File.ReadAllText(result.LibraryPath!));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void ANameAlreadyTakenGetsTwoThenThreeAndNothingIsOverwritten()
    {
        InLibrary("Manual.pdf", "%PDF the one already there");
        InLibrary("Manual (2).pdf", "%PDF another already there");

        var result = Assert.Single(Add(Download("Manual.pdf", "%PDF a different manual")));

        Assert.Equal(AddKind.Added, result.Kind);
        Assert.Equal(Path.Combine(_library, "Manual (3).pdf"), result.LibraryPath);
        Assert.True(result.Renamed);
        Assert.Equal("%PDF the one already there", File.ReadAllText(Path.Combine(_library, "Manual.pdf")));
        Assert.Equal("%PDF another already there", File.ReadAllText(Path.Combine(_library, "Manual (2).pdf")));
    }

    [Fact]
    public void ANameKeptInTheOriginalsOrTakenByASilencedFileCountsAsTaken()
    {
        // Processing a different manual under either name would push the earlier original aside.
        InLibrary(Path.Combine("_Originals", "Kept.pdf"), "%PDF an original whose searchable copy moved");
        InLibrary("Silenced_repaired.pdf", "%PDF a file renamed when its text was silenced");

        var results = Add(Download("Kept.pdf", "%PDF new one"), Download("Silenced.pdf", "%PDF new two"));

        Assert.Equal(Path.Combine(_library, "Kept (2).pdf"), results[0].LibraryPath);
        Assert.Equal(Path.Combine(_library, "Silenced (2).pdf"), results[1].LibraryPath);
    }

    [Fact]
    public void TheSameBytesAnywhereInTheLibraryOrItsOriginalsAreAlreadyHeld()
    {
        var inSubfolder = InLibrary(Path.Combine("HP", "Counter.pdf"), "%PDF a counter manual");
        var original = InLibrary(Path.Combine("_Originals", "Scope.pdf"), "%PDF a scope, as downloaded");
        InLibrary("Scope.pdf", "%PDF a scope, made searchable, so different bytes");

        var results = Add(
            Download("counter-renamed.pdf", "%PDF a counter manual"),
            Download("Scope.pdf", "%PDF a scope, as downloaded"));

        Assert.All(results, r => Assert.Equal(AddKind.AlreadyHeld, r.Kind));
        Assert.Equal(inSubfolder, results[0].LibraryPath);
        Assert.Equal(original, results[1].LibraryPath);
        Assert.Equal(["Scope.pdf"], Directory.GetFiles(_library, "*.pdf").Select(p => Path.GetFileName(p)).ToArray());
    }

    [Fact]
    public void TheSameSizeIsNotTheSameManual()
    {
        InLibrary("A.pdf", "%PDF aaaa");

        var result = Assert.Single(Add(Download("B.pdf", "%PDF bbbb")));

        Assert.Equal(AddKind.Added, result.Kind);
    }

    [Fact]
    public void AReferenceCopyKeptApartFromTheLibraryDoesNotCountAsHeld()
    {
        InLibrary(Path.Combine("BASELINE", "Manual.pdf"), "%PDF as another engine left it");

        var result = Assert.Single(Add(Download("Manual.pdf", "%PDF as another engine left it")));

        Assert.Equal(AddKind.Added, result.Kind);
    }

    [Fact]
    public void TheSameFileGivenTwiceInOneGoIsAddedOnce()
    {
        var source = Download("Twice.pdf", "%PDF given twice");
        var copy = Download("Twice copy.pdf", "%PDF given twice");

        var results = Add(source, copy);

        Assert.Equal(AddKind.Added, results[0].Kind);
        Assert.Equal(AddKind.AlreadyHeld, results[1].Kind);
        Assert.Equal(results[0].LibraryPath, results[1].LibraryPath);
    }

    [Fact]
    public void AFileAlreadyInTheLibraryIsAlreadyHeldByItself()
    {
        var already = InLibrary("Here.pdf", "%PDF already here");

        var result = Assert.Single(Add(already));

        Assert.Equal(AddKind.AlreadyHeld, result.Kind);
        Assert.Equal(already, result.LibraryPath);
    }

    [Fact]
    public void FoldersOtherFilesAndMissingFilesAreRefusedWithAReason()
    {
        var folder = Path.Combine(_downloads, "A folder");
        Directory.CreateDirectory(folder);

        var results = Add(folder, Download("notes.txt", "not a manual"), Path.Combine(_downloads, "gone.pdf"));

        Assert.All(results, r => Assert.Equal(AddKind.Refused, r.Kind));
        Assert.Contains("folder", results[0].Detail);
        Assert.Equal("not a PDF", results[1].Detail);
        Assert.Equal("not found", results[2].Detail);
        Assert.Empty(Directory.GetFileSystemEntries(_library));
    }

    [Fact]
    public void NoTemporaryFileIsLeftInTheLibrary()
    {
        Add(Download("One.pdf", "%PDF one"), Download("Two.pdf", "%PDF two"));

        Assert.Equal(
            ["One.pdf", "Two.pdf"],
            Directory.GetFileSystemEntries(_library).Select(p => Path.GetFileName(p)).Order().ToArray());
    }

    [Fact]
    public async Task AnAuditOfSomeFilesAuditsOnlyThoseIntoTheLibrarysFindings()
    {
        var added = TestPdf.Scanned(Path.Combine(_library, "Added.pdf"));
        TestPdf.Scanned(Path.Combine(_library, "Sub", "Other.pdf"));

        var report = await new DoctorRunner().RunAsync(_library, new DoctorOptions { Workers = 1 }, paths: [added]);

        Assert.Equal([added], report.Audits.Select(a => a.Path).ToArray());
        Assert.True(File.Exists(DoctorStore.DefaultPathFor(_library)));
    }

    [Theory]
    [InlineData(new string[0], new string[0], new string[0])]
    [InlineData(new[] { @"C:\Manuals" }, new[] { @"C:\Manuals" }, new string[0])]
    [InlineData(new[] { "--add", @"C:\a.pdf", @"C:\b.pdf" }, new string[0], new[] { @"C:\a.pdf", @"C:\b.pdf" })]
    [InlineData(new[] { @"C:\Manuals", "--ADD", @"C:\Folder" }, new[] { @"C:\Manuals" }, new[] { @"C:\Folder" })]
    public void TheCommandLineKeepsTheStartFolderApartFromFilesToAdd(string[] given, string[] start, string[] add)
    {
        var launch = LaunchArguments.Parse(given);

        Assert.Equal(start, launch.Start);
        Assert.Equal(add, launch.Add);
    }
}
