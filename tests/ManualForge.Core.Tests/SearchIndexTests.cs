using ManualForge.Core.Indexing;

namespace ManualForge.Core.Tests;

/// <summary>
/// The index has one job: turn a question into a manual, a page and enough text to know whether it
/// is the right page.
/// </summary>
public class SearchIndexTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));

    public SearchIndexTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private SearchIndex NewIndex(string name = "index.db") => new(Path.Combine(_directory, name));

    private static IndexedPageText[] Pages(params string[] text) =>
        text.Select(t => Dehyphenator.Prepare(t)).ToArray();

    [Fact]
    public void TheBundledSqliteHasFts5()
    {
        // Asked rather than assumed: FTS5 is an optional SQLite extension, and a build without it
        // fails only when the first virtual table is created - which would be at a user's first
        // index rather than here.
        Assert.True(SearchIndex.IsFts5Available(), "the bundled SQLite was built without FTS5");
    }

    [Fact]
    public void AQuestionFindsThePageThatAnswersIt()
    {
        using var index = NewIndex();
        index.AddDocument(
            @"C:\Manuals\59401A.pdf", "59401A", "hash-a",
            Pages(
                "Table of contents",
                "The HP-IB handshake lines are carried on pins 6 through 11 of the rear connector.",
                "Replacing the line fuse"));

        var hit = Assert.Single(index.Search("handshake"));

        Assert.Equal(2, hit.PageNumber);
        Assert.Equal("59401A", hit.Title);
        Assert.Contains("[handshake]", hit.Snippet, StringComparison.Ordinal);
        Assert.Contains("pins 6 through 11", hit.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralWordsTogetherBeatAnyOneOfThem()
    {
        using var index = NewIndex();
        index.AddDocument(@"C:\Manuals\a.pdf", "A", "hash-a",
            Pages("The handshake is described elsewhere.", "HP-IB handshake timing on pins 6 to 11."));

        var hits = index.Search("handshake pins");

        Assert.Equal(2, Assert.Single(hits).PageNumber);
    }

    [Fact]
    public void APhraseIsHonoured()
    {
        using var index = NewIndex();
        index.AddDocument(@"C:\Manuals\a.pdf", "A", "hash-a",
            Pages("power supply ripple", "supply power to the bridge"));

        var hit = Assert.Single(index.Search("\"power supply\""));
        Assert.Equal(1, hit.PageNumber);
    }

    [Fact]
    public void PageNumbersAreNotSearchableText()
    {
        // doc_id and page_number are UNINDEXED. If they were not, a query for a part number would
        // match every page that happened to share its digits.
        using var index = NewIndex();
        index.AddDocument(@"C:\Manuals\a.pdf", "A", "hash-a",
            Pages("nothing of interest", "nothing here either", "still nothing"));

        Assert.Empty(index.Search("2"));
    }

    [Fact]
    public void BlankPagesTakeNoRoomAndShiftNothing()
    {
        using var index = NewIndex();
        index.AddDocument(@"C:\Manuals\a.pdf", "A", "hash-a",
            Pages("first", "", "   ", "the calibration procedure"));

        var hit = Assert.Single(index.Search("calibration"));

        // The blank pages are not stored, but the page number still has to be the real one.
        Assert.Equal(4, hit.PageNumber);
        Assert.Equal(2, index.Statistics().Pages);
    }

    [Fact]
    public void ReindexingADocumentReplacesItRatherThanDoublingIt()
    {
        using var index = NewIndex();
        index.AddDocument(@"C:\Manuals\a.pdf", "A", "hash-1", Pages("the original text"));
        index.AddDocument(@"C:\Manuals\a.pdf", "A", "hash-2", Pages("the corrected text"));

        Assert.Equal(1, index.Statistics().Documents);
        Assert.Empty(index.Search("original"));
        Assert.Single(index.Search("corrected"));
        Assert.Equal("hash-2", index.ContentHashOf(@"C:\Manuals\a.pdf"));
    }

    [Fact]
    public void AnUnchangedDocumentCanBeRecognisedAndSkipped()
    {
        using var index = NewIndex();
        index.AddDocument(@"C:\Manuals\a.pdf", "A", "hash-1", Pages("text"));

        Assert.Equal("hash-1", index.ContentHashOf(@"C:\Manuals\a.pdf"));
        Assert.Null(index.ContentHashOf(@"C:\Manuals\never-seen.pdf"));
    }

    [Fact]
    public void IdenticalCopiesAnswerOnceAndSayWhereElseTheyLive()
    {
        // The gap noted when deduplication was built: it only hashes files marked for work, so a
        // duplicate that was skipped is never grouped. Without folding here, a library holding four
        // copies of one manual answers every question about it four times.
        using var index = NewIndex();
        foreach (var path in new[]
                 {
                     @"C:\Manuals\8340 Assembly Service.pdf",
                     @"C:\Manuals\HP8340B\08340-90243.pdf",
                     @"C:\Manuals\HP8340B\8340B Assembly.pdf",
                 })
        {
            index.AddDocument(path, Path.GetFileNameWithoutExtension(path), "same-hash",
                Pages("first page", "the YIG oscillator adjustment procedure"));
        }

        var hit = Assert.Single(index.Search("oscillator"));

        Assert.Equal(2, hit.PageNumber);
        Assert.True(hit.HasDuplicates);
        Assert.Equal(2, hit.AlsoAt.Count);

        // Asking for them separately is still possible.
        Assert.Equal(3, index.Search("oscillator", foldDuplicates: false).Count);
    }

    [Fact]
    public void DifferentDocumentsAreNeverFoldedTogether()
    {
        using var index = NewIndex();
        index.AddDocument(@"C:\Manuals\a.pdf", "A", "hash-a", Pages("the oscillator adjustment"));
        index.AddDocument(@"C:\Manuals\b.pdf", "B", "hash-b", Pages("the oscillator adjustment"));

        var hits = index.Search("oscillator");

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.False(h.HasDuplicates));
    }

    [Fact]
    public void ADocumentCanBeRemoved()
    {
        using var index = NewIndex();
        index.AddDocument(@"C:\Manuals\a.pdf", "A", "hash-a", Pages("the oscillator"));
        index.Remove(@"C:\Manuals\a.pdf");

        Assert.Empty(index.Search("oscillator"));
        Assert.Equal(0, index.Statistics().Documents);
    }

    [Fact]
    public void AccentsDoNotHaveToBeTypedToBeFound()
    {
        using var index = NewIndex();
        index.AddDocument(@"C:\Manuals\a.pdf", "A", "hash-a", Pages("mesure de fréquence"));

        Assert.Single(index.Search("frequence"));
        Assert.Single(index.Search("fréquence"));
    }

    [Fact]
    public void ResultsSurviveTheProcessThatWroteThem()
    {
        const string name = "durable.db";
        using (var index = NewIndex(name))
            index.AddDocument(@"C:\Manuals\a.pdf", "A", "hash-a", Pages("the YIG oscillator"));

        using (var reopened = NewIndex(name))
            Assert.Single(reopened.Search("YIG"));
    }
}
