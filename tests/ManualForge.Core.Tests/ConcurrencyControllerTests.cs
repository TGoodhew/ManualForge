using ManualForge.Core.Pipeline;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// Pages in flight chosen while running, on curves shaped like the cards measured in #31: a rise, a
/// knee, and an edge past which the card moves this process's memory out to system RAM and slows.
/// </summary>
public sealed class ConcurrencyControllerTests
{
    /// <summary>What a healthy run holds in system RAM on the RTX 5070 Ti, at any level under the edge.</summary>
    private const int Steady = 76;

    /// <summary>
    /// Drives a controller against a simulated card: throughput and memory gone out to system RAM, as
    /// functions of pages in flight and of the window. Returns the levels it ran at, in order.
    /// </summary>
    private static List<int> Run(
        ConcurrencyController controller, Func<int, double> pagesPerMinute, Func<int, int, int?> spilledMiB,
        int windows = 40)
    {
        var levels = new List<int>();
        for (var i = 0; i < windows; i++)
        {
            var level = controller.Level;
            levels.Add(level);
            var minutes = 12 / pagesPerMinute(level);
            controller.Observe(new ConcurrencyWindow(level, 12, TimeSpan.FromMinutes(minutes), spilledMiB(level, i)));
        }

        return levels;
    }

    /// <summary>The 16 GB card on 9 October, with room to spare: 50.4, 74.9, 86.8, 91.4, and little after.</summary>
    private static double Roomy(int level) => level switch { 1 => 50.4, 2 => 74.9, 3 => 86.8, 4 => 91.4, _ => 92 };

    [Fact]
    public void ItClimbsWhileEachStepPaysAndGivesBackOneThatDoesNot()
    {
        var controller = new ConcurrencyController(start: 1, maximum: 8);

        var levels = Run(controller, Roomy, (_, _) => Steady);

        // 4 pays 5.3% over 3; 5 pays under 1% over 4 and is given back.
        Assert.True(levels[^1] == 4, string.Join(",", levels));
        Assert.Contains(5, levels);
        Assert.Equal(4, controller.BestLevel);
        Assert.Null(controller.UnsafeFound);
    }

    [Fact]
    public void ACardThatLooksFullButHasNotSpilledIsStillClimbed()
    {
        // The first version stepped back here: the arena fills the card, so free memory was under the
        // reserve at two pages, and 85.8 pages a minute at three was given up for 54.6 (10 Oct).
        var controller = new ConcurrencyController(start: 1, maximum: 8);

        var levels = Run(controller, l => l switch { 1 => 50, 2 => 75, 3 => 86, _ => 86 }, (_, _) => Steady);

        Assert.Equal(3, levels[^1]);
        Assert.Equal(3, controller.BestLevel);
    }

    [Fact]
    public void MemoryGoingOutOnAStepUpTakesItBackAndMarksTheCard()
    {
        // The 5070 Ti on 10 October with the desktop holding 1.5 GB: four pages put 322 MiB out to
        // system RAM and halved throughput (85.8 to 46.2). Not quite half, so only the memory says so.
        var controller = new ConcurrencyController(start: 1, maximum: 8);

        var levels = Run(
            controller,
            l => l switch { 1 => 50, 2 => 75, 3 => 85.8, _ => 46.2 },
            (l, _) => l >= 4 ? 322 : Steady);

        Assert.True(levels[^1] == 3, string.Join(",", levels));
        Assert.Equal(4, controller.UnsafeFound);
        Assert.Equal(3, controller.BestLevel);
    }

    [Fact]
    public void WithoutTheCounterACollapseOnAStepUpIsStillCaught()
    {
        // The 8 GB card: two pages fine, three fell to 7.6 pages a minute. No spill reading at all,
        // as on a machine where the counter cannot be read.
        var controller = new ConcurrencyController(start: 1, maximum: 8);

        var levels = Run(controller, l => l switch { 1 => 50, 2 => 78, _ => 7.6 }, (_, _) => null);

        Assert.True(levels[^1] == 2 && controller.UnsafeFound == 3, string.Join(",", levels) + " unsafe " + controller.UnsafeFound);
    }

    [Fact]
    public void ASpillAfterALevelHadBeenFineLowersTheNextStartButDoesNotMarkTheCard()
    {
        // Settled at three; ten windows in, something else on the desktop takes the card, and this
        // run's memory starts going out.
        var controller = new ConcurrencyController(start: 3, maximum: 3);

        var levels = Run(controller, Roomy, (_, i) => i >= 10 ? 900 : Steady);

        Assert.Equal(3, levels[5]);
        Assert.Equal(2, levels[^1]);

        // An afternoon's game is not a fact about the card: the next run starts at two and may climb.
        Assert.Null(controller.UnsafeFound);
        Assert.Equal(2, controller.BestLevel);
    }

    [Fact]
    public void WhatWentOutAndStayedOutDoesNotKeepPushingItDown()
    {
        // The arena does not hand back what was moved out. After the step down, 322 MiB is still out;
        // what matters is that no more goes.
        var controller = new ConcurrencyController(start: 1, maximum: 8);
        var wentOut = false;

        var levels = Run(
            controller,
            l => l switch { 1 => 50, 2 => 75, 3 => 85.8, _ => 46.2 },
            (l, _) => (wentOut |= l >= 4) ? 322 : Steady);

        Assert.Equal(3, levels[^1]);
        Assert.DoesNotContain(2, levels.SkipWhile(l => l < 3));
    }

    [Fact]
    public void ACardRememberedAsUnsafeIsNeverStartedOrClimbedThere()
    {
        var controller = new ConcurrencyController(start: 5, maximum: 8, knownUnsafe: 3);

        var levels = Run(controller, Roomy, (_, _) => Steady);

        Assert.All(levels, l => Assert.True(l <= 2));
    }

    [Fact]
    public void AtOnePageInFlightThereIsNowhereToStepDownTo()
    {
        var controller = new ConcurrencyController(start: 1, maximum: 1);

        var levels = Run(controller, _ => 50, (_, i) => i * 500);

        Assert.All(levels, l => Assert.Equal(1, l));
    }
}
