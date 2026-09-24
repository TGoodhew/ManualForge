using ManualForge.Core.Indexing;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// `AND`, `OR` and `NOT` are FTS5 operators and they are also ordinary words — far more ordinary
/// than usual here, because instrument manuals are lettered in capitals and the repair reads those
/// capitals back off the page. Somebody who pastes a line out of a manual must get results.
/// </summary>
public sealed class SearchOperatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "manualforge-operators-" + Guid.NewGuid().ToString("N"));

    public SearchOperatorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private SearchIndex GivenAPage(params string[] pages)
    {
        var index = new SearchIndex(Path.Combine(_directory, "index.db"));
        index.AddDocument(
            @"C:\Manuals\E4418B CLIP V02.pdf", "E4418B CLIP V02", "hash-a",
            pages.Select(Dehyphenator.Prepare).ToArray());

        return index;
    }

    [Theory]
    [InlineData("FIT BOTTOM EDGE UNDER LUGS AND")]
    [InlineData("AND ROTATE TOP EDGE OF DISPLAY")]
    [InlineData("REMOVE THE SCREWS OR")]
    [InlineData("NOT")]
    public void APhraseThatMerelyStartsOrEndsWithAnOperatorIsNotAnExpression(string query)
    {
        Assert.False(SearchQuery.LooksLikeExpression(query));
    }

    [Theory]
    [InlineData("attenuator AND protection")]
    [InlineData("attenuator OR protection")]
    [InlineData("attenuator NOT protection")]
    [InlineData("NEAR(attenuator protection, 5)")]
    public void AnOperatorBetweenTwoTermsStillMeansWhatItSays(string query)
    {
        Assert.True(SearchQuery.LooksLikeExpression(query));
    }

    [Fact]
    public void APhraseEndingInAndReturnsResultsRatherThanAnError()
    {
        using var index = GivenAPage(
            "(vi) FIT BOTTOM EDGE UNDER LUGS AND ROTATE TOP EDGE OF DISPLAY DOWN INTO STEEL SCREEN");

        // Before this was fixed, the trailing AND made FTS5 read the whole line as an expression
        // with nothing to its right, and the search failed with 'fts5: syntax error near ""'.
        var hit = Assert.Single(index.Search("FIT BOTTOM EDGE UNDER LUGS AND"));

        Assert.Equal(1, hit.PageNumber);
    }

    [Fact]
    public void AnExpressionFts5CannotParseIsReadAsWordsInstead()
    {
        using var index = GivenAPage("the attenuator AND the protection circuit are on the same board");

        var notes = new List<string>();

        // A real operator, but with a parenthesis FTS5 will not accept. Rather than fail, the
        // query is re-read as ordinary words - and says so, because a search that silently
        // changes the question is worse than one that explains itself.
        var hits = index.Search("attenuator AND (protection", note: notes.Add);

        Assert.Single(hits);
        var note = Assert.Single(notes);
        Assert.Contains("ordinary words", note, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsSaidWhenTheQueryWasFine()
    {
        using var index = GivenAPage("the attenuator protection circuit");

        var notes = new List<string>();
        Assert.Single(index.Search("attenuator protection", note: notes.Add));

        Assert.Empty(notes);
    }
}
