using ManualForge.Core.Indexing;
using ManualForge.Shell.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualForge.Shell;

/// <summary>
/// The real search service.
///
/// The index is opened per call rather than held. A search takes milliseconds, and a connection
/// left open would keep a write lock on the file that the command line also wants to index into.
/// </summary>
public sealed class SearchService(ILoggerFactory? loggerFactory = null) : ISearchService
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    public bool HasIndex(string root) => File.Exists(LibraryIndexer.DefaultIndexPath(root));

    public Task<IReadOnlyList<SearchHit>> SearchAsync(
        string root, string query, int limit, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                using var index = new SearchIndex(LibraryIndexer.DefaultIndexPath(root), readOnly: true);
                return index.Search(query, limit);
            },
            cancellationToken);

    public Task<IndexStatistics?> StatisticsAsync(string root, CancellationToken cancellationToken) =>
        Task.Run<IndexStatistics?>(
            () =>
            {
                if (!HasIndex(root))
                    return null;

                using var index = new SearchIndex(LibraryIndexer.DefaultIndexPath(root), readOnly: true);
                return index.Statistics();
            },
            cancellationToken);

    public Task<IndexReport> BuildAsync(
        string root, IProgress<IndexProgress>? progress, CancellationToken cancellationToken) =>
        new LibraryIndexer(_loggerFactory.CreateLogger<LibraryIndexer>())
            .IndexAsync(root, new IndexOptions(), progress, cancellationToken);
}
