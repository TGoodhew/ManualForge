using ManualForge.Core.Pipeline;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// The guard on a run's pages in flight: it steps down when this process's GPU memory starts going out
/// to system RAM, and never up (#31). Readings shaped like the RTX 5070 Ti's on 10 October: a steady
/// 76 MiB out at three pages, 322 at four, 4.3 GB at six.
/// </summary>
public sealed class ConcurrencyControllerTests
{
    private const int Steady = 76;

    private static ConcurrencyWindow Window(int level, int? spilled) =>
        new(level, 12, TimeSpan.FromSeconds(10), spilled);

    /// <summary>Feeds windows with these readings, returning the levels it ran at.</summary>
    private static List<int> Run(ConcurrencyController controller, params int?[] spilled)
    {
        var levels = new List<int>();
        foreach (var reading in spilled)
        {
            levels.Add(controller.Level);
            controller.Observe(Window(controller.Level, reading));
        }

        return levels;
    }

    [Fact]
    public void AHealthyRunIsLeftAlone()
    {
        var controller = new ConcurrencyController(3);
        controller.Begin(0);

        var levels = Run(controller, Steady, Steady, Steady, Steady);

        Assert.All(levels, l => Assert.Equal(3, l));
        Assert.Null(controller.SpilledAt);
        Assert.True(controller.Watched);
    }

    [Fact]
    public void ACardThatLooksFullButHasNotSpilledIsLeftAlone()
    {
        // The arena fills the card, so a healthy run leaves under a gigabyte free. Only memory going
        // out to system RAM counts, and 76 MiB held steady from the start is not that.
        var controller = new ConcurrencyController(3);
        controller.Begin(0);

        Run(controller, Steady, Steady);

        Assert.Equal(3, controller.Level);
    }

    [Fact]
    public void MemoryGoingOutStepsItDown()
    {
        // Four pages on 10 October: 322 MiB out about 30 seconds in.
        var controller = new ConcurrencyController(4);
        controller.Begin(0);

        var levels = Run(controller, Steady, 322, 322, 322);

        Assert.Equal([4, 4, 3, 3], levels);
        Assert.Equal(4, controller.SpilledAt);
    }

    [Fact]
    public void ASpillAlreadyUnderWayByTheFirstReadingIsCaught()
    {
        // Six pages put 4.3 GB out within 8 seconds, before the first window closed. Against what the
        // process held before its first page, that is still a spill.
        var controller = new ConcurrencyController(6);
        controller.Begin(0);

        Run(controller, 4_300, 4_300);

        Assert.Equal(5, controller.Level);
        Assert.Equal(6, controller.SpilledAt);
    }

    [Fact]
    public void WhatWentOutAndStayedOutDoesNotKeepPushingItDown()
    {
        // The arena does not hand back what was moved out. Once stepped down, what counts is whether
        // more goes, not that 322 MiB is still there.
        var controller = new ConcurrencyController(4);
        controller.Begin(0);

        var levels = Run(controller, Steady, 322, 322, 330, 322);

        Assert.Equal(3, levels[^1]);
    }

    [Fact]
    public void MoreGoingOutAfterAStepDownStepsDownAgain()
    {
        var controller = new ConcurrencyController(4);
        controller.Begin(0);

        Run(controller, Steady, 322, 900, 900);

        Assert.Equal(2, controller.Level);
        Assert.Equal(4, controller.SpilledAt);
    }

    [Fact]
    public void AtOnePageInFlightThereIsNowhereToStepDownTo()
    {
        var controller = new ConcurrencyController(1);
        controller.Begin(0);

        var levels = Run(controller, 500, 1_000, 2_000);

        Assert.All(levels, l => Assert.Equal(1, l));
        Assert.Equal(1, controller.SpilledAt);
    }

    [Fact]
    public void WithoutTheCounterNothingIsDecidedAndNothingIsVouchedFor()
    {
        var controller = new ConcurrencyController(3);
        controller.Begin(null);

        Run(controller, null, null, null);

        Assert.Equal(3, controller.Level);
        Assert.False(controller.Watched);
    }

    [Fact]
    public void ItNeverStepsUp()
    {
        var controller = new ConcurrencyController(2);
        controller.Begin(500);

        // Readings below where it began - the counter dropping - are not room to climb into.
        Run(controller, 76, 0, 0, 0);

        Assert.Equal(2, controller.Level);
    }
}
