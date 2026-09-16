using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualForge.Core.Classification;
using ManualForge.Core.Pipeline;
using ManualForge.Core.State;

namespace ManualForge.Shell.ViewModels;

/// <summary>What the shell is doing, which decides what can be pressed.</summary>
public enum ShellState
{
    Idle,
    Surveying,
    Running,
    Cancelling,
}

/// <summary>
/// The shell's behaviour, with no window attached to it.
///
/// Everything the specification asks the UI to do lives here: pick a folder, survey it, show the
/// class summary before committing, run with live per-file and per-page state, throughput, GPU and
/// VRAM, a running error list, cancel, resume, and export the report. The XAML binds to it and
/// contains no logic, which is only a meaningful claim because this type can be driven by a test.
/// </summary>
public sealed partial class LibraryViewModel(
    ILibraryService service,
    TimeProvider? time = null,
    IUiDispatcher? dispatcher = null) : ObservableObject
{
    private readonly ILibraryService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly IUiDispatcher _dispatcher = dispatcher ?? InlineDispatcher.Instance;

    private CancellationTokenSource? _cancellation;
    private long _startedTicks;
    private int _errorsBeforeRun;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SurveyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private string? _folder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SurveyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(TrimMissingCommand))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private ShellState _state = ShellState.Idle;

    [ObservableProperty]
    private string _status = "Pick a folder of PDFs.";

    [ObservableProperty]
    private bool _dryRun;

    /// <summary>The file being worked on, and where the run has got to.</summary>
    [ObservableProperty]
    private string? _currentFile;

    [ObservableProperty]
    private string? _currentPage;

    [ObservableProperty]
    private int _filesDone;

    [ObservableProperty]
    private int _filesTotal;

    [ObservableProperty]
    private long _pagesDone;

    [ObservableProperty]
    private long _pagesTotal;

    [ObservableProperty]
    private double _pagesPerMinute;

    [ObservableProperty]
    private string? _gpuStatus;

    [ObservableProperty]
    private int _gpuUtilisation;

    [ObservableProperty]
    private int _vramUsedPercent;

    /// <summary>Records whose files have gone. Reported on every survey, whatever else was asked for.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrimMissingCommand))]
    private int _missingCount;

    public ObservableCollection<ClassSummaryRow> Summary { get; } = [];

    public ObservableCollection<FileOutcome> Results { get; } = [];

    public ObservableCollection<string> Errors { get; } = [];

    public bool IsBusy => State is not ShellState.Idle;

    /// <summary>
    /// The inverse, as a property rather than a converter in the XAML. A binding that needs a
    /// converter to say "not busy" is a small piece of logic living in the view, and one that fails
    /// at runtime rather than at build time if the converter is not there.
    /// </summary>
    public bool IsIdle => State is ShellState.Idle;

    public double FileProgress => FilesTotal <= 0 ? 0 : 100.0 * FilesDone / FilesTotal;

    /// <summary>
    /// Whether there is anything the run would do. Surveying first is not optional: the summary is
    /// what the specification asks be shown before committing to anything.
    /// </summary>
    public bool HasSurvey => Summary.Count > 0;

    private bool CanStart => State is ShellState.Idle && !string.IsNullOrWhiteSpace(Folder);

    private bool CanCancel => State is ShellState.Surveying or ShellState.Running;

    private bool CanTrim => State is ShellState.Idle && MissingCount > 0 && !string.IsNullOrWhiteSpace(Folder);

    /// <summary>The policy the summary table currently describes.</summary>
    public ClassificationPolicy CurrentPolicy() =>
        new(Summary.ToDictionary(r => r.TextClass, r => r.Action));

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task SurveyAsync()
    {
        var folder = Folder!;
        State = ShellState.Surveying;
        Errors.Clear();
        Status = "Classifying...";

        _cancellation = new CancellationTokenSource();
        var seen = 0;
        var progress = new DispatchedProgress<DocumentClassification>(_dispatcher, c =>
        {
            seen++;
            CurrentFile = Path.GetFileName(c.Path);
            Status = $"Classified {seen:N0} file(s)...";
        });

        try
        {
            var records = await _service
                .SurveyAsync(folder, CurrentPolicy(), progress, _cancellation.Token)
                .ConfigureAwait(true);

            ApplySurvey(records);
        }
        catch (OperationCanceledException)
        {
            Status = "Survey cancelled.";
        }
        catch (Exception ex)
        {
            Fail("Survey failed", ex);
        }
        finally
        {
            Finish();
        }
    }

    private void ApplySurvey(IReadOnlyList<FileRecord> records)
    {
        var policy = CurrentPolicy();

        Summary.Clear();
        foreach (var row in ClassSummaryRow.From(records, policy))
            Summary.Add(row);

        OnPropertyChanged(nameof(HasSurvey));

        var present = records.Where(r => r.Status != FileStatus.Missing).ToArray();
        MissingCount = records.Count - present.Length;

        FilesTotal = present.Length;
        FilesDone = 0;
        PagesTotal = present.Sum(r => (long)r.PageCount);
        PagesDone = 0;

        var outstanding = present.Count(r =>
            r.Action != ClassAction.Skip
            && r.Status is not (FileStatus.Completed or FileStatus.Skipped));

        Status = outstanding == 0
            ? $"{present.Length:N0} files, {PagesTotal:N0} pages. Nothing outstanding."
            : $"{present.Length:N0} files, {PagesTotal:N0} pages. {outstanding:N0} would be processed.";

        if (MissingCount > 0)
            Errors.Add($"{MissingCount:N0} recorded file(s) are no longer on disk. Trim to forget them.");
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task RunAsync()
    {
        var folder = Folder!;
        State = ShellState.Running;
        Status = DryRun ? "Dry run: nothing will be replaced." : "Processing...";
        Results.Clear();

        _cancellation = new CancellationTokenSource();
        _startedTicks = _time.GetTimestamp();
        FilesDone = 0;
        PagesDone = 0;

        // Anything already in the list came from the survey - files that have gone, most often -
        // and belongs to the folder rather than to this run, so it survives.
        _errorsBeforeRun = Errors.Count;

        var files = new DispatchedProgress<FileOutcome>(_dispatcher, OnFileFinished);
        var pages = new DispatchedProgress<PipelinePageProgress>(_dispatcher, OnPageRecognised);

        try
        {
            var outcomes = await _service
                .RunAsync(folder, CurrentPolicy(), DryRun, files, pages, _cancellation.Token)
                .ConfigureAwait(true);

            // Progress is advisory; the returned list is authoritative.
            //
            // IProgress<T> posts its callbacks rather than running them, so when a run finishes
            // there is no guarantee every report has been applied - the last few can still be in
            // flight. Reading the counters at that moment gives a number that is usually right,
            // which is the worst kind. Rebuilding from what the run returned makes the final state
            // exact, and leaves progress doing the only job it is good for: liveness.
            Reconcile(outcomes);

            Status = $"Finished. {Results.Count(r => r.Status == FileStatus.Completed):N0} completed, " +
                     $"{RunErrorCount:N0} problem(s).";
        }
        catch (OperationCanceledException)
        {
            // Cancelling is not failing. The pages already recognised are cached, so the next run
            // resumes rather than starting over, and saying so is the difference between a user who
            // cancels confidently and one who never dares.
            Status = $"Cancelled. {Results.Count:N0} file(s) finished; recognised pages are kept, so " +
                     "running again resumes from here.";
        }
        catch (Exception ex)
        {
            Fail("Run failed", ex);
        }
        finally
        {
            Finish();
        }
    }

    private void OnFileFinished(FileOutcome outcome)
    {
        Results.Add(outcome);
        FilesDone++;
        CurrentFile = Path.GetFileName(outcome.Path);
        OnPropertyChanged(nameof(FileProgress));

        foreach (var problem in ProblemsWith(outcome))
            Errors.Add(problem);

        if (outcome.Status == FileStatus.Completed)
        {
            PagesDone += outcome.PageCount;
            UpdateRate();
        }
    }

    /// <summary>
    /// Replaces whatever progress managed to deliver with what the run actually returned.
    /// </summary>
    private void Reconcile(IReadOnlyList<FileOutcome> outcomes)
    {
        Results.Clear();
        foreach (var outcome in outcomes)
            Results.Add(outcome);

        while (Errors.Count > _errorsBeforeRun)
            Errors.RemoveAt(Errors.Count - 1);

        foreach (var problem in outcomes.SelectMany(ProblemsWith))
            Errors.Add(problem);

        FilesDone = outcomes.Count;
        PagesDone = outcomes.Where(o => o.Status == FileStatus.Completed).Sum(o => (long)o.PageCount);
        UpdateRate();
        OnPropertyChanged(nameof(FileProgress));
    }

    /// <summary>
    /// What is worth telling the user about one file. A succeeded file can still have something to
    /// say: an invalidated signature is not a failure, but it is not nothing either.
    /// </summary>
    private static IEnumerable<string> ProblemsWith(FileOutcome outcome)
    {
        var name = Path.GetFileName(outcome.Path);

        if (outcome.Status == FileStatus.Failed)
            yield return $"{name}: {outcome.Error}";

        if (outcome.SignatureInvalidated)
            yield return $"{name}: signed; that signature is now invalid. The original is kept.";
    }

    /// <summary>Problems this run produced, not counting anything the survey left in the list.</summary>
    public int RunErrorCount => Math.Max(0, Errors.Count - _errorsBeforeRun);

    private void OnPageRecognised(PipelinePageProgress page)
    {
        CurrentFile = Path.GetFileName(page.DocumentPath);
        CurrentPage = $"page {page.PageNumber:N0} of {page.PageCount:N0}";
    }

    private void UpdateRate()
    {
        var elapsed = _time.GetElapsedTime(_startedTicks);
        PagesPerMinute = elapsed.TotalMinutes <= 0 ? 0 : PagesDone / elapsed.TotalMinutes;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        State = ShellState.Cancelling;
        Status = "Cancelling — finishing the page in flight.";
        _cancellation?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanTrim))]
    private async Task TrimMissingAsync()
    {
        try
        {
            var trimmed = await _service.TrimMissingAsync(Folder!, CancellationToken.None).ConfigureAwait(true);
            MissingCount = 0;
            Status = $"Forgot {trimmed.Count:N0} record(s) for files that are no longer on disk.";
        }
        catch (Exception ex)
        {
            Fail("Could not trim", ex);
        }
    }

    /// <summary>Samples the card. The view calls this on a timer; it never throws.</summary>
    public void RefreshGpu()
    {
        try
        {
            var gpu = _service.ReadGpu();
            if (gpu is null)
            {
                GpuStatus = "No NVIDIA GPU reported.";
                GpuUtilisation = 0;
                VramUsedPercent = 0;
                return;
            }

            GpuStatus = gpu.ToString();
            GpuUtilisation = gpu.UtilisationPercent ?? 0;
            VramUsedPercent = gpu.UsedPercent;
        }
        catch (Exception)
        {
            GpuStatus = "GPU status unavailable.";
        }
    }

    private void Fail(string what, Exception ex)
    {
        Status = $"{what}: {ex.Message}";
        Errors.Add($"{what}: {ex.Message}");
    }

    private void Finish()
    {
        _cancellation?.Dispose();
        _cancellation = null;
        CurrentPage = null;
        State = ShellState.Idle;
    }

    // ------------------------------------------------------------------ the report

    public string ToCsv()
    {
        var lines = new List<string>(Results.Count + 1)
        {
            "path,class,action,status,pages,words,worstDeviationPt,flattened,signatureInvalidated,seconds,error",
        };

        foreach (var r in Results)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"\"{r.Path.Replace("\"", "\"\"")}\",{r.TextClass},{r.Action},{r.Status},{r.PageCount}," +
                $"{r.WordsWritten},{r.WorstDeviationPt:F3},{r.WasFlattened},{r.SignatureInvalidated}," +
                $"{r.Duration.TotalSeconds:F1},\"{(r.Error ?? string.Empty).Replace("\"", "\"\"").ReplaceLineEndings(" ")}\""));
        }

        return string.Join(Environment.NewLine, lines);
    }

    public string ToJson() => JsonSerializer.Serialize(
        Results.Select(r => new
        {
            r.Path,
            TextClass = r.TextClass.ToString(),
            Action = r.Action.ToString(),
            Status = r.Status.ToString(),
            r.PageCount,
            r.WordsWritten,
            r.WorstDeviationPt,
            r.WasFlattened,
            r.SignatureInvalidated,
            Seconds = Math.Round(r.Duration.TotalSeconds, 1),
            r.Error,
        }),
        new JsonSerializerOptions { WriteIndented = true });

    public async Task SaveReportAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var text = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ToJson() : ToCsv();
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false)).ConfigureAwait(false);
    }
}
