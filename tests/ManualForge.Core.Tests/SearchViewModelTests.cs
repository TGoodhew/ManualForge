using ManualForge.Core.Indexing;
using ManualForge.Shell.ViewModels;

namespace ManualForge.Core.Tests;

internal sealed class FakeSearchService : ISearchService
{
    public bool IndexExists { get; set; }

    public List<SearchHit> Hits { get; } = [];

    public IndexStatistics? Stats { get; set; }

    public Exception? SearchThrows { get; set; }

    public string? LastQuery { get; private set; }

    public int BuildCalls { get; private set; }

    /// <summary>Runs inside BuildAsync, so a test can cancel part-way.</summary>
    public Func<CancellationToken, Task>? DuringBuild { get; set; }

    public bool HasIndex(string root) => IndexExists;

    public Task<IReadOnlyList<SearchHit>> SearchAsync(
        string root, string query, int limit, CancellationToken cancellationToken)
    {
        LastQuery = query;
        return SearchThrows is not null
            ? Task.FromException<IReadOnlyList<SearchHit>>(SearchThrows)
            : Task.FromResult<IReadOnlyList<SearchHit>>(Hits.ToArray());
    }

    public Task<IndexStatistics?> StatisticsAsync(string root, CancellationToken cancellationToken)
        => Task.FromResult(Stats);

    public async Task<IndexReport> BuildAsync(
        string root, IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        BuildCalls++;
        progress?.Report(new IndexProgress("a.pdf", 1, 2, 10));

        if (DuringBuild is not null)
            await DuringBuild(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        IndexExists = true;
        return new IndexReport(2, 0, 0, 1, 40, TimeSpan.FromMinutes(1));
    }
}

/// <summary>
/// The search tab, driven without a window. What matters is that it cannot be used before there is
/// something to search, that a bad query is reported as the user's typing rather than as a crash,
/// and that the state it reports is true.
/// </summary>
public class SearchViewModelTests
{
    private static SearchHit Hit(string title, int page, string snippet, params string[] alsoAt) =>
        new($@"C:\Manuals\{title}.pdf", title, page, snippet, -1.5, alsoAt);

    /// <summary>
    /// A model in the state the application would actually have it in: a folder chosen and the
    /// index looked for. Skipping the refresh leaves HasIndex false, which no real window does.
    /// </summary>
    private static async Task<(SearchViewModel Model, FakeSearchService Service)> NewAsync(bool indexed = true)
    {
        var service = new FakeSearchService { IndexExists = indexed };
        var model = new SearchViewModel(service) { Folder = @"C:\Manuals" };
        await model.RefreshAsync();
        return (model, service);
    }

