using ManualForge.Core.Ocr;
using ManualForge.Core.Pipeline;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// Pages in flight chosen while running, on curves shaped like the two cards measured in #31: a
/// rise, a knee, and a cliff where the card spills to system memory.
/// </summary>
public sealed class ConcurrencyControllerTests
{
    /// <summary>
    /// Drives a controller against a simulated card: throughput and memory as a function of pages in
    /// flight. Returns the levels it ran at, in order.
    /// </summary>
    private static List<int> Run(
        ConcurrencyController controller, Func<int, double> pagesPerMinute, Func<int, int> usedMiB,
        int totalMiB = 16_000, int windows = 40, Func<int, int>? usedAtWindow = null)
    {
        var levels = new List<int>();
        for (var i = 0; i < windows; i++)
        {
            var level = controller.Level;
            levels.Add(level);
            var used = usedAtWindow?.Invoke(i) is { } extra and > 0 ? usedMiB(level) + extra : usedMiB(level);
            var memory = new GpuMemory(totalMiB, Math.Min(totalMiB, used), "Simulated");
            var minutes = 12 / pagesPerMinute(level);
            controller.Observe(new ConcurrencyWindow(level, 12, TimeSpan.FromMinutes(minutes), memory));
        }

        return levels;
    }

    // The 16 GB card: 50, 75, 87, 91 pages a minute at one to four pages, and memory rising 2 GB a page.
    private static double Big(int level) => level switch { 1 => 50, 2 => 75, 3 => 87, 4 => 91, 5 => 92, _ => 10 };

    private static int BigMemory(int level) => 6_000 + 2_000 * level;

    [Fact]
    public void ItClimbsWhileEachStepPaysAndStopsAtTheKnee()
    {
        var controller = new ConcurrencyController(start: 1, maximum: 8);

        var levels = Run(controller, Big, BigMemory);

        // 1 -> 2 -> 3 all pay by more than 5%; 4 pays under 5% (91 against 87) and is given back.
        Assert.True(levels[^1] == 3, string.Join(",", levels));
        Assert.Contains(4, levels);
        Assert.Equal(3, controller.BestLevel);
    }

    [Fact]
    public void AStepOverTheCliffIsTakenBackAndNotTriedAgain()
    {
        // The 8 GB card: two pages fine, three spills - and memory gives no warning, as it gave
        // none there (7,613, 7,655 and 7,709 MiB at one, two and three pages).
        var controller = new ConcurrencyController(start: 1, maximum: 8);

        var levels = Run(controller, l => l switch { 1 => 50, 2 => 78, _ => 7.6 }, l => 6_500 + 50 * l, totalMiB: 8_000);

        Assert.True(levels[^1] == 2 && controller.UnsafeFound == 3, string.Join(",", levels) + " unsafe " + controller.UnsafeFound);
    }

    [Fact]
    public void FreeMemoryInTheReserveStepsDownBeforeTheDriverPages()
    {
        // Throughput still rising, but four pages leave less than 5% of the card free.
        var controller = new ConcurrencyController(start: 3, maximum: 8);

        var levels = Run(controller, Big, l => l >= 4 ? 15_700 : BigMemory(l), totalMiB: 16_300);

        Assert.Equal(3, levels[^1]);
        Assert.Equal(4, controller.UnsafeFound);
    }

    [Fact]
    public void MemoryTakenByAnotherProgramMidRunStepsItDown()
    {
        // Settled at three; then something else on the desktop takes 4 GB.
        var controller = new ConcurrencyController(start: 3, maximum: 3);

        var levels = Run(controller, Big, BigMemory, totalMiB: 16_000, usedAtWindow: i => i >= 10 ? 4_000 : 0);

        Assert.Equal(3, levels[5]);
        Assert.True(levels[^1] < 3);

        // An afternoon's browser is not a fact about the card.
        Assert.Null(controller.UnsafeFound);
        Assert.Equal(3, controller.BestLevel);
    }

    [Fact]
    public void ATightCardThatShowsNoRoomIsNotClimbed()
    {
        // Two pages leave too little free for the growth the last step showed: it stays put rather
        // than finding the cliff by falling off it.
        var controller = new ConcurrencyController(start: 1, maximum: 8);

        var levels = Run(controller, l => l switch { 1 => 50, 2 => 78, _ => 7.6 }, l => 5_000 + 1_000 * l, totalMiB: 8_000);

        Assert.DoesNotContain(3, levels);
        Assert.Equal(2, controller.BestLevel);
    }

    [Fact]
    public void ACardRememberedAsUnsafeIsNeverStartedOrClimbedThere()
    {
        var controller = new ConcurrencyController(start: 5, maximum: 8, knownUnsafe: 3);

        var levels = Run(controller, Big, BigMemory);

        Assert.All(levels, l => Assert.True(l <= 2));
    }

    [Fact]
    public void WithNoMemoryReadingItStillClimbsOnThroughputAlone()
    {
        var controller = new ConcurrencyController(start: 1, maximum: 8);
        var levels = new List<int>();
        for (var i = 0; i < 40; i++)
        {
            levels.Add(controller.Level);
            controller.Observe(new ConcurrencyWindow(controller.Level, 12, TimeSpan.FromMinutes(12 / Big(controller.Level)), null));
        }

        Assert.Equal(3, levels[^1]);
    }

    [Fact]
    public void TheReserveIsAShareOfTheCardWithAFloor()
    {
        Assert.Equal(512, ConcurrencyController.ReserveMiB(new GpuMemory(8_000, 0, null)));
        Assert.Equal(815, ConcurrencyController.ReserveMiB(new GpuMemory(16_303, 0, null)));
        Assert.Equal(1_638, ConcurrencyController.ReserveMiB(new GpuMemory(32_768, 0, null)));
    }
}
