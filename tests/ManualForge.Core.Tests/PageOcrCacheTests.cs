using ManualForge.Core.Geometry;
using ManualForge.Core.Ocr;
using ManualForge.Core.State;

namespace ManualForge.Core.Tests;

/// <summary>
/// Resume works by remembering recognition, not by resuming the writer. Recognising a 639-page
/// manual takes thirteen minutes on the GPU; assembling the PDF from results already in hand takes
/// seconds. So an interrupted document is rebuilt from scratch, reusing the recognition it already
/// has, and the interruption costs the page in flight rather than the document.
/// </summary>
public class PageOcrCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));

    public PageOcrCacheTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private SqlitePageOcrCache NewCache(string name = "cache.db") =>
        new(Path.Combine(_directory, name));

    private static RecognisedWord[] Words(params string[] texts) =>
        texts.Select((t, i) => new RecognisedWord(t, new RectD(100 + i * 50, 200, 45, 18), 0.9)).ToArray();

    private const string Settings = "dpi=300;grey=True;provider=Cuda;conf=0.3";

    [Fact]
    public void WordsComeBackExactlyAsTheyWentIn()
    {
        using var cache = NewCache();
        var words = Words("HEWLETT", "PACKARD", "59401A");

        cache.Save("manual.pdf", 7, Settings, words);
        var recovered = cache.TryGet("manual.pdf", 7, Settings);

        Assert.NotNull(recovered);
        Assert.Equal(3, recovered.Count);
        for (var i = 0; i < words.Length; i++)
        {
            Assert.Equal(words[i].Text, recovered[i].Text);
            Assert.Equal(words[i].BoxPx.X, recovered[i].BoxPx.X, 6);
            Assert.Equal(words[i].BoxPx.Y, recovered[i].BoxPx.Y, 6);
            Assert.Equal(words[i].BoxPx.Width, recovered[i].BoxPx.Width, 6);
            Assert.Equal(words[i].BoxPx.Height, recovered[i].BoxPx.Height, 6);
            Assert.Equal(words[i].Confidence, recovered[i].Confidence, 6);
        }
    }

    [Fact]
    public void APageNeverRecognisedReturnsNothing()
    {
        using var cache = NewCache("empty.db");
        Assert.Null(cache.TryGet("manual.pdf", 1, Settings));
    }

    [Fact]
    public void CachedRecognitionSurvivesTheProcessThatMadeIt()
    {
        // The point of putting this in SQLite rather than memory: a crash or a reboot must not
        // lose it.
        const string db = "durable.db";
        using (var cache = NewCache(db))
            cache.Save("manual.pdf", 42, Settings, Words("frequency", "controlling"));

        using (var reopened = NewCache(db))
        {
            var recovered = reopened.TryGet("manual.pdf", 42, Settings);
            Assert.NotNull(recovered);
            Assert.Equal(["frequency", "controlling"], recovered.Select(w => w.Text));
        }
    }

    [Fact]
    public void ResultsFromDifferentSettingsAreNeverReused()
    {
        // The dangerous case. Word boxes are in image pixels at a particular resolution; reusing
        // boxes recognised at 300 dpi for a run at 600 would put every word in the wrong place, and
        // nothing downstream could detect it.
        using var cache = NewCache("settings.db");
        cache.Save("manual.pdf", 1, "dpi=300;grey=True;provider=Cuda;conf=0.3", Words("at three hundred"));

        Assert.Null(cache.TryGet("manual.pdf", 1, "dpi=600;grey=True;provider=Cuda;conf=0.3"));
        Assert.NotNull(cache.TryGet("manual.pdf", 1, "dpi=300;grey=True;provider=Cuda;conf=0.3"));
    }

    [Fact]
    public void InterruptingHalfwayLeavesTheFinishedPagesReusable()
    {
        using var cache = NewCache("resume.db");
        const string manual = "639-page-manual.pdf";

        // Recognise 300 pages, then "crash".
        for (var page = 1; page <= 300; page++)
            cache.Save(manual, page, Settings, Words($"page{page}"));

        Assert.Equal(300, cache.CachedPageCount(manual, Settings));

        // The next attempt finds every one of them and re-recognises only what is missing.
        var reused = 0;
        var toRecognise = 0;
        for (var page = 1; page <= 639; page++)
        {
            if (cache.TryGet(manual, page, Settings) is not null) reused++;
            else toRecognise++;
        }

        Assert.Equal(300, reused);
        Assert.Equal(339, toRecognise);
    }

    [Fact]
    public void FinishingADocumentReleasesItsCache()
    {
        // Otherwise the cache grows to hold the recognition of the entire library rather than the
        // documents actually in flight.
        using var cache = NewCache("release.db");

        for (var page = 1; page <= 50; page++)
            cache.Save("done.pdf", page, Settings, Words("word"));
        for (var page = 1; page <= 10; page++)
            cache.Save("other.pdf", page, Settings, Words("word"));

        cache.Clear("done.pdf");

        Assert.Equal(0, cache.CachedPageCount("done.pdf", Settings));
        Assert.Equal(10, cache.CachedPageCount("other.pdf", Settings));
    }

    [Fact]
    public void APageThatRecognisedNothingIsStillRecordedAsDone()
    {
        // A blank page legitimately yields no words. Storing that distinguishes "recognised, found
        // nothing" from "not yet recognised", so a resumed run does not redo every blank page.
        using var cache = NewCache("blank.db");
        cache.Save("manual.pdf", 5, Settings, []);

        var recovered = cache.TryGet("manual.pdf", 5, Settings);

        Assert.NotNull(recovered);
        Assert.Empty(recovered);
    }

    [Fact]
    public void RerecognisingAPageReplacesWhatWasStored()
    {
        using var cache = NewCache("replace.db");
        cache.Save("manual.pdf", 1, Settings, Words("first", "attempt"));
        cache.Save("manual.pdf", 1, Settings, Words("second", "attempt", "better"));

        Assert.Equal(3, cache.TryGet("manual.pdf", 1, Settings)!.Count);
        Assert.Equal(1, cache.CachedPageCount("manual.pdf", Settings));
    }

    [Fact]
    public void TheNullCacheRemembersNothing()
    {
        var cache = NullPageOcrCache.Instance;
        cache.Save("manual.pdf", 1, Settings, Words("ignored"));

        Assert.Null(cache.TryGet("manual.pdf", 1, Settings));
    }
}
