using ManualForge.Core.Ocr;

namespace ManualForge.Core.Tests;

/// <summary>
/// What the card reports. Choosing pages in flight from it is the tuner's job, and tested there
/// (ConcurrencyControllerTests): no card's figures are built in any more (#31).
/// </summary>
public class GpuMemoryTests
{
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
    public void ProbingThisMachineEitherWorksOrSaysNothing()
    {
        // Runs on whatever the build agent is. The contract is only that it never throws and never
        // reports something impossible.
        var card = GpuMemoryProbe.TryRead();
        if (card is null)
            return;

        Assert.True(card.TotalMiB > 0, "a card that reports zero total memory is not a useful answer");
        Assert.InRange(card.UsedMiB, 0, card.TotalMiB * 2);
    }

    [Fact]
    public void ReadingWhatHasSpilledEitherWorksOrSaysNothing()
    {
        // This process has likely never touched a GPU, and a build agent may have no counters at all.
        // Either is an answer of null; what it must never do is throw or report negative memory.
        var spilled = GpuMemoryProbe.TryReadSpilledMiB();
        if (spilled is { } mib)
            Assert.True(mib >= 0);

        Assert.Null(GpuMemoryProbe.TryReadSpilledMiB(processId: int.MaxValue));
    }

    [Fact]
    public void IdentifyingThisMachinesCardEitherWorksOrSaysNothing()
    {
        var gpu = GpuMemoryProbe.TryIdentify();
        if (gpu is null)
            return;

        Assert.False(string.IsNullOrWhiteSpace(gpu.Name));
        Assert.True(gpu.TotalMiB > 0);
        Assert.Equal($"{gpu.Name}|{gpu.TotalMiB}", gpu.Key);
    }
}
