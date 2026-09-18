using ManualForge.Core.Indexing;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// Reading command syntax as the notation it is, rather than as a phrase.
///
/// <para>
/// The case that drove this: every one of the ground-truth strings for the 54845A Programmer's
/// Guide is command syntax copied off a syntax diagram, and as phrases none of them can ever match.
/// A syntax diagram draws <c>:CHANnel</c>, <c>INPut</c> and <c>LFR1</c> in three separate boxes,
/// so whatever reads that page — extractor or recogniser — reports them in whatever order the
/// boxes were drawn, never adjacent.
/// </para>
/// </summary>
public sealed class SearchQueryNotationTests
{
    [Fact]
    public void OrdinaryTypingBecomesAnAndOfQuotedTerms()
    {
        // The behaviour everything else depends on: a hyphen is part of a part number, not a
        // separator, and two words mean both words.
        Assert.Equal("\"HP-IB\" AND \"handshake\"", SearchQuery.Prepare("HP-IB handshake"));
        Assert.Equal("\"08340-60019\"", SearchQuery.Prepare("08340-60019"));
        Assert.Equal("\"frequency\"", SearchQuery.Prepare("frequency"));
    }

    [Fact]
    public void AnFts5ExpressionIsPassedThrough()
    {
        Assert.Equal("yig OR sweeper", SearchQuery.Prepare("yig OR sweeper"));
        Assert.Equal("wave*", SearchQuery.Prepare("wave*"));
    }

    [Fact]
    public void AnUppercaseWordContainingAnOperatorIsNotAnOperator()
    {
        // WORD contains OR, COMMAND contains AND, NOTE contains NOT. Treating those as operators
        // handed the query to FTS5 raw, where it is a syntax error.
        Assert.False(SearchQuery.LooksLikeExpression("WORD"));
        Assert.False(SearchQuery.LooksLikeExpression("COMMAND REFERENCE"));
        Assert.False(SearchQuery.LooksLikeExpression("NOTE"));
        Assert.True(SearchQuery.LooksLikeExpression("yig OR sweeper"));
        Assert.True(SearchQuery.LooksLikeExpression("NEAR(yig sweeper)"));
    }

    [Fact]
    public void ColonsSeparateLevelsThatMustAllAppear()
    {
        Assert.Equal("\"TIMebase\" AND \"SCALe\"", SearchQuery.Prepare(":TIMebase:SCALe"));
        Assert.Equal("\"WAVeform\" AND \"XINCrement?\"", SearchQuery.Prepare(":WAVeform:XINCrement?"));
    }

    [Fact]
    public void APlaceholderStandsForNothingAndIsDropped()
    {
        Assert.Equal("\"CHANnel\" AND \"INPut\"", SearchQuery.Prepare(":CHANnel<N>:INPut"));
        Assert.Equal("\"CHANnel\" AND \"RANGe\"", SearchQuery.Prepare(":CHANnel<N>:RANGe"));
    }

    [Fact]
    public void BracedAlternativesAreAlternatives()
    {
        // Any one will do. AND-ing them would mean a page that shows three of the four arguments
        // fails to match, which is exactly what a recogniser that missed one produces.
        Assert.Equal(
            "\"TRIGger\" AND \"MODE\" AND (\"EDGE\" OR \"GLITch\" OR \"ADVanced\")",
            SearchQuery.Prepare(":TRIGger:MODE {EDGE|GLITch|ADVanced}"));

        Assert.Equal(
            "\"WAVeform\" AND \"FORMat\" AND (\"ASCii\" OR \"BYTE\" OR \"WORD\" OR \"LONG\")",
            SearchQuery.Prepare(":WAVeform:FORMat {ASCii|BYTE|WORD|LONG}"));
    }

    [Fact]
    public void ABareVerticalBarIsAlsoAnAlternative()
    {
        // As written on the page: DC50|DCFifty, OFF|0.
        Assert.Equal("(\"DC50\" OR \"DCFifty\")", SearchQuery.Prepare("DC50|DCFifty"));
    }

    [Fact]
    public void ATermThatIsOnlyAPlaceholderDisappears()
    {
        Assert.Null(SearchQuery.Prepare("<N>"));
        Assert.Equal("\"SCALe\"", SearchQuery.Prepare("<N> SCALe"));
    }

    [Fact]
    public void TermsAreTheWordsAHitCanBeAttributedBy()
    {
        Assert.Equal(["CHANnel", "INPut"], SearchQuery.Terms(":CHANnel<N>:INPut"));
        Assert.Equal(
            ["WAVeform", "BYTeorder", "MSBFirst", "LSBFirst"],
            SearchQuery.Terms(":WAVeform:BYTeorder {MSBFirst|LSBFirst}"));
    }

    [Fact]
    public void CaseIsCarriedThroughUntouched()
    {
        // SCPI documents its abbreviations by capitalisation: BYTeorder means BYT is accepted.
        // Anything that normalised case here would destroy information that cannot be recovered.
        Assert.Contains("BYTeorder", SearchQuery.Prepare(":WAVeform:BYTeorder"), StringComparison.Ordinal);
        Assert.Contains("BYTeorder", SearchQuery.Terms(":WAVeform:BYTeorder"));
    }
}
