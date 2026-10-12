using ManualForge.Core.Auditing;
using ManualForge.Shell.ViewModels;

namespace ManualForge.Core.Tests;

/// <summary>
/// The Doctor tab saying what it is doing and what to do next (#74): an audit that counts the files it
/// only hashes, and a Show that a chosen document enables, starting at its first flagged page.
/// </summary>
public sealed class DoctorGuidanceTests
{
    [Fact]
    public void AnAuditOfAnUnchangedLibrarySaysHowFarItHasGot()
    {
        var status = DoctorViewModel.AuditStatus(new DoctorProgress(@"D:\Manuals\a.pdf", 212, 630, 0, 0, DocumentsUnchanged: 210));

        Assert.Equal(
            "Checking 212 of 630 documents: 210 unchanged since the last audit, 2 audited, 0 page(s) flagged so far. Nothing is being changed.",
            status);
    }

    [Theory]
    [InlineData("14,16,21-26", 16, true)]
    [InlineData("14,16,21-26", 23, true)]
    [InlineData("14,16,21-26", 15, false)]
    [InlineData("", 1, false)]
    public void APageIsFlaggedWhenItsListSaysSo(string ranges, int page, bool flagged)
        => Assert.Equal(flagged, DoctorViewModel.IsFlagged(ranges, page));

    [Fact]
    public void ThePictureSaysWhetherAnythingNeedsDoing()
    {
        var repair = new PageRepair(@"D:\Manuals\3585B-SM-V1.pdf", 14, "h", 300, "HP 3466A", 0.9, 42,
            new DateTimeOffset(2026, 10, 11, 7, 0, 0, TimeSpan.Zero));

        var recovered = DoctorViewModel.ExplainPageState(flagged: true, repair);
        Assert.Contains("red boxes mark the black shapes the audit judged to be lettering", recovered, StringComparison.Ordinal);
        Assert.Contains("already read this page and recovered 42 word(s)", recovered, StringComparison.Ordinal);
        Assert.Contains("nothing to do", recovered, StringComparison.Ordinal);

        var waiting = DoctorViewModel.ExplainPageState(flagged: true, repair: null);
        Assert.Contains("has not been recovered yet", waiting, StringComparison.Ordinal);
        Assert.Contains("press Recover text", waiting, StringComparison.Ordinal);

        Assert.Contains("did not flag this page", DoctorViewModel.ExplainPageState(flagged: false, repair: null), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("14,16,21-26", 14)]
    [InlineData("1,3,8", 1)]
    [InlineData(" 330-335", 330)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AChosenDocumentStartsAtItsFirstFlaggedPage(string? ranges, int? first)
        => Assert.Equal(first, DoctorViewModel.FirstPageOf(ranges));
}
