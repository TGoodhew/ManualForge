using ManualForge.Core.Indexing;

namespace ManualForge.Core.Tests;

/// <summary>
/// The recovered-text bias is scoped to a single bare word, because that is the only query shape it
/// helps: measured across the 33 ground-truth strings it takes <c>ATTenuation</c> from 14 to 1 and
/// <c>PROTection</c> from 11 to 2, and applied to command syntax as well it drags
/// <c>:CHANnel&lt;N&gt;:SCALe</c> from 6 to 15. So the classifier is the whole change, and getting it
/// wrong silently re-applies the bias to the queries it harms.
/// </summary>
public sealed class BareTermTests
{
    [Theory]
    [InlineData("ATTenuation")]
    [InlineData("PROTection")]
    [InlineData("SKEW")]
    [InlineData("XINCrement?")]
    [InlineData("klystron")]
    public void ASingleWordIsABareTerm(string query)
    {
        Assert.True(SearchQuery.IsBareTerm(query));
    }

    [Theory]
    [InlineData(":CHANnel<N>:SCALe")]                       // notation
    [InlineData(":WAVeform:FORMat {ASCii|BYTE|WORD|LONG}")] // notation and spaces
    [InlineData("DC50|DCFifty")]                            // alternation
    [InlineData("attenuator hold-off adjustment")]          // a phrase
    [InlineData("\"quoted phrase\"")]                       // an explicit phrase
    [InlineData("atten*")]                                  // a prefix search
    [InlineData("")]
    [InlineData("   ")]
    public void AnythingElseIsNot(string query)
    {
        Assert.False(SearchQuery.IsBareTerm(query));
    }

    [Fact]
    public void NullIsNotABareTerm()
    {
        Assert.False(SearchQuery.IsBareTerm(null));
    }

    [Fact]
    public void SurroundingSpaceDoesNotMakeAWordAPhrase()
    {
        Assert.True(SearchQuery.IsBareTerm("  ATTenuation  "));
    }

    /// <summary>
    /// The discriminating case for the scoping itself: the bias must reach a bare term and must not
    /// reach command syntax. Asserting only the first would pass against a bias applied to
    /// everything, which is the version measured and rejected.
    /// </summary>
    [Fact]
    public void TheBiasReachesABareTermAndNotCommandSyntax()
    {
        Assert.True(SearchQuery.IsBareTerm("ATTenuation"));
        Assert.False(SearchQuery.IsBareTerm(":CHANnel<N>:ATTenuation"));
    }
}
