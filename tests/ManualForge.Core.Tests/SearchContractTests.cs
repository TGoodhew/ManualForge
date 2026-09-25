using ManualForge.Core.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// The index is a file another application is invited to read, which makes some of its behaviour a
/// promise rather than an implementation detail: what it says about its own schema, what it admits
/// it cannot answer, and what it does with a hint the caller supplies.
/// </summary>
public sealed class SearchContractTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "manualforge-contract-" + Guid.NewGuid().ToString("N"));

    public SearchContractTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Path_(string name) => Path.Combine(_directory, name);

    private static IndexedPageText[] Pages(params string[] text) =>
        text.Select(Dehyphenator.Prepare).ToArray();

    [Fact]
    public void ANewIndexSaysWhichSchemaItIs()
    {
        var path = Path_("index.db");
        using (var index = new SearchIndex(path))
            index.AddDocument(@"C:\Manuals\a.pdf", "A", "hash-a", Pages("text"));

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";

        Assert.Equal(SearchIndex.SchemaVersion, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public void AnIndexFromTheFutureIsRefusedRatherThanGuessedAt()
    {
        var path = Path_("future.db");
        using (var index = new SearchIndex(path))
            index.AddDocument(@"C:\Manuals\a.pdf", "A", "hash-a", Pages("handshake"));

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {SearchIndex.SchemaVersion + 5}";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        // The alternative is reading it anyway and being subtly wrong about a column whose meaning
        // changed, which is the failure this number exists to prevent.
        var error = Assert.Throws<InvalidOperationException>(() => new SearchIndex(path, readOnly: true));
        Assert.Contains("newer version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIndexWrittenBeforeTheStampExistedIsStillRead()
    {
        var path = Path_("old.db");
        using (var index = new SearchIndex(path))
            index.AddDocument(@"C:\Manuals\a.pdf", "A", "hash-a", Pages("handshake"));

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 0";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        // Version 0 is every index built before anybody promised anything. Those indexes are
        // correct, and refusing them would throw away hours of work to enforce a rule invented
        // after they were written.
        using var reopened = new SearchIndex(path, readOnly: true);
        Assert.Single(reopened.Search("handshake"));
    }

    [Fact]
    public void ItNamesTheDocumentsThatHoldNoTextAtAll()
    {
        using var index = new SearchIndex(Path_("index.db"));
        index.AddDocument(@"C:\Manuals\good.pdf", "good", "hash-a", Pages("the HP-IB handshake lines"));
        index.AddDocument(@"C:\Manuals\scan.pdf", "scan", "hash-b", Pages("", "", ""));

        // An image-only scan nobody has OCR'd is in the library and invisible to search, and a
        // caller told only "nothing matched" will report absence for something that is present.
        Assert.Equal(1, index.Statistics().DocumentsWithoutText);
        Assert.Equal(@"C:\Manuals\scan.pdf", Assert.Single(index.DocumentsWithoutText()));
    }

    [Fact]
    public void AModelHintPromotesThatInstrumentsManual()
    {
        using var index = new SearchIndex(Path_("index.db"));

        // The prose page mentions the words far more often, so bm25 puts it first. This is the
        // real shape of the problem: the page that defines a command loses to a page that talks
        // about it at length.
        index.AddDocument(
            @"C:\Manuals\TDS3014B Programming.pdf", "TDS3014B Programming", "hash-a",
            Pages("waveform source waveform source waveform source waveform source waveform source"));

        index.AddDocument(
            @"C:\Manuals\54845A Programmer.pdf", "54845A Programmer", "hash-b",
            Pages("WAVeform SOURce"));

        // bm25 alone puts the prose page first. Asked without a bias, because the default one
        // promotes the 54845A page on its own here — its text is the two words on a line of their
        // own, which is exactly the shape RankingBias.Label exists to recognise. Worth pinning both
        // behaviours separately rather than letting one hide the other.
        Assert.Equal(
            "TDS3014B Programming",
            index.Search("waveform source", ranking: new RankingBias())[0].Title);

        Assert.Equal(
            "54845A Programmer",
            index.Search("waveform source", ranking: new RankingBias(), model: "54845A")[0].Title);

        Assert.Equal("54845A Programmer", index.Search("waveform source")[0].Title);
    }

    [Fact]
    public void ATermOnALineOfItsOwnBeatsAPageThatMerelyDiscussesIt()
    {
        using var index = new SearchIndex(Path_("index.db"));

        // The whole ranking problem in four lines. One page defines the command in a syntax
        // diagram, where the word appears once, alone. The other talks about attenuation at
        // length, which is what bm25 rewards.
        index.AddDocument(
            @"C:\Manuals\Prose.pdf", "Prose", "hash-a",
            Pages("attenuation is set by the attenuator whose attenuation range covers attenuation "
                + "in 10 dB steps, and attenuation accuracy depends on the attenuator"));

        index.AddDocument(
            @"C:\Manuals\Reference.pdf", "Reference", "hash-b",
            Pages("CHANnel Commands\nATTenuation\nrange 0 to 60 dB\nsee also PROBe"));

        Assert.Equal("Prose", index.Search("attenuation", ranking: new RankingBias())[0].Title);
        Assert.Equal("Reference", index.Search("attenuation")[0].Title);
    }

    [Fact]
    public void ALabelBoostDoesNotReachAPageThatOnlyMentionsTheWord()
    {
        using var index = new SearchIndex(Path_("index.db"));

        // Nothing here stands alone on a line, so the boost must not fire and the ordering must be
        // bm25's. A bias that applied to everything would be a no-op dressed up as an improvement.
        index.AddDocument(
            @"C:\Manuals\A.pdf", "A", "hash-a",
            Pages("the attenuator is described here and the attenuator is discussed at length"));

        index.AddDocument(
            @"C:\Manuals\B.pdf", "B", "hash-b",
            Pages("one passing mention of the attenuator in a long sentence about something else"));

        var withBias = index.Search("attenuator").Select(h => h.Title).ToArray();
        var without = index.Search("attenuator", ranking: new RankingBias()).Select(h => h.Title).ToArray();

        Assert.Equal(without, withBias);
    }

    [Fact]
    public void AModelHintBiasesAndNeverFilters()
    {
        using var index = new SearchIndex(Path_("index.db"));
        index.AddDocument(
            @"C:\Manuals\8566B Service.pdf", "8566B Service", "hash-a",
            Pages("the YIG oscillator bias network is adjusted at A3R7"));

        // Nothing here mentions the 54845A at all. A filter would return nothing and hide the
        // answer; the answer to a question about one instrument is often printed in another
        // instrument's manual, and that is the whole reason this is a bias.
        var hits = index.Search("YIG oscillator bias", model: "54845A");

        Assert.Equal("8566B Service", Assert.Single(hits).Title);
    }
}
