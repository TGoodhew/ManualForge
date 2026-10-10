using ManualForge.Core.Auditing;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// The estimate `repair` prints before it starts. It used to divide every outstanding page by one
/// rate, 46 pages/min, and so was a fifth short on the jobs made mostly of scanned pages - which
/// are the long ones (#15).
/// </summary>
public sealed class RepairEstimateTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "manualforge-estimate-" + Guid.NewGuid().ToString("N"));

    public RepairEstimateTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Pdf(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "stands in for a PDF; the plan never opens it");
        return path;
    }

    private static PageAudit Flagged(int page, PageKind kind) =>
        new(page, 40, 30, 200, 2, 0, 0, false, false,
            new InkAnalysis(0.03, 0.02, 120, 9), false, PageVerdict.UnderExtracted, ["flagged"])
        { Kind = kind, EstimatedRecoverableCharacters = 100 };

    [Fact]
    public void AScannedPageCostsMoreThanADrawnOneAtEveryResolution()
    {
        foreach (var dpi in new[] { 300, 400, 600 })
            Assert.True(RepairThroughput.PagesPerMinute(PageKind.Drawn, dpi)
                        > RepairThroughput.PagesPerMinute(PageKind.Raster, dpi));

        // 600 dpi is the slowest of either kind.
        Assert.True(RepairThroughput.PagesPerMinute(PageKind.Drawn, 600) < RepairThroughput.PagesPerMinute(PageKind.Drawn, 300));
        Assert.True(RepairThroughput.PagesPerMinute(PageKind.Raster, 600) < RepairThroughput.PagesPerMinute(PageKind.Raster, 300));
    }

    [Fact]
    public void EachPageIsCostedAtItsOwnRateAndEachDocumentAddsItsOverhead()
    {
        PlannedRepairPage[] pages =
        [
            .. Enumerable.Range(1, 78).Select(n => new PlannedRepairPage("a.pdf", n, PageKind.Drawn, 300)),
            .. Enumerable.Range(1, 35).Select(n => new PlannedRepairPage("b.pdf", n, PageKind.Raster, 300)),
        ];

        var expected = TimeSpan.FromMinutes(
                78 / RepairThroughput.PagesPerMinute(PageKind.Drawn, 300)
                + 35 / RepairThroughput.PagesPerMinute(PageKind.Raster, 300))
            + RepairThroughput.PerDocument * 2;
        Assert.Equal(expected.TotalSeconds, new RepairPlan(pages, 2).Estimate.TotalSeconds, precision: 3);
    }

    [Fact]
    public void ThePlanIsThePagesTheRepairWillReadAndNoOthers()
    {
        var drawn = Pdf("figures.pdf");
        var scanned = Pdf("scan.pdf");
        using var store = new DoctorStore(Path.Combine(_directory, "doctor.db"));

        // Two drawn pages and a scanned one, in a document whose problem is drawn content.
        store.Save(new DocumentAudit(drawn, "figures", "hash-a", 10,
            [Flagged(2, PageKind.Drawn), Flagged(5, PageKind.Drawn), Flagged(7, PageKind.Raster)])
        { Verdict = DocumentVerdict.UnderExtracted });

        // A scan whose existing OCR missed lettering.
        store.Save(new DocumentAudit(scanned, "scan", "hash-b", 10,
            [Flagged(1, PageKind.Raster), Flagged(3, PageKind.Raster)])
        { Verdict = DocumentVerdict.ScannedGaps });

        // Page 5 has been repaired. The store stamps a repair with the moment it is saved.
        store.SaveRepair(new PageRepair(drawn, 5, "hash-a", 300, "LFR1", 0.9, 1, DateTimeOffset.UtcNow));

        // By default: the drawn pages not yet done. Not the scanned page, nor the scan.
        var plan = PageRepairer.Plan(store, new RepairOptions());
        Assert.Equal([(drawn, 2)], plan.Pages.Select(p => (p.Path, p.PageNumber)));
        Assert.Equal(1, plan.Documents);

        // With scans: the scanned page of the first document and both pages of the scan.
        plan = PageRepairer.Plan(store, new RepairOptions { IncludeScannedPages = true });
        Assert.Equal(
            [(drawn, 2), (drawn, 7), (scanned, 1), (scanned, 3)],
            plan.Pages.Select(p => (p.Path, p.PageNumber)).Order());
        Assert.Equal(2, plan.Documents);

        // Redoing what was repaired before a pass that starts now brings page 5 back in.
        plan = PageRepairer.Plan(store, new RepairOptions { RedoBefore = DateTimeOffset.UtcNow.AddMinutes(1) });
        Assert.Equal([2, 5], plan.Pages.Select(p => p.PageNumber).Order());

        // Named documents only, at the resolution asked for, capped by the ceiling.
        plan = PageRepairer.Plan(store, new RepairOptions
        {
            IncludeScannedPages = true, Paths = [scanned], Dpi = 900, MaximumDpi = 600,
        });
        Assert.All(plan.Pages, p => Assert.Equal(scanned, p.Path));
        Assert.All(plan.Pages, p => Assert.Equal(600, p.Dpi));
        Assert.All(plan.Pages, p => Assert.Equal(PageKind.Raster, p.Kind));
    }

    [Fact]
    public void TrimmingForgetsOnlyTheFilesThatAreGone()
    {
        var gone = Pdf("gone.pdf");
        var here = Pdf("here.pdf");
        using var store = new DoctorStore(Path.Combine(_directory, "doctor.db"));
        store.Save(new DocumentAudit(gone, "gone", "hash-a", 3, [Flagged(1, PageKind.Drawn)])
        { Verdict = DocumentVerdict.UnderExtracted });
        store.Save(new DocumentAudit(here, "here", "hash-b", 3, [Flagged(2, PageKind.Drawn)])
        { Verdict = DocumentVerdict.UnderExtracted });
        store.SaveRepair(new PageRepair(gone, 1, "hash-a", 300, "LFR1", 0.9, 1, DateTimeOffset.UtcNow));
        File.Delete(gone);

        Assert.Equal([gone], store.Missing());
        Assert.Equal(2, store.Summary().FlaggedPages);

        Assert.Equal([gone], store.TrimMissing());

        Assert.Null(store.Document(gone));
        Assert.NotNull(store.Document(here));
        Assert.Equal(1, store.Summary().FlaggedPages);
        Assert.Equal(0, store.Summary().RepairedPages);
        Assert.Empty(store.Missing());
    }

    [Fact]
    public void AFileThatHasGoneIsNotPlanned()
    {
        var gone = Pdf("gone.pdf");
        using var store = new DoctorStore(Path.Combine(_directory, "doctor.db"));
        store.Save(new DocumentAudit(gone, "gone", "hash", 3, [Flagged(1, PageKind.Drawn)])
        { Verdict = DocumentVerdict.UnderExtracted });
        File.Delete(gone);

        var plan = PageRepairer.Plan(store, new RepairOptions());

        Assert.Empty(plan.Pages);
        Assert.Equal(0, plan.Documents);
    }
}
