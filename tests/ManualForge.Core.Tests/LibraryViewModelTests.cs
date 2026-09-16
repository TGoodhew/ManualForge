using ManualForge.Core.Classification;
using ManualForge.Core.Ocr;
using ManualForge.Core.Pdf;
using ManualForge.Core.Pipeline;
using ManualForge.Core.State;
using ManualForge.Shell;
using ManualForge.Shell.ViewModels;

namespace ManualForge.Core.Tests;

/// <summary>An engine room that answers instantly and does whatever the test needs it to.</summary>
internal sealed class FakeLibraryService : ILibraryService
{
    public List<FileRecord> SurveyResult { get; } = [];

    public List<FileOutcome> RunResult { get; } = [];

    public GpuMemory? Gpu { get; set; }

    public Exception? SurveyThrows { get; set; }

    public Exception? RunThrows { get; set; }

    /// <summary>Runs inside RunAsync before anything is reported, so a test can cancel mid-run.</summary>
    public Func<CancellationToken, Task>? DuringRun { get; set; }

    public ClassificationPolicy? PolicyUsed { get; private set; }

    public bool DryRunUsed { get; private set; }

    public int TrimCalls { get; private set; }

    public Task<IReadOnlyList<FileRecord>> SurveyAsync(
        string root, ClassificationPolicy policy,
        IProgress<DocumentClassification>? progress, CancellationToken cancellationToken)
    {
        PolicyUsed = policy;
        if (SurveyThrows is not null)
            return Task.FromException<IReadOnlyList<FileRecord>>(SurveyThrows);

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<FileRecord>>(SurveyResult.ToArray());
    }

    public async Task<IReadOnlyList<FileOutcome>> RunAsync(
        string root, ClassificationPolicy policy, bool dryRun,
        IProgress<FileOutcome>? files, IProgress<PipelinePageProgress>? pages,
        CancellationToken cancellationToken)
    {
        PolicyUsed = policy;
        DryRunUsed = dryRun;

        if (RunThrows is not null)
            throw RunThrows;

        if (DuringRun is not null)
            await DuringRun(cancellationToken).ConfigureAwait(false);

        foreach (var outcome in RunResult)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages?.Report(new PipelinePageProgress(
                outcome.Path, 1, outcome.PageCount, 10, FromCache: false, TimeSpan.Zero, TimeSpan.Zero));
            files?.Report(outcome);
        }

        return RunResult.ToArray();
    }

    public Task<IReadOnlyList<string>> TrimMissingAsync(string root, CancellationToken cancellationToken)
    {
        TrimCalls++;
        return Task.FromResult<IReadOnlyList<string>>(["gone.pdf"]);
    }

    public GpuMemory? ReadGpu() => Gpu;
}

/// <summary>
/// The shell's behaviour, driven without a window.
///
/// This is what makes "MVVM, no logic in code-behind" worth claiming. Everything the specification
/// asks the UI to do - survey before committing, live counts, throughput, GPU, a running error
/// list, cancel and resume, export the report - is asserted here rather than clicked through.
/// </summary>
public class LibraryViewModelTests
{
    private static FileRecord Record(
        string path, TextClass textClass, int pages,
        FileStatus status = FileStatus.Classified, ClassAction action = ClassAction.Ocr) =>
        new(path, status, new FileFingerprint(1, 2, "h"), pages, textClass,
            0, 0, 0, ModificationBlocker.None, action, null, null, null, 0);

    private static FileOutcome Outcome(
        string path, FileStatus status, int pages = 10, int words = 100,
        string? error = null, bool signed = false) =>
        new(path, TextClass.ImageOnly, ClassAction.Ocr, status, pages, words, 0.001, false,
            TimeSpan.FromSeconds(1), error, signed);

    private static (LibraryViewModel Model, FakeLibraryService Service) New()
    {
        var service = new FakeLibraryService();
        return (new LibraryViewModel(service) { Folder = @"C:\Manuals" }, service);
    }

    // ---------------------------------------------------------------- survey before committing

