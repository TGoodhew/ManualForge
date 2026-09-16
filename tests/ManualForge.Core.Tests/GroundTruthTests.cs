using ManualForge.Core.Benchmarking;

namespace ManualForge.Core.Tests;

/// <summary>
/// Hand-corrected pages are the expensive, irreplaceable part of a benchmark. Losing one to a
/// careless overwrite would cost an evening, so that is the property most worth pinning down.
/// </summary>
public class GroundTruthTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));

    public GroundTruthTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Truth => Path.Combine(_directory, "truth");

    private string Library
    {
        get
        {
            var path = Path.Combine(_directory, "library");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    [Fact]
    public void SeedingWritesAFileAPageAndAManifest()
    {
        var written = GroundTruthSet.Seed(Truth, Library,
        [
            (@"8340B Service.pdf", 42, PageKind.Table, "seeded text for page 42"),
            (@"8340B Service.pdf", 91, PageKind.Schematic, "seeded text for page 91"),
        ]);

        Assert.Equal(2, written);
        Assert.True(File.Exists(Path.Combine(Truth, GroundTruthSet.ManifestName)));

        var set = GroundTruthSet.Load(Truth, Library);
        Assert.Equal(2, set.Pages.Count);
        Assert.Equal([PageKind.Table, PageKind.Schematic], set.Pages.Select(p => p.Kind));
        Assert.Equal("seeded text for page 42", set.Pages[0].Text);
    }

    [Fact]
    public void SeedingAgainNeverOverwritesACorrection()
    {
        // The property that matters most. Re-seeding after correcting must not silently throw the
        // corrections away, or the harness destroys the only thing it cannot regenerate.
        GroundTruthSet.Seed(Truth, Library, [(@"a.pdf", 1, PageKind.Prose, "what the engine read")]);

        var textFile = Directory.GetFiles(Truth, "*.txt").Single();
        File.WriteAllText(textFile, "what a human corrected it to");

        var written = GroundTruthSet.Seed(Truth, Library, [(@"a.pdf", 1, PageKind.Prose, "a different engine reading")]);

        Assert.Equal(0, written);
        Assert.Equal("what a human corrected it to", File.ReadAllText(textFile));
        Assert.Equal("what a human corrected it to", GroundTruthSet.Load(Truth, Library).Pages.Single().Text);
    }

    [Fact]
    public void SeedingASecondKindKeepsTheFirstOnesRows()
    {
        // The documented workflow is one call per kind, because a kind applies to the whole call.
        // A manifest written fresh each time deregisters everything seeded before it - and the text
        // files stay on disk, so the work looks present and is simply never measured. Found by
        // walking the instructions rather than by reading them.
        GroundTruthSet.Seed(Truth, Library,
        [
            (@"a.pdf", 35, PageKind.Prose, "prose page"),
            (@"a.pdf", 161, PageKind.Prose, "another prose page"),
        ]);

        GroundTruthSet.Seed(Truth, Library,
        [
            (@"a.pdf", 148, PageKind.Table, "table page"),
        ]);

        var set = GroundTruthSet.Load(Truth, Library);

        Assert.Equal(3, set.Pages.Count);
        Assert.Equal([35, 148, 161], set.Pages.Select(p => p.PageNumber).Order());
        Assert.Equal(2, set.Pages.Count(p => p.Kind == PageKind.Prose));
        Assert.Single(set.Pages, p => p.Kind == PageKind.Table);
    }

    [Fact]
    public void SeedingTheSamePageAgainUpdatesItsRowRatherThanDuplicatingIt()
    {
        GroundTruthSet.Seed(Truth, Library, [(@"a.pdf", 7, PageKind.Prose, "text")]);
        GroundTruthSet.Seed(Truth, Library, [(@"a.pdf", 7, PageKind.Table, "text")]);

        var page = Assert.Single(GroundTruthSet.Load(Truth, Library).Pages);
        Assert.Equal(PageKind.Table, page.Kind);
    }

    [Fact]
    public void ARowWhoseTextFileHasGoneIsSkippedRatherThanThrowing()
    {
        GroundTruthSet.Seed(Truth, Library,
        [
            (@"a.pdf", 1, PageKind.Prose, "kept"),
            (@"b.pdf", 2, PageKind.Prose, "deleted"),
        ]);

        File.Delete(Directory.GetFiles(Truth, "b*.txt").Single());

        var set = GroundTruthSet.Load(Truth, Library);
        Assert.Equal("kept", set.Pages.Single().Text);
    }

    [Fact]
    public void RelativePathsResolveAgainstTheLibrary()
    {
        GroundTruthSet.Seed(Truth, Library, [(@"sub\a.pdf", 3, PageKind.Mixed, "text")]);

        var page = GroundTruthSet.Load(Truth, Library).Pages.Single();

        Assert.Equal(Path.Combine(Library, "sub", "a.pdf"), page.ManualPath);
        Assert.Equal(3, page.PageNumber);
    }

    [Fact]
    public void AMissingManifestSaysHowToMakeOne()
    {
        Directory.CreateDirectory(Truth);
        var ex = Assert.Throws<FileNotFoundException>(() => GroundTruthSet.Load(Truth));
        Assert.Contains("manualforge truth", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a,b,c", new[] { "a", "b", "c" })]
    [InlineData("\"HP 8340B, 41B.pdf\",42,Table,x.txt", new[] { "HP 8340B, 41B.pdf", "42", "Table", "x.txt" })]
    [InlineData("\"say \"\"what\"\"\",2", new[] { "say \"what\"", "2" })]
    [InlineData("", new[] { "" })]
    public void TheManifestHonoursQuotedFields(string line, string[] expected)
    {
        // Manual filenames contain commas often enough that a naive split would mis-read the
        // manifest and silently benchmark the wrong page.
        Assert.Equal(expected, GroundTruthSet.ParseCsvLine(line));
    }

    [Fact]
    public void AFilenameWithACommaSurvivesASeedAndLoadRoundTrip()
    {
        GroundTruthSet.Seed(Truth, Library, [(@"HP 8340B, 41B Service.pdf", 7, PageKind.Table, "text")]);

        var page = GroundTruthSet.Load(Truth, Library).Pages.Single();

        Assert.EndsWith("HP 8340B, 41B Service.pdf", page.ManualPath, StringComparison.Ordinal);
        Assert.Equal(7, page.PageNumber);
    }

    [Fact]
    public void AnUnknownKindBecomesMixedRatherThanFailingTheRun()
    {
        Directory.CreateDirectory(Truth);
        File.WriteAllText(Path.Combine(Truth, "p.txt"), "text");
        File.WriteAllLines(Path.Combine(Truth, GroundTruthSet.ManifestName),
            ["manual,page,kind,textFile", "a.pdf,1,Photographs,p.txt"]);

        Assert.Equal(PageKind.Mixed, GroundTruthSet.Load(Truth, Library).Pages.Single().Kind);
    }
}
