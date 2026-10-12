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
    [InlineData("14,16,21-26", 14)]
    [InlineData("1,3,8", 1)]
    [InlineData(" 330-335", 330)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AChosenDocumentStartsAtItsFirstFlaggedPage(string? ranges, int? first)
        => Assert.Equal(first, DoctorViewModel.FirstPageOf(ranges));
}
