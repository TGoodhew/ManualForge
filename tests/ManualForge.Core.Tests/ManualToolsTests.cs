using ManualForge.Core.Auditing;
using ManualForge.Core.Indexing;
using ManualForge.Mcp;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// What the MCP tools actually say, which is most of what they do.
///
/// <para>
/// The wording is the product here. A tool that answers "nothing found" in a way that reads as
/// "this library does not cover it" converts a recoverable gap into a wrong conclusion, and that is
/// not a presentation detail — it is the defect this work exists to fix. So the claims are asserted
/// rather than eyeballed.
/// </para>
/// </summary>
public sealed class ManualToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "manualforge-mcp-" + Guid.NewGuid().ToString("N"));

    public ManualToolsTests() => Directory.CreateDirectory(_root);

    private ManualLibraryContext Library => ManualLibraryContext.At(_root);

    private static IndexedPageText[] Pages(params string[] text) =>
        text.Select(Dehyphenator.Prepare).ToArray();

    /// <summary>A one-document library with an index, and a PDF on disk to match.</summary>
    private string GivenAManual(
        IReadOnlyDictionary<int, PageProvenance>? provenance = null, string supplement = "")
    {
        var pdf = TestPdf.TypesetOnly(
            Path.Combine(_root, "54845A Programmer.pdf"),
            "Set the FREQUENCY control fully clockwise.",
            pages: 3);

        using var index = new SearchIndex(LibraryIndexer.DefaultIndexPath(_root));
        index.AddDocument(
            pdf, "54845A Programmer", "hash-a",
            Pages(
                "CHANnel Commands",
                "CHANnel Commands\n\nBWLimit DISPlay INPut AC DC LFR1 LFR2 DC50|DCFifty",
                "ordinary prose about the front panel"),
            provenance,
            supplement);

        return pdf;
    }

    private void GivenAnAudit(string pdf, int flaggedPages, int repairedPages)
    {
        using var store = new DoctorStore(DoctorStore.DefaultPathFor(_root));

        var pages = Enumerable.Range(1, flaggedPages)
            .Select(n => new PageAudit(
                n, 40, 20, 200, 2, 0, 0, false, false,
                new InkAnalysis(0.03, 0.02, 120, 9), false, PageVerdict.UnderExtracted,
                ["the missing content is drawn on the page, not photographed"])
            {
                EstimatedRecoverableCharacters = 102,
            })
            .ToArray();

        store.Save(new DocumentAudit(pdf, "54845A Programmer", "hash-file", 110, pages)
        {
            Verdict = DocumentVerdict.UnderExtracted,
        });

        for (var n = 1; n <= repairedPages; n++)
        {
            store.SaveRepair(new PageRepair(
                pdf, n, "hash-file", 300, "CHANnel INPut LFR1", 0.96, 3, DateTimeOffset.UtcNow));
        }
    }

    [Fact]
    public void WithNoAuditAMissSaysWhatHasNotBeenChecked()
    {
        GivenAManual();

        var answer = new ManualTools(Library).LibrarySearch("gorilla husbandry");

        Assert.DoesNotContain("real absence", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT evidence", answer, StringComparison.Ordinal);
        Assert.Contains("vector graphics", answer, StringComparison.Ordinal);
        Assert.Contains("manualforge doctor", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void WithFindingsOutstandingAMissNamesTheSuspectDocuments()
    {
        var pdf = GivenAManual();
        GivenAnAudit(pdf, flaggedPages: 75, repairedPages: 0);

        var answer = new ManualTools(Library).LibrarySearch("gorilla husbandry");

        Assert.Contains("NOT a real absence yet", answer, StringComparison.Ordinal);
        Assert.Contains("54845A Programmer.pdf", answer, StringComparison.Ordinal);
        Assert.Contains("75", answer, StringComparison.Ordinal);
        Assert.Contains("manualforge repair", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void OnceEverythingFlaggedIsRepairedTheClaimIsMadeAgain()
    {
        // The claim was never wrong to want — only wrong to make unconditionally. Once the audit has
        // run and everything it found has been recovered, it is honest and is worth making.
        var pdf = GivenAManual();
        GivenAnAudit(pdf, flaggedPages: 4, repairedPages: 4);

        var answer = new ManualTools(Library).LibrarySearch("gorilla husbandry");

        Assert.Contains("real absence rather than a truncated search", answer, StringComparison.Ordinal);
        Assert.Contains("never OCR'd has no text to match", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void AHitOnRecoveredTextIsMarkedAndItsConfidenceGiven()
    {
        GivenAManual(
            new Dictionary<int, PageProvenance>
            {
                [2] = new PageProvenance(16, 48, 0.96, "BWLimit DISPlay INPut AC DC LFR1 LFR2 DC50|DCFifty"),
            },
            "supplement-1");

        var answer = new ManualTools(Library).LibrarySearch("LFR1");

        Assert.Contains("[OCR]", answer, StringComparison.Ordinal);
        Assert.Contains("96 %", answer.Replace("96%", "96 %", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("recovered by OCR", answer, StringComparison.Ordinal);

        // And the caller is warned about the confusions that matter in this genre, because the
        // string may be a command about to be sent to an instrument.
        Assert.Contains("0/O", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void AHitOnThePdfsOwnTextIsNotMarked()
    {
        GivenAManual(
            new Dictionary<int, PageProvenance>
            {
                [2] = new PageProvenance(16, 48, 0.96, "BWLimit DISPlay INPut AC DC LFR1 LFR2"),
            });

        var answer = new ManualTools(Library).LibrarySearch("prose");

        Assert.DoesNotContain("[OCR]", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("recovered by OCR", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusNamesTheFilesThatAreNotIndexedAndWhy()
    {
        GivenAManual();

        // A second PDF nobody has indexed, and an empty one that nothing will fix.
        TestPdf.TypesetOnly(Path.Combine(_root, "later.pdf"), "added after the index was built");
        File.WriteAllBytes(Path.Combine(_root, "placeholder.pdf"), []);

        var answer = new ManualTools(Library).LibraryStatus();

        Assert.Contains("NOT INDEXED", answer, StringComparison.Ordinal);
        Assert.Contains("later.pdf", answer, StringComparison.Ordinal);
        Assert.Contains("placeholder.pdf", answer, StringComparison.Ordinal);
        Assert.Contains("zero bytes", answer, StringComparison.Ordinal);

        // And it distinguishes the one a command fixes from the one a person has to look at.
        Assert.Contains("need a look", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusSaysWhetherTheLibraryHasBeenAuditedAtAll()
    {
        var pdf = GivenAManual();

        Assert.Contains("AUDIT  : never run", new ManualTools(Library).LibraryStatus(), StringComparison.Ordinal);

        GivenAnAudit(pdf, flaggedPages: 75, repairedPages: 10);

        var audited = new ManualTools(Library).LibraryStatus();
        Assert.Contains("75 page(s) in total", audited, StringComparison.Ordinal);
        Assert.Contains("65 page(s) still hold text that no search can reach", audited, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingAPageKeepsRecoveredTextUnderItsOwnHeading()
    {
        var pdf = GivenAManual();

        using (var store = new DoctorStore(DoctorStore.DefaultPathFor(_root)))
        {
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pdf)));

            store.SaveRepair(new PageRepair(
                pdf, 2, hash, 300, "BWLimit DISPlay INPut LFR1", 0.96, 4, DateTimeOffset.UtcNow));
        }

        var answer = new ManualTools(Library).ReadManualPage(Path.GetFileName(pdf), 2);

        Assert.Contains("recovered by OCR from the rendered page", answer, StringComparison.Ordinal);
        Assert.Contains("LFR1", answer, StringComparison.Ordinal);

        // The PDF's own text is still there, above it and unmarked.
        Assert.Contains("FREQUENCY", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void APathOutsideTheLibraryIsRefused()
    {
        GivenAManual();

        Assert.Contains(
            "not a file inside the manual library",
            new ManualTools(Library).ReadManualPage(@"..\..\..\secrets.txt", 1),
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A locked temp file is not a test failure.
        }
    }
}