    [Fact]
    public async Task NothingCanBeRunUntilAFolderIsChosen()
    {
        var model = new LibraryViewModel(new FakeLibraryService());

        Assert.False(model.SurveyCommand.CanExecute(null));
        Assert.False(model.RunCommand.CanExecute(null));

        model.Folder = @"C:\Manuals";

        Assert.True(model.SurveyCommand.CanExecute(null));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheSummaryGroupsTheLibraryByClassAndTotalsItsPages()
    {
        var (model, service) = New();
        service.SurveyResult.AddRange(
        [
            Record(@"C:\Manuals\a.pdf", TextClass.ImageOnly, 10),
            Record(@"C:\Manuals\b.pdf", TextClass.ImageOnly, 20),
            Record(@"C:\Manuals\c.pdf", TextClass.GoodText, 100, action: ClassAction.Skip),
        ]);

        await model.SurveyCommand.ExecuteAsync(null);

        Assert.True(model.HasSurvey);
        Assert.Equal(2, model.Summary.Count);

        var imageOnly = model.Summary.Single(r => r.TextClass == TextClass.ImageOnly);
        Assert.Equal(2, imageOnly.Files);
        Assert.Equal(30, imageOnly.Pages);
        Assert.Equal(ClassAction.Ocr, imageOnly.Action);

        Assert.Equal(130, model.PagesTotal);
        Assert.Equal(3, model.FilesTotal);
    }

    [Fact]
    public async Task ChangingAnActionInTheTableChangesThePolicyTheRunUses()
    {
        // The specification asks to choose per class whether to OCR, skip or strip-and-redo, which
        // only means anything if the table is what the run then obeys.
        var (model, service) = New();
        service.SurveyResult.Add(Record(@"C:\Manuals\a.pdf", TextClass.SuspectText, 10));

        await model.SurveyCommand.ExecuteAsync(null);
        model.Summary.Single().Action = ClassAction.StripAndRedo;

        await model.RunCommand.ExecuteAsync(null);

        Assert.Equal(ClassAction.StripAndRedo, service.PolicyUsed!.ActionFor(TextClass.SuspectText));
    }

    [Fact]
    public async Task FilesThatHaveGoneAreReportedAndCanBeForgotten()
    {
        var (model, service) = New();
        service.SurveyResult.AddRange(
        [
            Record(@"C:\Manuals\here.pdf", TextClass.ImageOnly, 10),
            Record(@"C:\Manuals\gone.pdf", TextClass.ImageOnly, 5, FileStatus.Missing),
        ]);

        await model.SurveyCommand.ExecuteAsync(null);

        // Left out of the totals, but never silently.
        Assert.Equal(1, model.MissingCount);
        Assert.Equal(1, model.FilesTotal);
        Assert.Equal(10, model.PagesTotal);
        Assert.Contains(model.Errors, e => e.Contains("no longer on disk", StringComparison.Ordinal));

        Assert.True(model.TrimMissingCommand.CanExecute(null));
        await model.TrimMissingCommand.ExecuteAsync(null);

        Assert.Equal(1, service.TrimCalls);
        Assert.Equal(0, model.MissingCount);
        Assert.False(model.TrimMissingCommand.CanExecute(null));
    }

    // ---------------------------------------------------------------- live progress

    [Fact]
    public async Task ProgressCountsFilesAndPagesAsTheyFinish()
    {
        var (model, service) = New();
        service.SurveyResult.AddRange(
        [
            Record(@"C:\Manuals\a.pdf", TextClass.ImageOnly, 10),
            Record(@"C:\Manuals\b.pdf", TextClass.ImageOnly, 20),
        ]);
        service.RunResult.AddRange(
        [
            Outcome(@"C:\Manuals\a.pdf", FileStatus.Completed, pages: 10),
            Outcome(@"C:\Manuals\b.pdf", FileStatus.Completed, pages: 20),
        ]);

        await model.SurveyCommand.ExecuteAsync(null);
        await model.RunCommand.ExecuteAsync(null);

        Assert.Equal(2, model.FilesDone);
        Assert.Equal(30, model.PagesDone);
        Assert.Equal(100, model.FileProgress);
        Assert.Equal(2, model.Results.Count);
        Assert.Equal("b.pdf", model.CurrentFile);
    }

    [Fact]
    public async Task OnlyCompletedPagesCountTowardsThroughput()
    {
        // A failure is not progress. Counting its pages would flatter the rate and, worse, would
        // make the estimate of what remains wrong in the optimistic direction.
        var (model, service) = New();
        service.RunResult.AddRange(
        [
            Outcome(@"C:\Manuals\a.pdf", FileStatus.Completed, pages: 10),
            Outcome(@"C:\Manuals\b.pdf", FileStatus.Failed, pages: 500, error: "no text layer"),
        ]);

        await model.RunCommand.ExecuteAsync(null);

        Assert.Equal(10, model.PagesDone);
        Assert.Equal(2, model.FilesDone);
    }

    [Fact]
    public async Task AFailedFileLandsInTheErrorList()
    {
        var (model, service) = New();
        service.RunResult.Add(Outcome(@"C:\Manuals\bad.pdf", FileStatus.Failed, error: "already carry a text layer"));

        await model.RunCommand.ExecuteAsync(null);

        var error = Assert.Single(model.Errors);
        Assert.Contains("bad.pdf", error, StringComparison.Ordinal);
        Assert.Contains("already carry a text layer", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidatedSignatureIsReportedEvenThoughTheFileSucceeded()
    {
        var (model, service) = New();
        service.RunResult.Add(Outcome(@"C:\Manuals\signed.pdf", FileStatus.Completed, signed: true));

        await model.RunCommand.ExecuteAsync(null);

        var error = Assert.Single(model.Errors);
        Assert.Contains("signature is now invalid", error, StringComparison.Ordinal);
        Assert.Contains("original is kept", error, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- cancel and resume

    [Fact]
    public async Task CancellingSaysThatTheWorkSoFarIsKept()
    {
        var (model, service) = New();
        service.DuringRun = async token =>
        {
            // The view model cancels itself once the run is under way, as pressing the button does.
            model.CancelCommand.Execute(null);
            await Task.Delay(10, CancellationToken.None);
            token.ThrowIfCancellationRequested();
        };

        await model.RunCommand.ExecuteAsync(null);

        Assert.Equal(ShellState.Idle, model.State);
        Assert.Contains("Cancelled", model.Status, StringComparison.Ordinal);

        // Cancelling is not failing, and a user who does not know the pages are kept will not dare.
        Assert.Contains("resumes", model.Status, StringComparison.Ordinal);
        Assert.Empty(model.Errors);
    }

    [Fact]
    public async Task CancelIsOnlyOfferedWhileSomethingIsRunning()
    {
        var (model, service) = New();
        Assert.False(model.CancelCommand.CanExecute(null));

        var sawCancelOffered = false;
        service.DuringRun = token =>
        {
            sawCancelOffered = model.CancelCommand.CanExecute(null);
            return Task.CompletedTask;
        };

        await model.RunCommand.ExecuteAsync(null);

        Assert.True(sawCancelOffered);
        Assert.False(model.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task NothingElseCanStartWhileARunIsGoing()
    {
        var (model, service) = New();
        var couldStartAnother = true;
        service.DuringRun = token =>
        {
            couldStartAnother = model.RunCommand.CanExecute(null) || model.SurveyCommand.CanExecute(null);
            return Task.CompletedTask;
        };

        await model.RunCommand.ExecuteAsync(null);

        Assert.False(couldStartAnother);
    }

    [Fact]
    public async Task AFailedRunLeavesTheShellUsableAndSaysWhatHappened()
    {
        var (model, service) = New();
        service.RunThrows = new InvalidOperationException("the card fell over");

        await model.RunCommand.ExecuteAsync(null);

        Assert.Equal(ShellState.Idle, model.State);
        Assert.Contains("the card fell over", model.Status, StringComparison.Ordinal);
        Assert.Contains(model.Errors, e => e.Contains("the card fell over", StringComparison.Ordinal));
        Assert.True(model.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task ADryRunIsPassedThrough()
    {
        var (model, service) = New();
        model.DryRun = true;

        await model.RunCommand.ExecuteAsync(null);

        Assert.True(service.DryRunUsed);
    }

    // ---------------------------------------------------------------- GPU

    [Fact]
    public void TheGpuPanelShowsMemoryAndBusyness()
    {
        var (model, service) = New();
        service.Gpu = new GpuMemory(8192, 6144, "RTX 3060 Ti", UtilisationPercent: 73);

        model.RefreshGpu();

        Assert.Equal(73, model.GpuUtilisation);
        Assert.Equal(75, model.VramUsedPercent);
        Assert.Contains("2,048 MiB free", model.GpuStatus!, StringComparison.Ordinal);
        Assert.Contains("73% busy", model.GpuStatus!, StringComparison.Ordinal);
    }

    [Fact]
    public void AMachineWithNoNvidiaCardSaysSoRatherThanShowingZeroes()
    {
        var (model, service) = New();
        service.Gpu = null;

        model.RefreshGpu();

        Assert.Contains("No NVIDIA GPU", model.GpuStatus!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the report

    [Fact]
    public async Task TheReportExportsAsCsvOrJsonByExtension()
    {
        var (model, service) = New();
        service.RunResult.AddRange(
        [
            Outcome(@"C:\Manuals\a.pdf", FileStatus.Completed, words: 25853),
            Outcome(@"C:\Manuals\b.pdf", FileStatus.Failed, error: "He said \"no\""),
        ]);

        await model.RunCommand.ExecuteAsync(null);

        var csv = model.ToCsv();
        Assert.StartsWith("path,class,action,status,pages,words", csv, StringComparison.Ordinal);
        Assert.Contains("25853", csv, StringComparison.Ordinal);

        // A quotation mark in an error message must not break the row it sits in.
        Assert.Contains("\"\"no\"\"", csv, StringComparison.Ordinal);
        Assert.Equal(3, csv.Split(Environment.NewLine).Length);

        var json = model.ToJson();
        Assert.Contains("\"WordsWritten\": 25853", json, StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var jsonPath = Path.Combine(directory, "report.json");
            await model.SaveReportAsync(jsonPath);
            Assert.StartsWith("[", (await File.ReadAllTextAsync(jsonPath)).TrimStart(), StringComparison.Ordinal);

            var csvPath = Path.Combine(directory, "report.csv");
            await model.SaveReportAsync(csvPath);
            Assert.StartsWith("path,", await File.ReadAllTextAsync(csvPath), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}
