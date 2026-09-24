using ManualForge.Core.Indexing;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// The indexer extracts several documents at once and writes them from a single thread. These are
/// the properties that has to keep: the same library indexed with one worker and with eight must
/// produce the same index, every document must be written exactly once, and a file that will not
/// open must cost only itself.
///
/// <para>
/// Worth testing rather than reasoning about, because the failure mode of getting it wrong is not a
/// crash. It is an index that is quietly missing a document, or holding one twice, on a run that
/// takes hours and reports success.
/// </para>
/// </summary>
public sealed class LibraryIndexerParallelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "manualforge-index-" + Guid.NewGuid().ToString("N"));

    public LibraryIndexerParallelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Twelve documents, each with a word that appears in no other.</summary>
    private string[] GivenALibrary(int documents = 12)
    {
        var paths = new string[documents];

        for (var i = 0; i < documents; i++)
        {
            paths[i] = TestPdf.TypesetOnly(
                Path.Combine(_root, $"manual-{i:00}.pdf"),
                $"The keyword for this document is distinctive{i:00} and it appears nowhere else.",
                pages: 3);
        }

        return paths;
    }

    private Task<IndexReport> IndexAsync(string indexName, int workers) =>
        new LibraryIndexer().IndexAsync(
            _root,
            new IndexOptions
            {
                IndexPath = Path.Combine(_root, indexName),
                Workers = workers,
            });

    [Fact]
    public async Task OneWorkerAndEightProduceTheSameIndex()
    {
        var paths = GivenALibrary();

        var serial = await IndexAsync("serial.db", workers: 1);
        var parallel = await IndexAsync("parallel.db", workers: 8);

        Assert.Equal(serial.DocumentsIndexed, parallel.DocumentsIndexed);
        Assert.Equal(paths.Length, parallel.DocumentsIndexed);
        Assert.Equal(serial.PagesIndexed, parallel.PagesIndexed);
        Assert.Equal(0, parallel.DocumentsFailed);

        using var one = new SearchIndex(Path.Combine(_root, "serial.db"), readOnly: true);
        using var many = new SearchIndex(Path.Combine(_root, "parallel.db"), readOnly: true);

        // Every document, not just the count: a race that drops one would keep the totals
        // plausible while losing exactly the manual somebody later searches for.
        for (var i = 0; i < paths.Length; i++)
        {
            var fromOne = one.Search($"distinctive{i:00}")
                .Select(h => (h.Path, h.PageNumber, h.Title)).OrderBy(h => h.PageNumber).ToArray();
            var fromMany = many.Search($"distinctive{i:00}")
                .Select(h => (h.Path, h.PageNumber, h.Title)).OrderBy(h => h.PageNumber).ToArray();

            Assert.NotEmpty(fromOne);
            Assert.Equal(fromOne, fromMany);
        }
    }

    [Fact]
    public async Task ASecondRunWritesNothingAgain()
    {
        GivenALibrary(documents: 8);

        var first = await IndexAsync("index.db", workers: 4);
        var second = await IndexAsync("index.db", workers: 4);

        Assert.Equal(8, first.DocumentsIndexed);
        Assert.Equal(0, first.DocumentsUnchanged);

        // Both hashes are recorded by the writer thread rather than the worker, so this is the
        // assertion that the write actually happened for every document the workers handed over.
        Assert.Equal(0, second.DocumentsIndexed);
        Assert.Equal(8, second.DocumentsUnchanged);
    }

    [Fact]
    public async Task AFileThatWillNotOpenCostsOnlyItself()
    {
        GivenALibrary(documents: 6);
        await File.WriteAllTextAsync(Path.Combine(_root, "broken.pdf"), "this is not a PDF at all");

        var report = await IndexAsync("index.db", workers: 4);

        Assert.Equal(1, report.DocumentsFailed);
        Assert.Equal(6, report.DocumentsIndexed);

        using var index = new SearchIndex(Path.Combine(_root, "index.db"), readOnly: true);
        Assert.NotEmpty(index.Search("distinctive05"));
    }
}
