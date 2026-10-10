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

    [Fact]
    public void AFirstRunOnACardStartsAtOneAndClimbs()
    {
        var tuning = GpuTuning.ForCard(Card, null, fixedConcurrency: null);
        Assert.NotNull(tuning.Tuner);
        Assert.Equal(1, tuning.Tuner.Level);
        Assert.Equal(GpuTuning.MaximumInFlight, tuning.Tuner.Ceiling);
        Assert.Contains("first run on this card", tuning.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ALaterRunStartsWhereTheCardSettledAndStaysOffItsCliff()
    {
        var profile = new GpuProfile { Name = Card.Name, TotalMiB = Card.TotalMiB, BestConcurrency = 3, UnsafeConcurrency = 5 };
        var tuning = GpuTuning.ForCard(Card, profile, fixedConcurrency: null);

        Assert.Equal(3, tuning.Tuner!.Level);
        Assert.Equal(4, tuning.Tuner.Ceiling);
        Assert.Equal(3, tuning.Options(2).GpuConcurrency);
    }

    [Fact]
    public void WhatARunLearnedIsRemembered()
    {
        var store = Store();
        var tuning = GpuTuning.ForCard(Card, null, fixedConcurrency: null);

        tuning.Remember(store, pagesPerMinute: 100);
        Assert.Equal(1, store.Find(Card)?.BestConcurrency);
        Assert.Equal(100, store.Find(Card)?.RunPagesPerMinute);

        // A second run's speed is averaged in, and a run too short to measure leaves it alone.
        GpuTuning.ForCard(Card, store.Find(Card), null).Remember(store, pagesPerMinute: 140);
        Assert.Equal(120, store.Find(Card)?.RunPagesPerMinute);
        GpuTuning.ForCard(Card, store.Find(Card), null).Remember(store, pagesPerMinute: null);
        Assert.Equal(120, store.Find(Card)?.RunPagesPerMinute);
    }

    [Fact]
    public void ACliffOnceFoundIsNeverForgotten()
    {
        var store = Store();
        store.Update(Card, p => p with { UnsafeConcurrency = 4 });

        // A fixed run learns nothing about the cliff, and must not clear what is known.
        GpuTuning.ForCard(Card, store.Find(Card), fixedConcurrency: 2).Remember(store, null);
        Assert.Equal(4, store.Find(Card)?.UnsafeConcurrency);
    }

    [Fact]
    public void WithNoCardIdentifiedNothingIsWritten()
    {
        var store = Store();
        GpuTuning.ForCard(null, null, fixedConcurrency: null).Remember(store, 100);
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
