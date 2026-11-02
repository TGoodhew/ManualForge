using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualForge.Core.Pipeline;
using ManualForge.Core.State;

namespace ManualForge.Shell.ViewModels;

/// <summary>Where one added file has got to.</summary>
public enum AddStage
{
    Copying,
    Waiting,
    Processing,
    Done,
    AlreadyHeld,
    Refused,
    Failed,
    Stopped,
}

/// <summary>One file given to the app to add, as the Added tab lists it.</summary>
public sealed partial class AddItem(string source, string root) : ObservableObject
{
    public string Source { get; } = source;

    /// <summary>The library it is being added to.</summary>
    public string Root { get; } = root;

    public string Name => Path.GetFileName(Source);

    /// <summary>Where it is in the library, once it is there: renamed if its name was taken, or by the run.</summary>
    [ObservableProperty]
    private string? _libraryPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StageText))]
    private AddStage _stage = AddStage.Copying;

    [ObservableProperty]
    private string _detail = "Copying into the library...";

    public string StageText => Stage switch
    {
        AddStage.Copying => "Copying",
        AddStage.Waiting => "Waiting",
        AddStage.Processing => "Working",
        AddStage.Done => "Done",
        AddStage.AlreadyHeld => "Already there",
        AddStage.Refused => "Not added",
        AddStage.Failed => "Failed",
        _ => "Stopped",
    };

    /// <summary>Whether nothing more will happen to it.</summary>
    public bool IsFinished => Stage is AddStage.Done or AddStage.AlreadyHeld or AddStage.Refused or AddStage.Failed;
}

/// <summary>
/// Files added to the library from Explorer or the command line (#89), and the work on them.
///
/// <para>
/// Each file is copied into the library as soon as it arrives. The rest - the run, the audit, the
/// repair with scanned pages included, and the index - is done to every file waiting, together, once
/// nothing else in the app is working, and through the same view models the buttons use. So two jobs
/// never compete for the card, what is happening shows where it always shows, and a run already going
/// when a file arrives finishes first.
/// </para>
///
/// <para>
/// A step that is cancelled or fails stops the batch, and its files say so. They are in the library
/// by then, so nothing is lost: Retry carries on with them, and an ordinary Run would find them too.
/// </para>
/// </summary>
public sealed partial class AddQueueViewModel : ObservableObject
{
    private readonly ILibraryService _service;
    private readonly LibraryViewModel _library;
    private readonly DoctorViewModel _doctor;
    private readonly SearchViewModel _search;
    private readonly Func<string?> _libraryRoot;

    private INotifyPropertyChanged? _watched;
    private TaskCompletionSource? _idle;

    /// <param name="libraryRoot">The library to add to, asked each time, or null when there is none.</param>
    public AddQueueViewModel(
        ILibraryService service,
        LibraryViewModel library,
        DoctorViewModel doctor,
        SearchViewModel search,
        Func<string?> libraryRoot)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _doctor = doctor ?? throw new ArgumentNullException(nameof(doctor));
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _libraryRoot = libraryRoot ?? throw new ArgumentNullException(nameof(libraryRoot));