    [Fact]
    public async Task SearchingNeedsAFolderAnIndexAndAQuery()
    {
        var service = new FakeSearchService { IndexExists = false };
        var model = new SearchViewModel(service);

        Assert.False(model.SearchCommand.CanExecute(null));
        Assert.False(model.BuildIndexCommand.CanExecute(null));

        model.Folder = @"C:\Manuals";
        await model.RefreshAsync();

        // A folder with no index can be indexed but not searched.
        Assert.True(model.BuildIndexCommand.CanExecute(null));
        Assert.False(model.SearchCommand.CanExecute(null));
        Assert.Contains("No index", model.Status, StringComparison.Ordinal);

        service.IndexExists = true;
        await model.RefreshAsync();
        Assert.False(model.SearchCommand.CanExecute(null));   // still no query

        model.Query = "handshake";
        Assert.True(model.SearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task ResultsCarryTheManualThePageAndASnippet()
    {
        var (model, service) = await NewAsync();
        service.Hits.Add(Hit("59401A", 42, "the HP-IB [handshake] lines are carried on pins 6 to 11"));
        model.Query = "handshake";

        await model.SearchCommand.ExecuteAsync(null);

        var hit = Assert.Single(model.Results);
        Assert.Equal("59401A", hit.Title);
        Assert.Equal(42, hit.PageNumber);
        Assert.Contains("pins 6 to 11", hit.Snippet, StringComparison.Ordinal);
        Assert.Equal("handshake", service.LastQuery);
        Assert.Contains("1 result", model.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQueryThatMatchesNothingSaysSoRatherThanLookingBroken()
    {
        var (model, _) = await NewAsync();
        model.Query = "flux capacitor";

        await model.SearchCommand.ExecuteAsync(null);

        Assert.Empty(model.Results);
        Assert.Contains("Nothing matched", model.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedQueryIsReportedAsTypingNotAsAFailure()
    {
        // FTS5 rejects an unbalanced quote or a bare NEAR. That is the user's input, and the tab
        // has to stay usable afterwards.
        var (model, service) = await NewAsync();
        service.SearchThrows = new InvalidOperationException("fts5: syntax error near \"\"");
        model.Query = "\"unbalanced";

        await model.SearchCommand.ExecuteAsync(null);

        Assert.Contains("could not be run", model.Status, StringComparison.Ordinal);
        Assert.True(model.IsIdle);
        Assert.True(model.SearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task ASecondSearchReplacesTheFirstRatherThanAppending()
    {
        var (model, service) = await NewAsync();
        service.Hits.Add(Hit("A", 1, "first"));
        model.Query = "one";
        await model.SearchCommand.ExecuteAsync(null);

        service.Hits.Clear();
        service.Hits.Add(Hit("B", 2, "second"));
        model.Query = "two";
        await model.SearchCommand.ExecuteAsync(null);

        Assert.Equal("B", Assert.Single(model.Results).Title);
    }

    [Fact]
    public async Task BuildingAnIndexReportsWhatItDidAndEnablesSearching()
    {
        var (model, service) = await NewAsync(indexed: false);
        Assert.False(model.HasIndex);

        await model.BuildIndexCommand.ExecuteAsync(null);

        Assert.Equal(1, service.BuildCalls);
        Assert.True(model.HasIndex);
        Assert.Contains("2 document", model.Status, StringComparison.Ordinal);

        // Documents with no text layer are named rather than quietly missing from the count.
        Assert.Contains("no text layer", model.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingAnIndexKeepsWhatWasDone()
    {
        var (model, service) = await NewAsync(indexed: false);
        service.DuringBuild = async token =>
        {
            model.CancelCommand.Execute(null);
            await Task.Delay(10, CancellationToken.None);
        };

        await model.BuildIndexCommand.ExecuteAsync(null);

        Assert.Contains("cancelled", model.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kept", model.Status, StringComparison.Ordinal);
        Assert.True(model.IsIdle);
    }

    [Fact]
    public async Task TheStatusLineSaysHowBigTheIndexIs()
    {
        var (model, service) = await NewAsync();
        service.Stats = new IndexStatistics(576, 101_733, 300L * 1024 * 1024);

        await model.RefreshAsync();

        Assert.Contains("576 documents", model.Status, StringComparison.Ordinal);
        Assert.Contains("101,733 pages", model.Status, StringComparison.Ordinal);
        Assert.Contains("300 MB", model.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicatePathsTravelWithTheResult()
    {
        var (model, service) = await NewAsync();
        service.Hits.Add(Hit("8340B", 7, "the YIG oscillator", @"C:\Manuals\copy.pdf"));
        model.Query = "oscillator";

        await model.SearchCommand.ExecuteAsync(null);

        var hit = Assert.Single(model.Results);
        Assert.True(hit.HasDuplicates);
        Assert.Single(hit.AlsoAt);
    }

    [Fact]
    public async Task NothingCanBeStartedTwiceAtOnce()
    {
        var (model, service) = await NewAsync(indexed: false);
        var couldStartAnother = true;
        service.DuringBuild = token =>
        {
            couldStartAnother = model.BuildIndexCommand.CanExecute(null);
            return Task.CompletedTask;
        };

        await model.BuildIndexCommand.ExecuteAsync(null);

        Assert.False(couldStartAnother);
    }
}
