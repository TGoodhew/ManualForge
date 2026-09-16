using ManualForge.Core.Classification;
using ManualForge.Core.Pdf;
using ManualForge.Core.Pipeline;
using ManualForge.Core.State;

namespace ManualForge.Core.Tests;

/// <summary>
/// A library assembled over years accumulates copies of the same manual under different names.
/// Recognising each copy costs GPU hours and makes the search index return the same document
/// several times, so identical content must be recognised once.
/// </summary>
public class DeduplicationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));

    public DeduplicationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private JobStore NewStore(string name = "dedup.db") => new(Path.Combine(_directory, name));

    private static FileRecord Register(JobStore store, string path, int pages = 100)
    {
        store.Register(path);
        store.RecordClassification(
            path,
            new DocumentClassification(path, pages, 8, 10, 0.9, 0.3, TextClass.ImageOnly, "no text", []),
            new PdfCapabilities(path, true, true, ModificationBlocker.None, false, false, pages, null),
            ClassAction.Ocr);
        return store.Find(path)!;
    }

    [Fact]
    public void ReferenceCopiesAndPreservedOriginalsAreNeverDiscoveredAsWork()
    {
        // BASELINE holds another engine's output, kept so its quality can be measured against ours.
        // Processing those would overwrite the very thing they exist to compare against.
        Write("real-manual.pdf", "a manual");
        Write(Path.Combine("BASELINE", "real-manual.pdf"), "the same manual as Acrobat left it");
        Write(Path.Combine("_Originals", "real-manual.pdf"), "the untouched original");
        Write(Path.Combine("Subfolder", "another.pdf"), "another manual");

        var options = new LibraryOptions { Root = _directory };
        var found = new LibraryProcessor(null, new DocumentClassifier())
            .Discover(options)
            .Select(f => Path.GetRelativePath(_directory, f))
            .ToArray();

        Assert.Equal(2, found.Length);
        Assert.Contains("real-manual.pdf", found);
        Assert.Contains(Path.Combine("Subfolder", "another.pdf"), found);
        Assert.DoesNotContain(found, f => f.StartsWith("BASELINE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, f => f.StartsWith("_Originals", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IdenticalFilesAreRecognisedOnceAndCopiedToTheRest()
    {
        const string content = "the same scanned manual, byte for byte";
        var main = Write("2235_lg.pdf", content);
        var staged = Write(Path.Combine("_OCR_QUEUE", "005_2235_lg.pdf"), content);
        var other = Write(Path.Combine("deep", "folder", "TDS 2235 copy.pdf"), content);

        using var store = NewStore();
        var records = new[] { Register(store, main), Register(store, staged), Register(store, other) };

        var report = new ContentDeduplicator().Apply(store, records);

        Assert.Equal(1, report.DistinctDocuments);
        Assert.Equal(2, report.RedundantFiles);
        Assert.Equal(200, report.RedundantPages);

        // The shallowest path wins, without the deduplicator needing to know what _OCR_QUEUE is.
        var group = Assert.Single(report.Groups);
        Assert.Equal(main, group.Primary);

        Assert.Equal(ClassAction.Ocr, store.Find(main)!.Action);
        Assert.Equal(ClassAction.CopyFromDuplicate, store.Find(staged)!.Action);
        Assert.Equal(ClassAction.CopyFromDuplicate, store.Find(other)!.Action);
        Assert.Equal(main, store.Find(staged)!.DuplicateOf);
    }

    [Fact]
    public void FilesThatMerelyShareANameAreNotTreatedAsDuplicates()
    {
        // Two different scans of the same manual are not interchangeable, however alike the names.
        var a = Write("438A.pdf", "one scan");
        var b = Write(Path.Combine("_OCR_QUEUE", "012_438A.pdf"), "a different scan of the same manual");

        using var store = NewStore("names.db");
        var report = new ContentDeduplicator().Apply(store, [Register(store, a), Register(store, b)]);

        Assert.Equal(2, report.DistinctDocuments);
        Assert.Empty(report.Groups);
        Assert.Equal(ClassAction.Ocr, store.Find(b)!.Action);
    }

    [Fact]
    public void AUniqueFileIsLeftAlone()
    {
        var only = Write("unique.pdf", "nothing else looks like this");

        using var store = NewStore("unique.db");
        var report = new ContentDeduplicator().Apply(store, [Register(store, only)]);

        Assert.Empty(report.Groups);
        Assert.Equal(ClassAction.Ocr, store.Find(only)!.Action);
        Assert.NotNull(store.Find(only)!.ContentHash);
        Assert.Null(store.Find(only)!.DuplicateOf);
    }

    [Fact]
    public void TheContentHashIsRecordedSoTheIndexCanGroupCopiesLater()
    {
        const string content = "identical";
        var a = Write("a.pdf", content);
        var b = Write("bb.pdf", content);

        using var store = NewStore("hash.db");
        new ContentDeduplicator().Apply(store, [Register(store, a), Register(store, b)]);

        var hashA = store.Find(a)!.ContentHash;
        var hashB = store.Find(b)!.ContentHash;

        Assert.False(string.IsNullOrEmpty(hashA));
        Assert.Equal(hashA, hashB);
    }

    [Theory]
    // Real pairs from the library. In every one the descriptive name is the better search result,
    // and the shortest-name rule this replaced picked the part number instead.
    [InlineData("kei2015-sman.pdf", "2015THD Service.pdf")]
    [InlineData("LC574AL.pdf", "LeCroy-5674 User.pdf")]
    [InlineData("smd-00243_AN30.pdf", "Basic THD Measurement.pdf")]
    [InlineData("08340-90243.pdf", "HP 8340B, 41B Assembly Level Service.pdf")]
    public void TheMoreDescriptiveNameWinsATieAtTheSameDepth(string terse, string descriptive)
    {
        const string content = "the same manual under two names";
        var a = Write(terse, content);
        var b = Write(descriptive, content);

        using var store = NewStore(Guid.NewGuid().ToString("N") + ".db");
        var report = new ContentDeduplicator().Apply(store, [Register(store, a), Register(store, b)]);

        // The primary is the path the search index will cite, so it should be the one a person can
        // recognise at a glance.
        Assert.Equal(b, Assert.Single(report.Groups).Primary);
    }

    [Fact]
    public void DepthStillBeatsDescriptiveness()
    {
        // A manual in the library root beats the same manual staged in a subfolder, however much
        // more the subfolder copy's name says. That rule needs no knowledge of the folder.
        const string content = "identical";
        var shallow = Write("LC574AL.pdf", content);
        var deep = Write(Path.Combine("_OCR_QUEUE", "083_LeCroy 5674 Service Manual.pdf"), content);

        using var store = NewStore("depth.db");
        var report = new ContentDeduplicator().Apply(store, [Register(store, shallow), Register(store, deep)]);

        Assert.Equal(shallow, Assert.Single(report.Groups).Primary);
    }

    [Fact]
    public void AChangedFileForgetsItsOldContentHash()
    {
        // The dangerous version of stale state. If a file's recorded hash outlived a change to its
        // contents, deduplication would group it with whatever it used to match - and copy that
        // other document over it.
        const string original = "the original scan";
        var a = Write("first.pdf", original);
        var b = Write("second.pdf", original);

        using var store = NewStore("changed.db");
        new ContentDeduplicator().Apply(store, [Register(store, a), Register(store, b)]);

        var sharedHash = store.Find(a)!.ContentHash;
        Assert.Equal(sharedHash, store.Find(b)!.ContentHash);
        Assert.Equal(ClassAction.CopyFromDuplicate, store.Find(b)!.Action);

        // Replace one of them with something entirely different.
        Thread.Sleep(10);
        File.WriteAllText(b, "a completely different manual with different contents entirely");

        var reregistered = store.Register(b);

        Assert.Null(reregistered.ContentHash);
        Assert.Null(reregistered.DuplicateOf);

        // And a fresh pass must now see two distinct documents, not a duplicate pair.
        var report = new ContentDeduplicator().Apply(store, [Register(store, a), store.Find(b)!]);

        Assert.Equal(2, report.DistinctDocuments);
        Assert.Empty(report.Groups);
    }

    [Fact]
    public void DeduplicationSurvivesAFileThatCannotBeRead()
    {
        var readable = Write("readable.pdf", "content");
        var missing = Path.Combine(_directory, "vanished.pdf");
        File.WriteAllText(missing, "temporary");

        using var store = NewStore("missing.db");
        var records = new[] { Register(store, readable), Register(store, missing) };
        File.Delete(missing);

        // A file that disappears between survey and hashing must not bring the pass down.
        var report = new ContentDeduplicator().Apply(store, records);

        Assert.Equal(1, report.DistinctDocuments);
    }
}