        _library.PropertyChanged += OnOtherChanged;
        _doctor.PropertyChanged += OnOtherChanged;
        _search.PropertyChanged += OnOtherChanged;
    }

    public ObservableCollection<AddItem> Items { get; } = [];

    [ObservableProperty]
    private string _status = "Nothing added yet. Select PDFs in Explorer, right-click, and send them to the ManualForge library.";

    /// <summary>What the step under way says about itself: the run, the doctor or the index.</summary>
    [ObservableProperty]
    private string? _activity;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private bool _isWorking;

    /// <summary>Raised when files arrive, so the window can come forward and show them.</summary>
    public event EventHandler? Arrived;

    private bool OthersBusy => _library.IsBusy || _doctor.IsBusy || _search.IsBusy;

    /// <summary>Copies the files into the library now, and queues the rest of the work on them.</summary>
    public async Task AddAsync(IReadOnlyList<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
            return;

        var root = _libraryRoot();
        var items = sources.Select(s => new AddItem(s, root ?? "")).ToArray();
        foreach (var item in items)
            Items.Add(item);
        Arrived?.Invoke(this, EventArgs.Empty);

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            foreach (var item in items)
                Set(item, AddStage.Failed, "There is no library to add it to. Choose one with Browse, then add it again.");
            Status = "Nothing was added: there is no library folder.";
            return;
        }

        Status = $"Copying {items.Length:N0} file(s) into {root}...";
        IReadOnlyList<AddResult> results;
        try
        {
            results = await _service.AddAsync(root, sources, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            foreach (var item in items)
                Set(item, AddStage.Failed, $"Could not copy it: {ex.Message}");
            Status = $"Nothing was added: {ex.Message}";
            return;
        }

        for (var i = 0; i < items.Length && i < results.Count; i++)
        {
            var item = items[i];
            var result = results[i];
            item.LibraryPath = result.LibraryPath;
            switch (result.Kind)
            {
                case AddKind.Added:
                    Set(item, AddStage.Waiting, result.Renamed
                        ? $"Copied in as {Path.GetFileName(result.LibraryPath)}, because that name was taken."
                        : "Copied in.");
                    break;
                case AddKind.AlreadyHeld:
                    Set(item, AddStage.AlreadyHeld, $"The library already has it, as {result.LibraryPath}.");
                    break;
                case AddKind.Refused:
                    Set(item, AddStage.Refused, $"Not added: {result.Detail}.");
                    break;
                default:
                    Set(item, AddStage.Failed, $"Could not copy it: {result.Detail}");
                    break;
            }
        }

        // What the copying did; the work still to come says what it is waiting for, or that it has begun.
        var copied = items.Count(i => i.Stage == AddStage.Waiting);
        var held = items.Count(i => i.Stage == AddStage.AlreadyHeld);
        var notAdded = items.Length - copied - held;
        Status = (copied == 0 ? "Nothing new to add." : $"Copied {copied:N0} file(s) in.")
                 + (held == 0 ? "" : $" {held:N0} already in the library.")
                 + (notAdded == 0 ? "" : $" {notAdded:N0} not added; see each for why.");

        TryStart();
    }

    private bool CanRetry => !IsWorking && Items.Any(i => i.Stage == AddStage.Stopped);

    /// <summary>Carries on with the files a stopped batch left.</summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry()
    {
        foreach (var item in Items.Where(i => i.Stage == AddStage.Stopped))
            Set(item, AddStage.Waiting, "Waiting to carry on.");
        TryStart();
    }

    /// <summary>Takes the files nothing more will happen to off the list.</summary>
    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var item in Items.Where(i => i.IsFinished).ToArray())
            Items.Remove(item);
        if (Items.Count == 0)
            Status = "Nothing waiting.";
    }

    /// <summary>Starts the next batch if there is one and nothing else is working; else says what it waits for.</summary>
    private void TryStart()
    {
        RetryCommand.NotifyCanExecuteChanged();
        if (IsWorking)
            return;

        var waiting = Items.Where(i => i.Stage == AddStage.Waiting).ToArray();
        if (waiting.Length == 0)
            return;

        if (OthersBusy)
        {
            Status = $"{waiting.Length:N0} file(s) copied in, waiting for {Describe()} to finish.";
            return;
        }

        // One library at a time; a file added to another waits for the next batch.
        var root = waiting[0].Root;
        _ = ProcessAsync(waiting.Where(i => string.Equals(i.Root, root, StringComparison.OrdinalIgnoreCase)).ToArray(), root);
    }

    private string Describe() =>
        _library.IsBusy ? "the run in progress"
        : _doctor.IsBusy ? "the doctor"
        : "the index";

    private async Task ProcessAsync(AddItem[] batch, string root)
    {
        IsWorking = true;
        try
        {
            foreach (var item in batch)
                Set(item, AddStage.Processing, "Making it searchable...");

            // The window may be showing another folder. The page gives the others the folder too and
            // has them read it, so wait for that before using them.
            if (!string.Equals(_library.Folder, root, StringComparison.OrdinalIgnoreCase))
            {
                _library.Folder = root;
                _doctor.Folder = root;
                _search.Folder = root;
            }

            await WhenOthersIdleAsync().ConfigureAwait(true);

            Status = $"Making {batch.Length:N0} file(s) searchable.";
            Watch(_library);
            var outcomes = await _library.ProcessAsync(batch.Select(i => i.LibraryPath!).ToArray()).ConfigureAwait(true);
            if (outcomes is null)
            {
                Stop(batch, "Stopped while it was being made searchable.");
                return;
            }

            var byPath = outcomes.ToDictionary(o => Path.GetFullPath(o.Path), StringComparer.OrdinalIgnoreCase);
            var going = new List<AddItem>();
            var made = new HashSet<AddItem>();
            foreach (var item in batch)
            {
                if (byPath.TryGetValue(Path.GetFullPath(item.LibraryPath!), out var outcome))
                {
                    if (outcome.Status == FileStatus.Failed)
                    {
                        Set(item, AddStage.Failed, $"In the library, but could not be made searchable: {outcome.Error}");
                        continue;
                    }

                    if (outcome.RenamedTo is not null)
                        item.LibraryPath = outcome.RenamedTo;
                    made.Add(item);
                }

                Set(item, AddStage.Processing, "Checking its text for what it misses...");
                going.Add(item);
            }

            if (going.Count > 0)
            {
                Status = $"Checking {going.Count:N0} file(s) for lettering their text misses, and recovering it.";
                Watch(_doctor);
                if (!await _doctor.AuditAndRepairAsync(going.Select(i => i.LibraryPath!).ToArray()).ConfigureAwait(true))
                {
                    Stop(going, "Stopped while its text was being checked and recovered.");
                    return;
                }

                foreach (var item in going)
                    Set(item, AddStage.Processing, "Indexing...");

                Status = "Indexing, so search finds them.";
                Watch(_search);
                if (!await _search.IndexAsync().ConfigureAwait(true))
                {
                    Stop(going, "Stopped while the library was being indexed.");
                    return;
                }

                foreach (var item in going)
                {
                    Set(item, AddStage.Done, made.Contains(item)
                        ? "Made searchable, checked and indexed."
                        : "Its text was already there; checked and indexed.");
                }
            }

            var done = batch.Count(i => i.Stage == AddStage.Done);
            var failed = batch.Length - done;
            Status = failed == 0
                ? $"Added {done:N0} file(s). Search finds them now."
                : $"Added {done:N0} file(s); {failed:N0} could not be made searchable. See each for why.";
        }
        catch (Exception ex)
        {
            Stop(batch.Where(i => !i.IsFinished), $"Stopped: {ex.Message}");
        }
        finally
        {
            Watch(null);
            IsWorking = false;
            TryStart();
        }
    }

    private void Stop(IEnumerable<AddItem> items, string detail)
    {
        foreach (var item in items)
            Set(item, AddStage.Stopped, detail + " It is in the library; Retry carries on.");
        Status = "Stopped. The files are in the library; Retry carries on with them.";
    }

    private static void Set(AddItem item, AddStage stage, string detail)
    {
        item.Stage = stage;
        item.Detail = detail;
    }

    /// <summary>Shows what this step says about itself as <see cref="Activity"/>, until the next.</summary>
    private void Watch(ObservableObject? step)
    {
        _watched = step;
        Activity = step switch
        {
            LibraryViewModel l => l.Status,
            DoctorViewModel d => d.Status,
            SearchViewModel s => s.Status,
            _ => null,
        };
    }

    private void OnOtherChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Status" && ReferenceEquals(sender, _watched))
            Watch((ObservableObject)sender!);

        if (e.PropertyName != "IsBusy")
            return;

        if (!OthersBusy)
        {
            var idle = _idle;
            _idle = null;
            idle?.TrySetResult();
        }

        TryStart();
    }

    private Task WhenOthersIdleAsync()
    {
        if (!OthersBusy)
            return Task.CompletedTask;

        _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _idle.Task;
    }
}
