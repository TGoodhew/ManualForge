using ManualForge.Core.Indexing;

namespace ManualForge.Core.Tests;

/// <summary>
/// A hyphen at a line end is ambiguous, and the two readings need opposite treatment.
///
/// <c>fre-</c> / <c>quency</c> is one word split for typesetting: leave it and a search for
/// "frequency" finds nothing. <c>frequency-</c> / <c>controlling</c> is a real compound: fuse it and
/// a search for "frequency-controlling" finds nothing. They look identical on the page.
///
/// So the rule these tests hold to is not "guess correctly" — nothing can — but "never lose a
/// result to a guess". The likelier reading goes in the text a person reads; the other goes in a
/// column that is indexed and never shown.
/// </summary>
public class DehyphenationTests
{
    private static IndexedPageText Prepare(params string[] lines) =>
        Dehyphenator.Prepare(string.Join('\n', lines));

    [Fact]
    public void AWordSplitForTypesettingIsPutBackTogether()
    {
        var page = Prepare("the fre-", "quency of the source");

        Assert.Contains("frequency", page.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("fre-", page.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealCompoundKeepsItsHyphen()
    {
        // Both halves stand as words, so this reads as a compound.
        var page = Prepare("the frequency-", "controlling element");

        Assert.Contains("frequency-controlling", page.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOtherReadingIsAlwaysIndexedToo()
    {
        // The whole point. Whichever way the guess went, the alternative is still findable.
        var split = Prepare("the fre-", "quency of the source");
        Assert.Contains("fre-quency", split.Alternates, StringComparison.Ordinal);

        var compound = Prepare("the frequency-", "controlling element");
        Assert.Contains("frequencycontrolling", compound.Alternates, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRestOfTheContinuingLineIsKept()
    {
        var page = Prepare("set the fre-", "quency to 10 MHz and observe");

        Assert.Contains("frequency to 10 MHz and observe", page.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoBreaksInARowBothResolve()
    {
        var page = Prepare("the oscil-", "lator is fre-", "quency locked");

        Assert.Contains("oscillator", page.Text, StringComparison.Ordinal);
        Assert.Contains("frequency", page.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACapitalAfterTheBreakIsNotAContinuation()
    {
        // "Hewlett-" / "Packard" is a name, and a new sentence after a dash is not a broken word.
        var page = Prepare("made by Hewlett-", "Packard of Palo Alto");

        Assert.Contains("Hewlett-", page.Text, StringComparison.Ordinal);
        Assert.Contains("Packard", page.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("HewlettPackard", page.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ADashUsedAsPunctuationIsLeftAlone()
    {
        var page = Prepare("the output --", "measured at the load");

        Assert.Contains("--", page.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("measuredat", page.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AHyphenAtTheEndOfThePageHasNothingToJoinTo()
    {
        var page = Prepare("continued on the next page fre-");

        Assert.Contains("fre-", page.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void APartNumberEndingInAHyphenIsNotMangled()
    {
        // Model and part numbers end in hyphens constantly in this material, and the digits either
        // side are not letters, so there is no word to rejoin.
        var page = Prepare("order part 08340-", "90243 from the factory");

        Assert.Contains("90243", page.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("0834090243", page.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n ")]
    public void NothingInMeansNothingOut(string? input)
    {
        var page = Dehyphenator.Prepare(input);

        Assert.True(page.IsEmpty);
        Assert.Equal(string.Empty, page.Alternates);
    }

    [Fact]
    public void TextWithNoBreaksIsReturnedUnchangedApartFromTrimming()
    {
        var page = Prepare("The instrument is shipped with the line", "voltage selector set correctly.");

        Assert.Equal(
            "The instrument is shipped with the line\nvoltage selector set correctly.",
            page.Text);
        Assert.Equal(string.Empty, page.Alternates);
    }

    [Fact]
    public void BothReadingsAreFindableThroughTheIndex()
    {
        // The end-to-end claim: a guess costs at most an odd-looking snippet, never a missed hit.
        var directory = Path.Combine(Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            using var index = new SearchIndex(Path.Combine(directory, "index.db"));
            index.AddDocument(@"C:\Manuals\a.pdf", "A", "hash-a",
            [
                Prepare("the fre-", "quency of the source"),
                Prepare("the frequency-", "controlling element"),
            ]);

            // The split page, rejoined - and the compound page, whose first token is also
            // "frequency", so both legitimately match.
            Assert.Equal(2, index.Search("frequency").Count);
            Assert.NotEmpty(index.Search("\"frequency controlling\"")); // the compound, as tokens
            Assert.Single(index.Search("frequencycontrolling"));       // the compound, fused
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}
