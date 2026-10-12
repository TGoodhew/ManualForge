using ManualForge.Shell;

namespace ManualForge.Core.Tests;

/// <summary>
/// Which library the app opens on: the command line, else the folder it was closed on, else the one the
/// installer recorded, each only while it is still there. Before 1.0.2 it opened on an empty box.
/// </summary>
public sealed class StartFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "manualforge-start-" + Guid.NewGuid().ToString("N"));

    public StartFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static readonly HashSet<string> There = new(StringComparer.OrdinalIgnoreCase) { @"D:\Manuals", @"E:\Other", @"F:\Given" };

    private static bool Exists(string path) => There.Contains(path);

    [Fact]
    public void TheInstallersLibraryIsWhereAFirstRunOpens()
        => Assert.Equal(@"D:\Manuals", StartFolder.Choose([], null, @"D:\Manuals", Exists));

    [Fact]
    public void TheFolderLastClosedOnComesBeforeTheInstallersLibrary()
        => Assert.Equal(@"E:\Other", StartFolder.Choose([], @"E:\Other", @"D:\Manuals", Exists));

    [Fact]
    public void AFolderOnTheCommandLineComesFirst()
        => Assert.Equal(@"F:\Given", StartFolder.Choose(["--verbose", @"F:\Given"], @"E:\Other", @"D:\Manuals", Exists));

    [Fact]
    public void AFolderThatHasGoneIsPassedOver()
        => Assert.Equal(@"D:\Manuals", StartFolder.Choose([@"G:\Gone"], @"H:\AlsoGone", @"D:\Manuals", Exists));

    [Fact]
    public void WithNothingToGoOnTheBoxStaysEmpty()
        => Assert.Null(StartFolder.Choose([], null, null, Exists));

    [Fact]
    public void SettingsSurviveARoundTrip()
    {
        var path = Path.Combine(_root, "ManualForge", "app.json");

        new AppSettings(@"D:\Manuals").Save(path);

        Assert.Equal(@"D:\Manuals", AppSettings.Load(path).LastFolder);
    }

    [Fact]
    public void MissingOrUnreadableSettingsAreNoSettings()
    {
        var path = Path.Combine(_root, "app.json");
        Assert.Null(AppSettings.Load(path).LastFolder);

        File.WriteAllText(path, "{ not json");
        Assert.Null(AppSettings.Load(path).LastFolder);
    }
}
