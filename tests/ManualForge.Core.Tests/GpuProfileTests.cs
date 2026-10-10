using ManualForge.Core.Auditing;
using ManualForge.Core.Ocr;
using ManualForge.Core.Pipeline;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// What a machine remembers about its card between runs (#31): where pages in flight settled, any
/// cliff found, and how fast runs and repairs went. Learned, not built in, so a card nobody here
/// has measured gets its own figures by being used.
/// </summary>
public sealed class GpuProfileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "manualforge-profile-" + Guid.NewGuid().ToString("N"));

    private static readonly GpuIdentity Card = new("Simulated RTX", 12_288, "999.99");

    private GpuProfileStore Store() => new(Path.Combine(_directory, "gpu-profiles.json"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ACardNeverSeenHasNoProfile()
    {
        Assert.Null(Store().Find(Card));
        Assert.Null(Store().Find(null));
    }

    [Fact]
    public void AProfileSurvivesToTheNextRun()
    {
        Store().Update(Card, p => p with { BestConcurrency = 3, UnsafeConcurrency = 5, RunPagesPerMinute = 120 });

        var found = Store().Find(Card);
        Assert.NotNull(found);
        Assert.Equal(3, found.BestConcurrency);
        Assert.Equal(5, found.UnsafeConcurrency);
        Assert.Equal(120, found.RunPagesPerMinute);
        Assert.Equal("999.99", found.Driver);
    }

    [Fact]
    public void ADifferentCardDoesNotInheritAnother()
    {
        Store().Update(Card, p => p with { BestConcurrency = 4 });
        Assert.Null(Store().Find(Card with { TotalMiB = 8_192 }));
        Assert.Null(Store().Find(Card with { Name = "Another" }));
    }

    [Fact]
    public void ADriverUpdateIsTheSameCard()
    {
        Store().Update(Card, p => p with { BestConcurrency = 4 });
        Assert.Equal(4, Store().Find(Card with { Driver = "1000.01" })?.BestConcurrency);
    }

    [Fact]
    public void AnUnreadableFileIsACardWithNothingLearned()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "gpu-profiles.json"), "{ not json");

        Assert.Null(Store().Find(Card));
        Store().Update(Card, p => p with { BestConcurrency = 2 });
        Assert.Equal(2, Store().Find(Card)?.BestConcurrency);
    }

    [Fact]
    public void OnTheCpuNothingIsTunedAndOnePageIsInFlight()
    {
        var tuning = GpuTuning.For(usingGpu: false, fixedConcurrency: null, Store());
        Assert.Null(tuning.Tuner);
        Assert.Equal(1, tuning.FixedConcurrency);
        Assert.Equal(1, tuning.Options(2).GpuConcurrency);
    }

    [Fact]
    public void AskingForAFixedNumberTurnsTuningOff()
    {
        var tuning = GpuTuning.For(usingGpu: true, fixedConcurrency: 3, Store());
        Assert.Null(tuning.Tuner);
        Assert.Equal(3, tuning.Options(2).GpuConcurrency);
    }

    /// <summary>
    /// One run on the simulated card, as the pipeline would drive it: the pages it reads, and what the
    /// spill counter says after each window. Returns where the next run will start.
    /// </summary>
    private int RunOnce(GpuProfileStore store, int pages, params int?[] spilled)
    {
        var tuning = GpuTuning.ForCard(Card, store.Find(Card), fixedConcurrency: null);
        var tuner = tuning.Tuner!;
        tuner.Begin(spilled.Length == 0 ? 0 : spilled.Any(x => x is not null) ? 0 : null);

        for (var i = 0; i < pages; i++)
            tuner.PageRead();
        foreach (var reading in spilled)
            tuner.Observe(new ConcurrencyWindow(tuner.Level, 12, TimeSpan.FromSeconds(20), reading));

        return GpuTuning.StartFor(tuning.Remember(store, pagesPerMinute: null));
    }

    [Fact]
    public void ACardNeverSeenStartsAtTheFirstGuess()
    {
        var tuning = GpuTuning.ForCard(Card, null, fixedConcurrency: null);

        Assert.Equal(GpuTuning.FirstGuess, tuning.Tuner!.Level);
        Assert.Equal(GpuTuning.FirstGuess, tuning.Options(2).GpuConcurrency);
        Assert.Contains("first try on this card", tuning.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void EachCleanRunLetsTheNextTryOneMoreUntilOneSpills()
    {
        // The 5070 Ti on 10 October: three held, four put 322 MiB out.
        var store = Store();
        int?[] clean = [76, 76, 76];

        Assert.Equal(3, RunOnce(store, 200, clean));                 // 2 held: try 3
        Assert.Equal(4, RunOnce(store, 200, clean));                 // 3 held: try 4
        Assert.Equal(3, RunOnce(store, 200, 76, 322, 322));          // 4 spilled: back to 3
        Assert.Equal(4, store.Find(Card)?.UnsafeConcurrency);

        // And there it stays: three is the most this card holds, and four is never tried again.
        Assert.Equal(3, RunOnce(store, 200, clean));
        Assert.Equal(3, RunOnce(store, 200, clean));
        Assert.Contains("the most this card holds", GpuTuning.ForCard(Card, store.Find(Card), null).Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ASpillAtASettingTheCardHasHeldIsNotHeldAgainstIt()
    {
        // Settled at three, with four known to spill. A game takes the card one afternoon and three
        // spills: the next run starts lower, and climbs back, because three is not ruled out.
        var store = Store();
        store.Update(Card, p => p with { BestConcurrency = 3, UnsafeConcurrency = 4 });

        Assert.Equal(3, RunOnce(store, 200, 76, 900, 900));
        Assert.Equal(2, store.Find(Card)?.BestConcurrency);
        Assert.Equal(4, store.Find(Card)?.UnsafeConcurrency);
    }

    [Fact]
    public void AShortCleanRunVouchesForNothing()
    {
        // Thirty pages have not met a big enough page to say a setting holds.
        var store = Store();

        Assert.Equal(GpuTuning.FirstGuess, RunOnce(store, 30, 76, 76));
        Assert.Null(store.Find(Card)?.BestConcurrency);
    }

    [Fact]
    public void AShortRunThatSpillsIsStillBelieved()
    {
        var store = Store();

        Assert.Equal(1, RunOnce(store, 30, 900));
        Assert.Equal(2, store.Find(Card)?.UnsafeConcurrency);
    }

    [Fact]
    public void ARunThatCouldNotReadTheCounterLearnsNothingAboutTheCard()
    {
        var store = Store();

        Assert.Equal(GpuTuning.FirstGuess, RunOnce(store, 500, null, null, null));
        Assert.Null(store.Find(Card)?.BestConcurrency);
    }

    [Fact]
    public void ItNeverGoesPastTheMaximum()
    {
        var store = Store();
        store.Update(Card, p => p with { BestConcurrency = GpuTuning.MaximumInFlight });

        Assert.Equal(GpuTuning.MaximumInFlight, GpuTuning.StartFor(store.Find(Card)));
    }

    [Fact]
    public void ARunsSpeedIsAveragedWithTheLast()
    {
        var store = Store();
        GpuTuning.ForCard(Card, null, null).Remember(store, pagesPerMinute: 100);
        Assert.Equal(100, store.Find(Card)?.RunPagesPerMinute);

        GpuTuning.ForCard(Card, store.Find(Card), null).Remember(store, pagesPerMinute: 140);
        Assert.Equal(120, store.Find(Card)?.RunPagesPerMinute);

        // A run too short to measure leaves it alone.
        GpuTuning.ForCard(Card, store.Find(Card), null).Remember(store, pagesPerMinute: null);
        Assert.Equal(120, store.Find(Card)?.RunPagesPerMinute);
    }

    [Fact]
    public void AFixedRunLeavesWhatIsKnownAboutTheCardAlone()
    {
        var store = Store();
        store.Update(Card, p => p with { BestConcurrency = 3, UnsafeConcurrency = 4 });

        GpuTuning.ForCard(Card, store.Find(Card), fixedConcurrency: 6).Remember(store, 50);

        Assert.Equal(3, store.Find(Card)?.BestConcurrency);
        Assert.Equal(4, store.Find(Card)?.UnsafeConcurrency);
    }

    [Fact]
    public void WithNoCardIdentifiedNothingIsWritten()
    {
        var store = Store();
        Assert.Null(GpuTuning.ForCard(null, null, fixedConcurrency: null).Remember(store, 100));
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public void TheLearnedRunSpeedIsWhatEstimatesUse()
    {
        var basis = new ThroughputBasis(120, "as measured on this Simulated RTX");
        Assert.Equal(1.0, basis.HoursFor(7_200), 6);
        Assert.Contains("120 pages/min", basis.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARepairRateIsLearnedOnlyFromEnoughPages()
    {
        var learned = RepairThroughput.Learn(
            new Dictionary<string, double>(),
            [
                new RepairTiming(PageKind.Drawn, 300, 60, TimeSpan.FromMinutes(1)),   // 60 pages/min
                new RepairTiming(PageKind.Raster, 300, 5, TimeSpan.FromSeconds(5)),   // too few to say
            ]);

        Assert.Equal(60, learned["Drawn/300"], 6);
        Assert.False(learned.ContainsKey("Raster/300"));
    }

    [Fact]
    public void ANewRepairRateIsAveragedWithTheLast()
    {
        var learned = RepairThroughput.Learn(
            new Dictionary<string, double> { ["Raster/600"] = 20 },
            [new RepairTiming(PageKind.Raster, 600, 40, TimeSpan.FromMinutes(1))]);

        Assert.Equal(30, learned["Raster/600"], 6);
    }

    [Fact]
    public void TheEstimateUsesThisCardsRatesAndTheTableForTheRest()
    {
        var profile = new GpuProfile
        {
            Name = Card.Name,
            TotalMiB = Card.TotalMiB,
            RepairPagesPerMinute = new() { ["Drawn/300"] = 60 },
        };

        Assert.Equal(60, RepairThroughput.PagesPerMinute(PageKind.Drawn, 300, profile));
        Assert.Equal(60, RepairThroughput.PagesPerMinute(PageKind.Drawn, 250, profile));
        Assert.Equal(
            RepairThroughput.PagesPerMinute(PageKind.Raster, 300),
            RepairThroughput.PagesPerMinute(PageKind.Raster, 300, profile));

        var pages = Enumerable.Range(1, 60).Select(n => new PlannedRepairPage("a.pdf", n, PageKind.Drawn, 300)).ToArray();
        Assert.Equal(1.0, new RepairPlan(pages, 1).EstimateOn(profile).TotalMinutes, 6);
    }

    [Fact]
    public void ARepairIsRememberedAgainstTheCardItRanOn()
    {
        var store = Store();
        var report = new RepairReport(
            1, 61, 0, 0, 0, 0, 0, 0.9, TimeSpan.FromMinutes(1),
            [new RepairTiming(PageKind.Drawn, 300, 60, TimeSpan.FromMinutes(1))]);

        RepairThroughput.Remember(store, Card, report);
        RepairThroughput.Remember(store, null, report);   // no card seen: nothing to file it under

        Assert.Equal(60, store.Find(Card)?.RepairPagesPerMinute["Drawn/300"]);
    }
}
