using ManualForge.Core.Auditing;
using ManualForge.Shell;
using ManualForge.Shell.ViewModels;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// The doctor tab driven with no window, no GPU and no PDF.
///
/// <para>
/// The behaviour worth asserting is all about the expensive button. Which documents are ticked when
/// the list arrives, what happens to the ones already done, that stopping keeps what was recovered,
/// and that the tab does not claim more than it did. Clicking through a repair to find out takes
/// twenty minutes of GPU time per attempt.
/// </para>
/// </summary>
public sealed class DoctorViewModelTests
{
    private const string Root = @"C:\Manuals";

    private static DoctorFinding Finding(
        string title,
        DocumentVerdict verdict,
        int pages = 110,
        int flagged = 75,
        int drawn = 75,
        int repaired = 0) =>
        new(
            Path.Combine(Root, title + ".pdf"), title, pages, flagged, drawn, repaired,
            flagged * 200, 300, verdict, "19-23,30");

    private static DoctorStanding Standing(params DoctorFinding[] findings) =>
        new(
            new AuditSummary(
                DocumentsAudited: 579,
                DocumentsFlagged: findings.Length,
                FlaggedPages: findings.Sum(f => (long)f.FlaggedPages),
                DrawnPages: findings.Sum(f => (long)f.DrawnPages),
                RepairedPages: findings.Sum(f => (long)f.RepairedPages)),
            findings);

    private sealed class FakeDoctor : IDoctorService
    {
        public DoctorStanding Standing { get; set; } = DoctorStanding.None;

        public List<string> Repaired { get; } = [];

        public RepairOptions? LastOptions { get; private set; }

        public Func<IProgress<RepairProgress>?, CancellationToken, Task<RepairReport>>? OnRepair { get; set; }

        public byte[]? Picture { get; set; }

        public bool HasAudit(string root) => Standing.HasAudit;

        public Task<DoctorStanding> ReadAsync(string root, CancellationToken cancellationToken) =>
            Task.FromResult(Standing);

        public Task<DoctorStanding> AuditAsync(
            string root, DoctorOptions options, IProgress<DoctorProgress>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new DoctorProgress(@"C:\Manuals\a.pdf", 1, 2, 1, 4));
            progress?.Report(new DoctorProgress(@"C:\Manuals\b.pdf", 2, 2, 1, 75));
            return Task.FromResult(Standing);
        }

        public Task<RepairReport> RepairAsync(
            string root, IReadOnlyList<string> paths, RepairOptions options,
            IProgress<RepairProgress>? progress, CancellationToken cancellationToken)
        {
            Repaired.AddRange(paths);
            LastOptions = options;

            return OnRepair?.Invoke(progress, cancellationToken)
                   ?? Task.FromResult(new RepairReport(
                       paths.Count, 75, 0, 0, 0, 3_291, 26_474, 0.976, TimeSpan.FromMinutes(1.3)));
        }

