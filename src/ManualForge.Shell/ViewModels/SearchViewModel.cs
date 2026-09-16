using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualForge.Core.Indexing;

namespace ManualForge.Shell.ViewModels;

/// <summary>Everything the search tab needs from the index, behind one seam so it can be faked.</summary>
public interface ISearchService
{
    /// <summary>Whether an index exists for this library yet.</summary>
    bool HasIndex(string root);

    Task<IReadOnlyList<SearchHit>> SearchAsync(string root, string query, int limit, CancellationToken cancellationToken);

    Task<IndexStatistics?> StatisticsAsync(string root, CancellationToken cancellationToken);

    Task<IndexReport> BuildAsync(string root, IProgress<IndexProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// The search tab: a query, results as manual, page and snippet, and a way to build the index if
/// there is not one.
/// </summary>
public sealed partial class SearchViewModel(ISearchService service, IUiDispatcher? dispatcher = null)
    : ObservableObject
{
    private readonly ISearchService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly IUiDispatcher _dispatcher = dispatcher ?? InlineDispatcher.Instance;

    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildIndexCommand))]
    private string? _folder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _query = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildIndexCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Build an index, then ask it something.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private bool _hasIndex;

    public ObservableCollection<SearchHit> Results { get; } = [];

    public bool IsIdle => !IsBusy;

    private bool CanSearch =>
        !IsBusy && HasIndex && !string.IsNullOrWhiteSpace(Query) && !string.IsNullOrWhiteSpace(Folder);

    private bool CanBuild => !IsBusy && !string.IsNullOrWhiteSpace(Folder);

    /// <summary>Re-reads whether an index exists, and how big it is. Called when the folder changes.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Folder))
        {
            HasIndex = false;
            Status = "Pick a folder first.";
            return;
        }

        HasIndex = _service.HasIndex(Folder);
        if (!HasIndex)
        {
            Status = "No index for this folder yet.";
            return;
        }

        var statistics = await _service.StatisticsAsync(Folder, cancellationToken).ConfigureAwait(true);
        Status = statistics is null
            ? "Index found."
            : $"{statistics.Documents:N0} documents, {statistics.Pages:N0} pages indexed " +
              $"({statistics.SizeBytes / 1024.0 / 1024.0:F0} MB).";
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        IsBusy = true;
        Results.Clear();

        try
        {
            var hits = await _service
                .SearchAsync(Folder!, Query, 50, CancellationToken.None)
                .ConfigureAwait(true);

            foreach (var hit in hits)
                Results.Add(hit);

            Status = hits.Count == 0
                ? $"Nothing matched {Query}."
                : $"{hits.Count:N0} result(s) for {Query}.";
        }
        catch (Exception ex)
        {
            // A malformed FTS5 query is the common case here - an unbalanced quote, a bare NEAR -
            // and it is the user's typing, not a fault.
            Status = $"That query could not be run: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildIndexAsync()
    {
        IsBusy = true;
        _cancellation = new CancellationTokenSource();

        var progress = new DispatchedProgress<IndexProgress>(_dispatcher, p =>
            Status = $"Indexing {p.DocumentsDone:N0}/{p.DocumentsTotal:N0} documents, {p.PagesIndexed:N0} pages...");

        try
        {
            var report = await _service
                .BuildAsync(Folder!, progress, _cancellation.Token)
                .ConfigureAwait(true);

            HasIndex = true;
            Status =
                $"Indexed {report.DocumentsIndexed:N0} document(s), {report.PagesIndexed:N0} pages " +
                $"in {report.Elapsed.TotalMinutes:F1} min" +
                (report.DocumentsWithoutText > 0
                    ? $". {report.DocumentsWithoutText:N0} have no text layer to index yet."
                    : ".");
        }
        catch (OperationCanceledException)
        {
            Status = "Indexing cancelled. What was done is kept.";
        }
        catch (Exception ex)
        {
            Status = $"Indexing failed: {ex.Message}";
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();
}
