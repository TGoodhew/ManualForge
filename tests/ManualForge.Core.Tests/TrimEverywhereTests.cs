using ManualForge.Core.Indexing;
using ManualForge.Shell;

namespace ManualForge.Core.Tests;

/// <summary>
/// The app's Trim forgets a deleted manual everywhere ManualForge remembers it, as the command line's
/// three --trim-missing options do. Until 1.0.3 it trimmed only the work queue, so a deleted manual
/// could still be returned by search, in the app and through MCP.
/// </summary>
public sealed class TrimEverywhereTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "manualforge-trim-" + Guid.NewGuid().ToString("N"));

    public TrimEverywhereTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ADeletedManualLeavesTheSearchIndexToo()
    {
        var kept = TestPdf.TypesetOnly(Path.Combine(_root, "kept.pdf"), "This manual stays in the library.", pages: 2);
        var deleted = TestPdf.TypesetOnly(Path.Combine(_root, "deleted.pdf"), "This manual is deleted by its owner.", pages: 2);
        await new LibraryIndexer().IndexAsync(_root, new IndexOptions());

        File.Delete(deleted);
        await using var service = new LibraryService(new LibraryServiceOptions());
        var forgotten = await service.TrimMissingAsync(_root, CancellationToken.None);

        Assert.Contains(forgotten, p => p.EndsWith("deleted.pdf", StringComparison.OrdinalIgnoreCase));
        using var index = new SearchIndex(LibraryIndexer.DefaultIndexPath(_root), readOnly: true);
        var paths = index.Documents().Select(d => Path.GetFileName(d.Path)).ToArray();
        Assert.Equal(["kept.pdf"], paths);
        Assert.True(File.Exists(kept));
    }
}
