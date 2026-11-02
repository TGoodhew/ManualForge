using ManualForge.Core.Auditing;
using ManualForge.Core.Classification;
using ManualForge.Core.Pipeline;
using ManualForge.Core.State;
using ManualForge.Shell;
using ManualForge.Shell.ViewModels;

namespace ManualForge.Core.Tests;

/// <summary>
/// The Added tab (#89), driven without a window: files are copied in at once, and the run, the audit,
/// the repair and the index are done to them only once nothing else is working, through the same view
/// models the buttons use.
/// </summary>
public sealed class AddQueueViewModelTests
{
    /// <summary>A real folder, because the queue will not add to one that is not there; the fakes write nothing to it.</summary>
    private static readonly string Root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "ManualForge.Tests", "add-queue")).FullName;

    private sealed class FakeDoctor : IDoctorService
    {
        public List<string> Audited { get; } = [];

        public List<string> Repaired { get; } = [];

        public RepairOptions? RepairOptionsUsed { get; private set; }

        /// <summary>Files the audit flags, with the pages it finds still to recover in each.</summary>
        public Dictionary<string, int> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool CancelRepair { get; set; }

        public bool HasAudit(string root) => true;

        public Task<DoctorStanding> ReadAsync(string root, CancellationToken cancellationToken) =>
            Task.FromResult(Standing());

        public Task<DoctorStanding> AuditAsync(
            string root, DoctorOptions options, IReadOnlyList<string>? paths,
            IProgress<DoctorProgress>? progress, CancellationToken cancellationToken)
        {
            Audited.AddRange(paths ?? []);
            return Task.FromResult(Standing());
        }

        public Task<RepairReport> RepairAsync(
            string root, IReadOnlyList<string> paths, RepairOptions options,
            IProgress<RepairProgress>? progress, CancellationToken cancellationToken)
        {
            if (CancelRepair)
                throw new OperationCanceledException();

            Repaired.AddRange(paths);
            RepairOptionsUsed = options;
            return Task.FromResult(new RepairReport(paths.Count, 3, 0, 0, 0, 30, 200, 0.9, TimeSpan.FromSeconds(5)));
        }

        public Task<byte[]?> ExplainAsync(string path, int pageNumber, CancellationToken cancellationToken) =>
            Task.FromResult<byte[]?>(null);

        private DoctorStanding Standing()
        {
            var findings = Flags
                .Select(f => new DoctorFinding(
                    f.Key, Path.GetFileNameWithoutExtension(f.Key), 20, f.Value, 0, 0, 100, 300,
                    DocumentVerdict.ScannedGaps, "1-" + f.Value))
                .ToArray();
            return new DoctorStanding(new AuditSummary(10, findings.Length, findings.Sum(f => f.FlaggedPages), 0, 0), findings);
        }
    }

    private sealed record Rig(
        AddQueueViewModel Adds,
        FakeLibraryService Service,
        LibraryViewModel Library,
        FakeDoctor DoctorService,
        DoctorViewModel Doctor,
        FakeSearchService SearchService);

    private static Rig New(bool withLibrary = true)
    {
        var root = withLibrary ? Root : null;
        var service = new FakeLibraryService();
        var library = new LibraryViewModel(service) { Folder = root };
        var doctorService = new FakeDoctor();
        var doctor = new DoctorViewModel(doctorService) { Folder = root };
        var searchService = new FakeSearchService();
        var search = new SearchViewModel(searchService) { Folder = root };
        return new Rig(
            new AddQueueViewModel(service, library, doctor, search, () => root),
            service, library, doctorService, doctor, searchService);
    }

    private static FileOutcome Completed(string path, string? renamedTo = null) =>
        new(path, TextClass.ImageOnly, ClassAction.Ocr, FileStatus.Completed, 20, 400, 0.01, false,
            TimeSpan.FromSeconds(3), null) { RenamedTo = renamedTo };

    [Fact]
    public async Task AnAddedFileIsMadeSearchableCheckedRepairedWithScansAndIndexed()
    {
        var rig = New();
        var added = Path.Combine(Root, "Scope.pdf");
        rig.Service.RunResult.Add(Completed(added));
        rig.DoctorService.Flags[added] = 4;

        await rig.Adds.AddAsync([@"C:\Downloads\Scope.pdf"]);

        // The run is on just the added file, and for real even with Dry run ticked on the main page.
        Assert.Equal([added], rig.Service.DocumentsUsed);
        Assert.False(rig.Service.DryRunUsed);
        Assert.Equal([added], rig.DoctorService.Audited);
        Assert.Equal([added], rig.DoctorService.Repaired);
        Assert.True(rig.DoctorService.RepairOptionsUsed!.IncludeScannedPages);
        Assert.Equal(1, rig.SearchService.BuildCalls);

        var item = Assert.Single(rig.Adds.Items);
        Assert.Equal(AddStage.Done, item.Stage);
        Assert.Equal("Made searchable, checked and indexed.", item.Detail);
        Assert.Contains("Added 1 file(s)", rig.Adds.Status);
        Assert.False(rig.Adds.IsWorking);
    }

    [Fact]
    public async Task NothingIsRepairedWhenTheAuditFindsNothing()
    {
        var rig = New();

        await rig.Adds.AddAsync([@"C:\Downloads\Born digital.pdf"]);

        Assert.Single(rig.DoctorService.Audited);
        Assert.Empty(rig.DoctorService.Repaired);
        Assert.Equal(1, rig.SearchService.BuildCalls);
        Assert.Equal("Its text was already there; checked and indexed.", Assert.Single(rig.Adds.Items).Detail);
    }

    [Fact]
    public async Task AFileTheLibraryAlreadyHoldsIsNotWorkedOn()
    {
        var rig = New();
        rig.Service.OnAdd = (_, sources) => [new AddResult(sources[0], AddKind.AlreadyHeld, Path.Combine(Root, "HP", "Counter.pdf"))];

        await rig.Adds.AddAsync([@"C:\Downloads\Counter.pdf"]);

        var item = Assert.Single(rig.Adds.Items);
        Assert.Equal(AddStage.AlreadyHeld, item.Stage);
        Assert.Contains(Path.Combine(Root, "HP", "Counter.pdf"), item.Detail);
        Assert.Null(rig.Service.DocumentsUsed);
        Assert.Equal(0, rig.SearchService.BuildCalls);
        Assert.Equal("Nothing new to add. 1 already in the library.", rig.Adds.Status);
    }

    [Fact]
    public async Task ARenamedFileSaysWhyAndIsFollowedUnderItsNewName()
    {
        var rig = New();
        var copied = Path.Combine(Root, "Manual (2).pdf");
        var silenced = Path.Combine(Root, "Manual (2)_repaired.pdf");
        rig.Service.OnAdd = (_, sources) => [new AddResult(sources[0], AddKind.Added, copied)];
        rig.Service.RunResult.Add(Completed(copied, renamedTo: silenced));

        await rig.Adds.AddAsync([@"C:\Downloads\Manual.pdf"]);

        Assert.Equal([silenced], rig.DoctorService.Audited);
        Assert.Equal(silenced, Assert.Single(rig.Adds.Items).LibraryPath);
    }

    [Fact]
    public async Task AFileTheRunFailsOnIsNotCheckedOrIndexed()
    {
        var rig = New();
        var added = Path.Combine(Root, "Damaged.pdf");
        rig.Service.RunResult.Add(Completed(added) with { Status = FileStatus.Failed, Error = "cannot be opened" });

        await rig.Adds.AddAsync([@"C:\Downloads\Damaged.pdf"]);

        var item = Assert.Single(rig.Adds.Items);
        Assert.Equal(AddStage.Failed, item.Stage);
        Assert.Contains("cannot be opened", item.Detail);
        Assert.Empty(rig.DoctorService.Audited);
        Assert.Equal(0, rig.SearchService.BuildCalls);
    }

    [Fact]
    public async Task FilesArrivingDuringARunAreCopiedAtOnceAndWorkedOnAfterIt()
    {
        var rig = New();
        var release = new TaskCompletionSource();
        var first = true;
        rig.Service.DuringRun = async _ =>
        {
            if (first)
            {
                first = false;
                await release.Task;
            }
        };

        var running = rig.Library.RunCommand.ExecuteAsync(null);
        await rig.Adds.AddAsync([@"C:\Downloads\Later.pdf"]);

        var item = Assert.Single(rig.Adds.Items);
        Assert.Equal(AddStage.Waiting, item.Stage);
        Assert.Contains("waiting for the run in progress", rig.Adds.Status);
        Assert.Null(rig.Service.DocumentsUsed);

        release.SetResult();
        await running;
        await WhenAsync(() => item.Stage == AddStage.Done);

        Assert.Equal([Path.Combine(Root, "Later.pdf")], rig.Service.DocumentsUsed);
    }

    [Fact]
    public async Task AStoppedStepLeavesTheFilesToRetry()
    {
        var rig = New();
        var added = Path.Combine(Root, "Scope.pdf");
        rig.DoctorService.Flags[added] = 4;
        rig.DoctorService.CancelRepair = true;

        await rig.Adds.AddAsync([@"C:\Downloads\Scope.pdf"]);

        var item = Assert.Single(rig.Adds.Items);
        Assert.Equal(AddStage.Stopped, item.Stage);
        Assert.Contains("Retry", item.Detail);
        Assert.Equal(0, rig.SearchService.BuildCalls);
        Assert.True(rig.Adds.RetryCommand.CanExecute(null));

        rig.DoctorService.CancelRepair = false;
        rig.Adds.RetryCommand.Execute(null);
        await WhenAsync(() => item.Stage == AddStage.Done);

        Assert.Equal([added], rig.DoctorService.Repaired);
        Assert.Equal(1, rig.SearchService.BuildCalls);
    }

    [Fact]
    public async Task WithNoLibraryNothingIsAdded()
    {
        var rig = New(withLibrary: false);

        await rig.Adds.AddAsync([@"C:\Downloads\Scope.pdf"]);

        Assert.Equal(AddStage.Failed, Assert.Single(rig.Adds.Items).Stage);
        Assert.Contains("no library", rig.Adds.Status);
    }

    [Fact]
    public async Task ClearFinishedKeepsOnlyWhatIsStillToDo()
    {
        var rig = New();
        rig.Service.OnAdd = (_, sources) =>
        [
            new AddResult(sources[0], AddKind.AlreadyHeld, Path.Combine(Root, "A.pdf")),
            new AddResult(sources[1], AddKind.Refused, null, "not a PDF"),
        ];

        await rig.Adds.AddAsync([@"C:\Downloads\A.pdf", @"C:\Downloads\notes.txt"]);
        rig.Adds.ClearFinishedCommand.Execute(null);

        Assert.Empty(rig.Adds.Items);
    }

    private static async Task WhenAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
