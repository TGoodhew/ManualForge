using ManualForge.Core.Auditing;
using ManualForge.Core.Indexing;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// What happens to recovered text between the recogniser and a search result: it is stored, it is
/// merged without disturbing anything that was already right, and a hit that came from it says so.
/// </summary>
public sealed class RepairAndProvenanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "manualforge-repair-" + Guid.NewGuid().ToString("N"));

    public RepairAndProvenanceTests() => Directory.CreateDirectory(_directory);

    private string At(string name) => Path.Combine(_directory, name);

    private static IndexedPageText[] Pages(params string[] text) =>
        text.Select(Dehyphenator.Prepare).ToArray();

    private static PageRepair Repair(string path, int page, string hash, string text, double confidence = 0.95) =>
        new(path, page, hash, 300, text, confidence, text.Split(' ').Length, DateTimeOffset.UtcNow);

    // -----------------------------------------------------------------------------------------
    // The store.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void FindingsAndRepairsSurviveTheProcessThatWroteThem()
    {
        var database = At("doctor.db");
        var pdf = At("a.pdf");

        using (var store = new DoctorStore(database))
        {
            store.Save(new DocumentAudit(pdf, "A", "hash-1", 3,
            [
                new PageAudit(2, 40, 30, 200, 2, 0, 0, false, false,
                    new InkAnalysis(0.03, 0.02, 120, 9), false, PageVerdict.UnderExtracted,
                    ["most of this page is drawn"]) { EstimatedRecoverableCharacters = 102 },
            ])
            { Verdict = DocumentVerdict.UnderExtracted });

            store.SaveRepair(Repair(pdf, 2, "hash-1", "CHANnel INPut LFR1"));
        }

        using var reopened = new DoctorStore(database, readOnly: true);

        var document = Assert.Single(reopened.Flagged());
        Assert.Equal(1, document.FlaggedPages);
        Assert.Equal(1, document.RepairedPages);
        Assert.Equal(0, document.OutstandingPages);
        Assert.Equal(DocumentVerdict.UnderExtracted, document.Verdict);

        var finding = Assert.Single(reopened.Findings(pdf, PageVerdict.UnderExtracted));
        Assert.Equal(2, finding.PageNumber);
        Assert.Equal(120, finding.GlyphLikeBlobs);
        Assert.Contains("drawn", finding.Signals, StringComparison.Ordinal);
    }

    [Fact]
    public void RepairsAreOnlyReturnedForTheFileTheyWereMadeFrom()
    {
        // The page numbers in a repair describe one particular file. If the PDF is rebuilt, its
        // pagination may change, and attaching old text to new pages is worse than not repairing.
        var database = At("doctor.db");
        var pdf = At("a.pdf");

        using var store = new DoctorStore(database);
        store.SaveRepair(Repair(pdf, 2, "hash-1", "CHANnel INPut"));

        Assert.Single(store.Repairs(pdf, "hash-1"));
        Assert.Empty(store.Repairs(pdf, "hash-2"));
    }

    [Fact]
    public void AnUnchangedFileCanBeRecognisedAndSkipped()
    {
        using var store = new DoctorStore(At("doctor.db"));
        var pdf = At("a.pdf");

        store.Save(new DocumentAudit(pdf, "A", "hash-1", 1, []));

        Assert.Equal("hash-1", store.ContentHashOf(pdf));
        Assert.Null(store.ContentHashOf(At("never-seen.pdf")));
    }

    // -----------------------------------------------------------------------------------------
    // The merge.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void MergingKeepsTheExistingTextByteForByteAndOnlyAddsToIt()
    {
        var embedded = Pages("CHANnel Commands", "ordinary prose that extracted perfectly");
        var repairs = new Dictionary<int, PageRepair>
        {
            [1] = Repair("a.pdf", 1, "h", "BWLimit DISPlay INPut AC DC LFR1 LFR2"),
        };

        var (merged, provenance) = LibraryIndexer.Merge(embedded, repairs);

        // The page nobody repaired is the same object's text, untouched.
        Assert.Equal(embedded[1].Text, merged[1].Text);
        Assert.False(provenance.ContainsKey(2));

        // The repaired page still starts with exactly what was there before.
        Assert.StartsWith(embedded[0].Text, merged[0].Text, StringComparison.Ordinal);
        Assert.Contains("LFR1", merged[0].Text, StringComparison.Ordinal);

        var source = provenance[1];
        Assert.Equal(embedded[0].Text.Length, source.EmbeddedCharacters);
        Assert.True(source.OcrCharacters > 0);
    }

    [Fact]
    public void MergingChangesNothingWhenThereIsNothingToMerge()
    {
        var embedded = Pages("one", "two");

        var (merged, provenance) = LibraryIndexer.Merge(embedded, new Dictionary<int, PageRepair>());

        Assert.Same(embedded, merged);
        Assert.Empty(provenance);
    }

    [Fact]
    public void ARepairChangesTheSupplementHashSoTheDocumentIsIndexedAgain()
    {
        // The file itself does not change when it is repaired, so the content hash cannot be what
        // decides whether to re-index. Without this, recovered text would never reach search.
        var none = LibraryIndexer.SupplementHash(new Dictionary<int, PageRepair>());
        var one = LibraryIndexer.SupplementHash(new Dictionary<int, PageRepair>
        {
            [1] = Repair("a.pdf", 1, "h", "CHANnel"),
        });
        var two = LibraryIndexer.SupplementHash(new Dictionary<int, PageRepair>
        {
            [1] = Repair("a.pdf", 1, "h", "CHANnel"),
            [2] = Repair("a.pdf", 2, "h", "TIMebase"),
        });

        Assert.Equal(string.Empty, none);
        Assert.NotEqual(none, one);
        Assert.NotEqual(one, two);
    }

    // -----------------------------------------------------------------------------------------
    // What a search result says about where its text came from.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void AHitOnRecoveredTextSaysSo()
    {
        using var index = new SearchIndex(At("index.db"));

        index.AddDocument(
            At("a.pdf"), "A", "hash-a",
            Pages("CHANnel Commands\n\nBWLimit DISPlay INPut LFR1", "ordinary prose"),
            new Dictionary<int, PageProvenance>
            {
                [1] = new PageProvenance(16, 28, 0.96, "BWLimit DISPlay INPut LFR1"),
            },
            "supplement-1");

        var recovered = Assert.Single(index.Search("LFR1"));
        Assert.Equal(TextSource.Ocr, recovered.MatchSource);
        Assert.Equal(TextSource.Mixed, recovered.PageSource);
        Assert.True(recovered.MatchedOcrText);
        Assert.Equal(0.96, recovered.OcrConfidence, 3);

        // The heading was typeset, and is on the same page. A hit on it is not an OCR hit.
        var typeset = Assert.Single(index.Search("Commands"));
        Assert.Equal(TextSource.Embedded, typeset.MatchSource);
        Assert.False(typeset.MatchedOcrText);

        // A page nobody repaired says nothing about OCR at all.
        var prose = Assert.Single(index.Search("prose"));
        Assert.Equal(TextSource.Embedded, prose.PageSource);
    }

    [Fact]
    public void AHitOnBothSourcesAtOnceIsReportedAsMixed()
    {
        using var index = new SearchIndex(At("index.db"));

        index.AddDocument(
            At("a.pdf"), "A", "hash-a",
            Pages("CHANnel Commands\n\nCHANnel BWLimit"),
            new Dictionary<int, PageProvenance>
            {
                [1] = new PageProvenance(16, 15, 0.9, "CHANnel BWLimit"),
            });

        var hit = Assert.Single(index.Search("CHANnel BWLimit"));
        Assert.Equal(TextSource.Mixed, hit.MatchSource);
    }

    [Fact]
    public void CaseIsStoredExactlyAndSearchedWithoutRegardToIt()
    {
        // SCPI documents its abbreviations by capitalisation. Case-insensitive search is right;
        // case-insensitive storage would destroy information that cannot be recovered.
        using var index = new SearchIndex(At("index.db"));
        index.AddDocument(At("a.pdf"), "A", "hash-a", Pages("BYTeorder space MSBFirst LSBFirst"));

        Assert.Single(index.Search("BYTeorder"));
        Assert.Single(index.Search("byteorder"));
        Assert.Single(index.Search("BYTEORDER"));

        Assert.Contains("BYTeorder", Assert.Single(index.Search("MSBFirst")).Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void AHitCarriesALabelForWhereItsTextCameFrom()
    {
        // Bound straight into the application's result list, where an empty label draws nothing.
        using var index = new SearchIndex(At("index.db"));

        index.AddDocument(
            At("a.pdf"), "A", "hash-a",
            Pages("CHANnel Commands\n\nBWLimit LFR1", "ordinary prose"),
            new Dictionary<int, PageProvenance>
            {
                [1] = new PageProvenance(16, 13, 0.96, "BWLimit LFR1"),
            });

        Assert.StartsWith("OCR ", Assert.Single(index.Search("LFR1")).SourceLabel, StringComparison.Ordinal);
        Assert.StartsWith("part OCR ", Assert.Single(index.Search("CHANnel BWLimit")).SourceLabel, StringComparison.Ordinal);
        Assert.Equal(string.Empty, Assert.Single(index.Search("prose")).SourceLabel);
    }

    [Fact]
    public void StatisticsCountTheRepairedPages()
    {
        using var index = new SearchIndex(At("index.db"));
        index.AddDocument(At("a.pdf"), "A", "hash-a", Pages("one", "two"),
            new Dictionary<int, PageProvenance> { [2] = new PageProvenance(3, 9, 0.9, "recovered") });

        var statistics = index.Statistics();
        Assert.Equal(1, statistics.RepairedPages);
        Assert.Equal(1, statistics.RepairedDocuments);
    }

    [Fact]
    public void RemovingADocumentTakesItsProvenanceWithIt()
    {
        using var index = new SearchIndex(At("index.db"));
        index.AddDocument(At("a.pdf"), "A", "hash-a", Pages("one"),
            new Dictionary<int, PageProvenance> { [1] = new PageProvenance(3, 9, 0.9, "recovered") });

        index.Remove(At("a.pdf"));

        Assert.Equal(0, index.Statistics().RepairedPages);
    }

    // -----------------------------------------------------------------------------------------
    // Accounting for the files that are not in the index.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ReconcilingNamesTheFilesThatAreNotIndexedAndWhy()
    {
        var library = At("library");
        Directory.CreateDirectory(library);

        var indexed = TestPdf.TypesetOnly(Path.Combine(library, "indexed.pdf"), "the frequency control");
        var readable = TestPdf.TypesetOnly(Path.Combine(library, "never-indexed.pdf"), "the range switch");
        var broken = Path.Combine(library, "broken.pdf");
        File.WriteAllText(broken, "this is not a PDF at all");

        using var index = new SearchIndex(At("index.db"));
        index.AddDocument(indexed, "indexed", "hash-a", Pages("the frequency control"));

        var reconciliation = LibraryReconciler.Reconcile(library, index);

        Assert.False(reconciliation.IsClean);
        Assert.Equal(3, reconciliation.FilesOnDisk);
        Assert.Equal(2, reconciliation.NotIndexed.Count);

        var fixable = Assert.Single(reconciliation.NotIndexed, f => f.Path == Path.GetFullPath(readable));
        Assert.True(fixable.FixedByReindexing);
        Assert.Contains("never been indexed", fixable.Reason, StringComparison.Ordinal);

        // The one that needs a person is told apart from the one that needs a command.
        var hopeless = Assert.Single(reconciliation.NotIndexed, f => f.Path == Path.GetFullPath(broken));
        Assert.False(hopeless.FixedByReindexing);
        Assert.Contains("cannot be opened", hopeless.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyFileIsCalledEmptyRatherThanMalformed()
    {
        // The real library had one: a zero-byte placeholder from 2017. "Could not find the version
        // header comment" is true and tells nobody anything.
        var library = At("library");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "placeholder.pdf"), []);

        using var index = new SearchIndex(At("index.db"));

        var file = Assert.Single(LibraryReconciler.Reconcile(library, index).NotIndexed);
        Assert.Contains("zero bytes", file.Reason, StringComparison.Ordinal);
        Assert.False(file.FixedByReindexing);
    }

    [Fact]
    public void ReconcilingAlsoNoticesADocumentThatHasLeftTheFolder()
    {
        var library = At("library");
        Directory.CreateDirectory(library);

        using var index = new SearchIndex(At("index.db"));
        index.AddDocument(Path.Combine(library, "gone.pdf"), "gone", "hash-a", Pages("text"));

        var reconciliation = LibraryReconciler.Reconcile(library, index);

        Assert.Single(reconciliation.IndexedButGone);
        Assert.False(reconciliation.IsClean);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception)
        {
            // A locked temp file is not a test failure.
        }
    }
}
