using ManualForge.Core.Auditing;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// What "outstanding" means once an audit has changed its mind.
///
/// <para>
/// The counts come from two tables that drift apart: the audit rewrites a document's findings every
/// time it runs, while its repairs are kept, because recovered text is expensive and outlives any
/// re-audit. Counting every repair a document has ever had and subtracting it from the pages
/// currently flagged is therefore not a count of anything — and it is the arithmetic that told
/// `repair` a library with 352 pages waiting had nothing left to do.
/// </para>
/// </summary>
public sealed class OutstandingPagesTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "manualforge-outstanding-" + Guid.NewGuid().ToString("N"));

    public OutstandingPagesTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private DoctorStore NewStore() => new(Path.Combine(_directory, "doctor.db"));

    private static PageAudit Flagged(int pageNumber) => new(
        pageNumber, 40, 20, 200, 2, 0, 0, false, false,
        new InkAnalysis(0.03, 0.02, 120, 9), false, PageVerdict.UnderExtracted,
        ["the missing content is drawn on the page, not photographed"]);

    private static PageAudit Fine(int pageNumber) => new(
        pageNumber, 2000, 1900, 5, 300, 0, 0, false, false,
        InkAnalysis.NotRendered, false, PageVerdict.Fine, []);

    private static DocumentAudit Document(string path, string hash, params PageAudit[] pages) =>
        new(path, Path.GetFileNameWithoutExtension(path), hash, pages.Length, pages, null);

    private static PageRepair Repair(string path, int page, string hash) =>
        new(path, page, hash, 600, "recovered words here", 0.95, 3, DateTimeOffset.UtcNow);

    [Fact]
    public void RepairsOfPagesTheAuditNoLongerFlagsDoNotCountAsProgress()
    {
        const string path = @"C:\Manuals\a.pdf";
        var store = NewStore();

        // First audit flags three pages, and all three are repaired.
        store.Save(Document(path, "hash-a", Flagged(1), Flagged(2), Flagged(3)));
        foreach (var page in new[] { 1, 2, 3 })
            store.SaveRepair(Repair(path, page, "hash-a"));

        Assert.Equal(0, store.Summary().OutstandingPages);

        // A second audit, with a detector that looks at more of the page, flags two *different*
        // pages as well. The old three repairs are still there and still valid; the new two are
        // work outstanding, and saying otherwise stops the repair from ever running.
        store.Save(Document(
            path, "hash-a", Flagged(1), Flagged(2), Flagged(3), Flagged(7), Flagged(9)));

        Assert.Equal(2, store.Summary().OutstandingPages);
        Assert.Equal(2, store.Flagged().Single().OutstandingPages);
    }

    [Fact]
    public void APageThatStopsBeingFlaggedDoesNotInflateTheRepairedCount()
    {
        const string path = @"C:\Manuals\b.pdf";
        var store = NewStore();

        store.Save(Document(path, "hash-b", Flagged(1), Flagged(2)));
        store.SaveRepair(Repair(path, 1, "hash-b"));
        store.SaveRepair(Repair(path, 2, "hash-b"));

        // The rule-segment filter stops counting a ruled table as lettering, so page 2 is sound
        // after all. Its recovered text is kept - it cost GPU time and harms nothing - but the
        // document is now one flagged page, fully repaired, not two.
        store.Save(Document(path, "hash-b", Flagged(1), Fine(2)));

        var summary = store.Summary();
        Assert.Equal(1, summary.FlaggedPages);
        Assert.Equal(1, summary.RepairedPages);
        Assert.Equal(0, summary.OutstandingPages);
    }

    [Fact]
    public void WorkIsNotHiddenByRepairsOfPagesThatAreNoLongerFlagged()
    {
        // The shape of the real failure, and the only one of these tests that the old arithmetic
        // gets wrong. The audit used to flag pages 4, 5 and 6 and they were repaired. It now flags
        // 1, 2 and 3 instead - a different gate, a different opinion - and none of those has been
        // touched. Counting all six repairs and subtracting gives zero, so `repair` announced that
        // every flagged page had already been recovered and did nothing at all.
        const string path = @"C:\Manuals\d.pdf";
        var store = NewStore();

        store.Save(Document(path, "hash-d", Fine(1), Fine(2), Fine(3), Flagged(4), Flagged(5), Flagged(6)));
        foreach (var page in new[] { 4, 5, 6 })
            store.SaveRepair(Repair(path, page, "hash-d"));

        store.Save(Document(path, "hash-d", Flagged(1), Flagged(2), Flagged(3), Fine(4), Fine(5), Fine(6)));

        var summary = store.Summary();
        Assert.Equal(3, summary.FlaggedPages);
        Assert.Equal(0, summary.RepairedPages);
        Assert.Equal(3, summary.OutstandingPages);
        Assert.Equal(3, store.Flagged().Single().OutstandingPages);
    }

    [Fact]
    public void AFreshlyAuditedDocumentIsAllOutstanding()
    {
        var store = NewStore();
        store.Save(Document(@"C:\Manuals\c.pdf", "hash-c", Flagged(1), Flagged(2), Flagged(3)));

        Assert.Equal(3, store.Summary().OutstandingPages);
    }
}