        public Task<byte[]?> ExplainAsync(string path, int pageNumber, CancellationToken cancellationToken) =>
            Task.FromResult(Picture);
    }

    private static DoctorViewModel New(FakeDoctor service) =>
        new(service, InlineDispatcher.Instance) { Folder = Root };

    [Fact]
    public async Task ReadingTheAuditNeedsNoAuditRun()
    {
        // The whole point of splitting these: a corpus-wide audit is a command-line job that takes
        // hours, and the tab has to be useful over its results without repeating it.
        var service = new FakeDoctor
        {
            Standing = Standing(Finding("54845A Programmer", DocumentVerdict.UnderExtracted)),
        };

        var doctor = New(service);
        await doctor.RefreshAsync();

        var row = Assert.Single(doctor.Documents);
        Assert.Equal("54845A Programmer", row.Title);
        Assert.Equal("drawn, never text", row.Kind);
        Assert.Contains("579", doctor.Headline);
        Assert.True(doctor.HasAudit);
    }

    [Fact]
    public async Task DrawnDocumentsStartTickedAndScannedOnesDoNot()
    {
        // Ticking three hundred scanned service manuals by default would start hours of work on a
        // different problem from the one somebody opened this tab to fix.
        var service = new FakeDoctor
        {
            Standing = Standing(
                Finding("54845A Programmer", DocumentVerdict.UnderExtracted),
                Finding("TDS3014B Programming Manual", DocumentVerdict.Figures, 418, 7, 7),
                Finding("3585B-SM-V2", DocumentVerdict.ScannedGaps, 301, 136, 0)),
        };

        var doctor = New(service);
        await doctor.RefreshAsync();

        Assert.True(doctor.Documents[0].Selected);
        Assert.True(doctor.Documents[1].Selected);
        Assert.False(doctor.Documents[2].Selected);
        Assert.Equal(2, doctor.SelectedCount);
    }

    [Fact]
    public async Task ADocumentAlreadyRecoveredIsNotTicked()
    {
        var service = new FakeDoctor
        {
            Standing = Standing(
                Finding("done", DocumentVerdict.UnderExtracted, flagged: 75, repaired: 75)),
        };

        var doctor = New(service);
        await doctor.RefreshAsync();

        Assert.False(Assert.Single(doctor.Documents).Selected);
        Assert.Equal(0, doctor.SelectedCount);
        Assert.False(doctor.RepairCommand.CanExecute(null));
    }

    [Fact]
    public async Task OnlyTheTickedDocumentsAreRepaired()
    {
        var service = new FakeDoctor
        {
            Standing = Standing(
                Finding("wanted", DocumentVerdict.UnderExtracted),
                Finding("not wanted", DocumentVerdict.UnderExtracted)),
        };

        var doctor = New(service);
        await doctor.RefreshAsync();
        doctor.Documents[1].Selected = false;

        Assert.Equal(1, doctor.SelectedCount);
        await doctor.RepairAsync();

        Assert.Equal([Path.Combine(Root, "wanted.pdf")], service.Repaired);
    }

    [Fact]
    public async Task ScannedPagesAreOnlyRepairedWhenAskedFor()
    {
        var service = new FakeDoctor
        {
            Standing = Standing(Finding("a", DocumentVerdict.UnderExtracted)),
        };

        var doctor = New(service);
        await doctor.RefreshAsync();

        await doctor.RepairAsync();
        Assert.False(service.LastOptions!.IncludeScannedPages);

        doctor.IncludeScannedPages = true;
        await doctor.RepairAsync();
        Assert.True(service.LastOptions!.IncludeScannedPages);
    }

    [Fact]
    public async Task ProgressArrivesPageByPageAndTheStatusSaysWhatWasDone()
    {
        var service = new FakeDoctor
        {
            Standing = Standing(Finding("54845A Programmer", DocumentVerdict.UnderExtracted)),
        };

        service.OnRepair = (progress, _) =>
        {
            progress?.Report(new RepairProgress(@"C:\Manuals\54845A Programmer.pdf", 19, 1, 75, 117));
            progress?.Report(new RepairProgress(@"C:\Manuals\54845A Programmer.pdf", 20, 2, 75, 80));
            return Task.FromResult(new RepairReport(1, 75, 0, 0, 0, 3_291, 26_474, 0.976, TimeSpan.FromMinutes(1.3)));
        };

        var doctor = New(service);
        await doctor.RefreshAsync();
        await doctor.RepairAsync();

        Assert.Equal(2, doctor.Recovered.Count);
        Assert.Contains("page 20", doctor.Recovered[0], StringComparison.Ordinal);
        Assert.Contains("3,291 words", doctor.Status, StringComparison.Ordinal);
        Assert.Contains("98 %", doctor.Status.Replace("98%", "98 %", StringComparison.Ordinal), StringComparison.Ordinal);

        // And it says plainly that no PDF was touched, because that is the promise being made.
        Assert.Contains("Nothing was written to any PDF", doctor.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoppingKeepsWhatWasAlreadyRecovered()
    {
        var service = new FakeDoctor
        {
            Standing = Standing(Finding("54845A Programmer", DocumentVerdict.UnderExtracted)),
        };

        DoctorViewModel? doctor = null;

        service.OnRepair = (progress, token) =>
        {
            progress?.Report(new RepairProgress(@"C:\Manuals\54845A Programmer.pdf", 19, 1, 75, 117));

            // Cancel from inside the run, which is what pressing the button during one does.
            doctor!.Cancel();
            token.ThrowIfCancellationRequested();

            return Task.FromResult(new RepairReport(0, 0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero));
        };

        doctor = New(service);
        await doctor.RefreshAsync();
        await doctor.RepairAsync();

        Assert.Equal(DoctorState.Idle, doctor.State);
        Assert.Contains("already recovered are kept", doctor.Status, StringComparison.Ordinal);
        Assert.Contains("picks up where this left off", doctor.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRepairSaysSoRatherThanLookingLikeSuccess()
    {
        var service = new FakeDoctor
        {
            Standing = Standing(Finding("a", DocumentVerdict.UnderExtracted)),
        };

        service.OnRepair = (_, _) => throw new InvalidOperationException("no CUDA runtime");

        var doctor = New(service);
        await doctor.RefreshAsync();
        await doctor.RepairAsync();

        Assert.Equal(DoctorState.Idle, doctor.State);
        Assert.Contains("The repair failed: no CUDA runtime", doctor.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheListRefreshesAfterARepairWithoutLosingTheRows()
    {
        var service = new FakeDoctor
        {
            Standing = Standing(Finding("a", DocumentVerdict.UnderExtracted, flagged: 75)),
        };

        var doctor = New(service);
        await doctor.RefreshAsync();
        var row = Assert.Single(doctor.Documents);

        service.Standing = Standing(Finding("a", DocumentVerdict.UnderExtracted, flagged: 75, repaired: 75));
        await doctor.RepairAsync();

        Assert.Same(row, Assert.Single(doctor.Documents));
        Assert.Equal("75/75", row.Progress);
        Assert.True(row.IsRepaired);
        Assert.False(row.Selected);
    }

    [Fact]
    public async Task AuditingReportsProgressAndThenShowsWhatItFound()
    {
        var service = new FakeDoctor
        {
            Standing = Standing(Finding("54845A Programmer", DocumentVerdict.UnderExtracted)),
        };

        var doctor = New(service);
        await doctor.AuditAsync();

        Assert.Equal(2, doctor.DocumentsDone);
        Assert.Equal(2, doctor.DocumentsTotal);
        Assert.Single(doctor.Documents);
        Assert.Equal(DoctorState.Idle, doctor.State);
    }

    [Fact]
    public async Task NothingCanBePressedWhileSomethingIsRunning()
    {
        var service = new FakeDoctor
        {
            Standing = Standing(Finding("a", DocumentVerdict.UnderExtracted)),
        };

        var gate = new TaskCompletionSource();
        service.OnRepair = async (_, _) =>
        {
            await gate.Task;
            return new RepairReport(1, 1, 0, 0, 0, 1, 1, 0.9, TimeSpan.Zero);
        };

        var doctor = New(service);
        await doctor.RefreshAsync();

        var running = doctor.RepairAsync();

        Assert.False(doctor.AuditCommand.CanExecute(null));
        Assert.False(doctor.RepairCommand.CanExecute(null));
        Assert.False(doctor.RefreshCommand.CanExecute(null));
        Assert.True(doctor.CancelCommand.CanExecute(null));

        gate.SetResult();
        await running;

        Assert.True(doctor.AuditCommand.CanExecute(null));
    }

    [Fact]
    public async Task ThePictureForOnePageCanBeAskedForAndExplainsItself()
    {
        var service = new FakeDoctor
        {
            Standing = Standing(Finding("54845A Programmer", DocumentVerdict.UnderExtracted)),
            Picture = [0x89, 0x50, 0x4E, 0x47],
        };

        var doctor = New(service);
        await doctor.RefreshAsync();

        Assert.False(doctor.ExplainCommand.CanExecute(null));

        doctor.SelectedDocument = doctor.Documents[0];
        doctor.ExplainPage = 40;
        Assert.True(doctor.ExplainCommand.CanExecute(null));

        await doctor.ExplainAsync();

        Assert.Equal(service.Picture, doctor.Diagnostic);
        Assert.Contains("nothing accounts for", doctor.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectAllSkipsWhatIsAlreadyDone()
    {
        var service = new FakeDoctor
        {
            Standing = Standing(
                Finding("outstanding", DocumentVerdict.ScannedGaps, flagged: 136, drawn: 0),
                Finding("done", DocumentVerdict.UnderExtracted, flagged: 75, repaired: 75)),
        };

        var doctor = New(service);
        await doctor.RefreshAsync();

        doctor.SelectEverything();

        Assert.True(doctor.Documents[0].Selected);
        Assert.False(doctor.Documents[1].Selected);
        Assert.Equal(1, doctor.SelectedCount);

        doctor.SelectNothing();
        Assert.Equal(0, doctor.SelectedCount);
    }

    [Fact]
    public void NothingCanBePressedWithoutAFolder()
    {
        var doctor = new DoctorViewModel(new FakeDoctor(), InlineDispatcher.Instance);

        Assert.False(doctor.RefreshCommand.CanExecute(null));
        Assert.False(doctor.AuditCommand.CanExecute(null));
        Assert.False(doctor.RepairCommand.CanExecute(null));
    }
}
