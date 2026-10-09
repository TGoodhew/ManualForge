using ManualForge.Core.Ocr;

namespace ManualForge.Core.Tests;

/// <summary>
/// Choosing how many pages to put on the GPU at once is not an optimisation with a gentle curve.
/// Under what VRAM holds, more pages is faster: 54.9, 78.1 and 83.3 pages a minute at one, two and
/// three on an 8 GB card. Over it, the driver spills to system memory over PCIe and throughput
/// collapses to 7.6 — an order of magnitude slower than serial, with no exception raised to say
/// why. A 16 GB card did the same one step higher: 91.4 at four pages, 13.7 at six. On each card
/// the fastest setting was the one at the edge, which is why each default stops one short of it.
///
/// So the failure this guards against is asymmetric, and the arithmetic errs downwards.
/// </summary>
public class GpuMemoryTests
{
    private const int EightGb = 8192;      // RTX 3060 Ti
    private const int SixteenGb = 16303;   // RTX 5070 Ti, as nvidia-smi reports it

    private static GpuMemory Card(int totalMiB, int usedMiB) => new(totalMiB, usedMiB, "test card");

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
    [InlineData(EightGb, 4800, 1)]      // 3,392 free: 2,880 after the reserve, not a whole page of budget
    [InlineData(EightGb, 4600, 2)]      // 3,592 free: 3,080 after the reserve, one extra page
    [InlineData(EightGb, 2860, 2)]      // the 3060 Ti's real idle desktop
    [InlineData(SixteenGb, 2600, 3)]    // the 5070 Ti's real idle desktop: room for five, ceiling is three
    [InlineData(SixteenGb, 10600, 2)]   // the same card with a game holding 8 GB: two, not three
    [InlineData(SixteenGb, 12700, 2)]   // 3,603 free: 3,091 after the reserve, one extra page
    [InlineData(SixteenGb, 13400, 1)]   // 2,903 free: 2,391 after the reserve, not a whole page
    public void ConcurrencyRisesWithFreeMemoryAndStopsAtTheCeiling(int total, int used, int expected)
        => Assert.Equal(expected, GpuMemoryProbe.ConcurrencyFor(Card(total, used)));

    [Fact]
    public void AnEightGigabyteCardStopsAtTwo()
    {
        // Three was the fastest measured there - 83.3 pages a minute against 78.1 - and also the
        // setting that tipped the card into spilling once the pages were varied enough. Six per
        // cent of upside against a factor of ten of downside belongs behind an explicit request.
        Assert.Equal(2, GpuMemoryProbe.ConcurrencyFor(Card(EightGb, 0)));
        Assert.Equal(3, GpuMemoryProbe.ConcurrencyFor(Card(EightGb, 0), ceiling: 3));
    }

    [Fact]
    public void ASixteenGigabyteCardStopsAtThreeAndFourHasToBeAskedFor()
    {
        // Four was the fastest measured there, 91.4 pages a minute, but peaked at 15.7 GB of 16.3:
        // nothing left for a browser opened partway through a night's run. Six had already fallen
        // to 13.7. Three peaked at 12.4-13.3 GB.
        Assert.Equal(3, GpuMemoryProbe.ConcurrencyFor(Card(SixteenGb, 0)));
        Assert.Equal(4, GpuMemoryProbe.ConcurrencyFor(Card(SixteenGb, 0), ceiling: 4));
        Assert.Equal(1, GpuMemoryProbe.ConcurrencyFor(Card(SixteenGb, 0), ceiling: 1));
    }

    [Fact]
    public void ALargerCardThanAnyMeasuredStillStopsAtThree()
        => Assert.Equal(3, GpuMemoryProbe.ConcurrencyFor(Card(49152, 0)));

    [Theory]
    // 16 GB cards report a little under 16,384 MiB, and a 12 GB card is not one of them.
    [InlineData(EightGb, 2)]
    [InlineData(12288, 2)]
    [InlineData(GpuMemoryProbe.LargeCardMiB, 3)]
    [InlineData(SixteenGb, 3)]
    public void TheCeilingFollowsTheCardsTotalNotWhatIsFree(int total, int expected)
        => Assert.Equal(expected, GpuMemoryProbe.CeilingFor(Card(total, total)));

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
        Assert.InRange(GpuMemoryProbe.ConcurrencyFor(card), 1, 3);
    }
}
