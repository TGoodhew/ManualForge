using ManualForge.Core.Auditing;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// Which flagged pages a repair recognises. The case that matters is re-reading the whole corpus
/// after the recogniser improves: ten hours of GPU time, which has to survive an interruption
/// without starting over.
/// </summary>
public sealed class RepairRedoTests
{
    private static readonly DateTimeOffset PassStarted = new(2026, 9, 28, 20, 0, 0, TimeSpan.Zero);

    private static PageRepair RepairedAt(DateTimeOffset when) =>
        new("manual.pdf", 4, "hash", 400, "text", 0.9, 1, when);

    [Fact]
    public void APageNeverRepairedIsRead()
    {
        Assert.True(PageRepairer.ShouldRead(null, new RepairOptions()));
    }

    [Fact]
    public void ARepairedPageIsLeftAloneUnlessAsked()
    {
        Assert.False(PageRepairer.ShouldRead(RepairedAt(PassStarted.AddDays(-5)), new RepairOptions()));
    }

    [Fact]
    public void RedoReadsEveryPageEveryTime()
    {
        Assert.True(PageRepairer.ShouldRead(RepairedAt(PassStarted.AddMinutes(30)), new RepairOptions { Force = true }));
    }

    [Fact]
    public void RedoBeforeReadsOnlyWhatThePassHasNotReachedYet()
    {
        var options = new RepairOptions { RedoBefore = PassStarted };

        // Repaired last week, by the old recogniser: read again.
        Assert.True(PageRepairer.ShouldRead(RepairedAt(PassStarted.AddDays(-5)), options));

        // Repaired half an hour into this pass, before it was interrupted: a restart skips it.
        Assert.False(PageRepairer.ShouldRead(RepairedAt(PassStarted.AddMinutes(30)), options));
    }
}
