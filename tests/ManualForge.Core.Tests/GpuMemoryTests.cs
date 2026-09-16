using ManualForge.Core.Ocr;

namespace ManualForge.Core.Tests;

/// <summary>
/// Choosing how many pages to put on the GPU at once is not an optimisation with a gentle curve.
/// Under what VRAM holds, more pages is faster: 54.9, 78.1 and 83.3 pages a minute at one, two and
/// three. Over it, the driver spills to system memory over PCIe and throughput collapses to 7.6 —
/// an order of magnitude slower than serial, with no exception raised to say why. Three was both
/// the fastest setting and the one that tipped over, which is why the default stops at two.
///
/// So the failure this guards against is asymmetric, and the arithmetic errs downwards.
/// </summary>
public class GpuMemoryTests
{
    private static GpuMemory Card(int totalMiB, int usedMiB) => new(totalMiB, usedMiB, "RTX 3060 Ti");

    [Fact]
    public void FreeIsWhatIsLeftAfterEverybodyElse()
    {
        // The desktop, a browser and whatever else is running hold 2.6-2.9 GB of this card before
        // any work starts, which is why the total is not the number that matters.
        var card = Card(8192, 2860);
        Assert.Equal(5332, card.FreeMiB);
    }

    [Fact]
    public void AFullCardNeverReportsNegativeSpace()
        => Assert.Equal(0, Card(8192, 9000).FreeMiB);

    [Fact]
    public void WithNothingToGoOnItRecognisesOnePageAtATime()
    {
        // No NVIDIA card, no nvidia-smi, or a CPU run. One is the only safe answer: on CPU the
        // work is already spread across every core inside the engine, and outer concurrency would
        // only contend with it.
        Assert.Equal(1, GpuMemoryProbe.ConcurrencyFor(null));
    }

    [Fact]
    public void ACardWithNothingSpareStaysAtOne()
    {
        // 7,950 of 8,192 MiB is where the collapse was measured.
        Assert.Equal(1, GpuMemoryProbe.ConcurrencyFor(Card(8192, 7950)));
    }

    [Fact]
    public void HeadroomIsKeptBackForADesktopThatGrows()
    {
        // Free space equal to the reserve alone buys nothing: a desktop that opens one more window
        // must not push a thirty-hour run over the edge.
        Assert.Equal(1, GpuMemoryProbe.ConcurrencyFor(Card(8192, 8192 - GpuMemoryProbe.ReserveMiB)));
    }

    [Theory]
    // free = total - used, then the reserve comes off, then whole pages of budget.
    [InlineData(8192, 7000, 1)]   // 1,192 free: 680 after the reserve, not a whole page of budget
    [InlineData(8192, 6900, 2)]   // 1,292 free: 780 after the reserve, one extra page
    [InlineData(8192, 6000, 2)]   // 2,192 free: room for more, but the ceiling is two
    [InlineData(8192, 2860, 2)]   // the real idle desktop on this machine
    [InlineData(24576, 1000, 2)]  // a much larger card still stops at the ceiling
    public void ConcurrencyRisesWithFreeMemoryAndStopsAtTheCeiling(int total, int used, int expected)
        => Assert.Equal(expected, GpuMemoryProbe.ConcurrencyFor(Card(total, used)));

    [Fact]
    public void TheDefaultCeilingIsTwoAndThreeHasToBeAskedFor()
    {
        // Three was the fastest measured - 83.3 pages a minute against 78.1 - and also the setting
        // that tipped this card into spilling once the pages were varied enough. Six per cent of
        // upside against a factor of ten of downside belongs behind an explicit request.
        Assert.Equal(2, GpuMemoryProbe.ConcurrencyFor(Card(49152, 0)));
        Assert.Equal(3, GpuMemoryProbe.ConcurrencyFor(Card(49152, 0), ceiling: 3));
        Assert.Equal(1, GpuMemoryProbe.ConcurrencyFor(Card(49152, 0), ceiling: 1));
    }

    [Fact]
    public void ANonsensicalCeilingIsTreatedAsOne()
        => Assert.Equal(1, GpuMemoryProbe.ConcurrencyFor(Card(8192, 0), ceiling: 0));

    [Fact]
    public void ProbingThisMachineEitherWorksOrSaysNothing()
    {
        // Runs on whatever the build agent is. The contract is only that it never throws and never
        // reports something impossible.
        var card = GpuMemoryProbe.TryRead();
        if (card is null)
            return;

        Assert.True(card.TotalMiB > 0, "a card that reports zero total memory is not a useful answer");
        Assert.InRange(card.UsedMiB, 0, card.TotalMiB * 2);
        Assert.InRange(GpuMemoryProbe.ConcurrencyFor(card), 1, 2);
    }
}
