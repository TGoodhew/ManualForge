using ManualForge.Core.Benchmarking;

namespace ManualForge.Core.Tests;

/// <summary>
/// The arithmetic every benchmark number rests on. If this is wrong, every conclusion drawn from
/// the harness is wrong in a way that looks authoritative.
/// </summary>
public class ErrorRateTests
{
    [Fact]
    public void PerfectRecognitionIsZeroError()
    {
        var accuracy = ErrorRate.Measure("the quick brown fox", "the quick brown fox");

        Assert.Equal(0, accuracy.CharacterErrors);
        Assert.Equal(0, accuracy.WordErrors);
        Assert.Equal(0, accuracy.CharacterErrorRate);
        Assert.Equal(0, accuracy.WordErrorRate);
    }

    [Fact]
    public void OneSubstitutedCharacterIsOneError()
    {
        // The classic OCR mistake in this material: a part number where 1 becomes l.
        var accuracy = ErrorRate.Measure("1N21", "1N2l");

        Assert.Equal(1, accuracy.CharacterErrors);
        Assert.Equal(4, accuracy.TruthCharacters);
        Assert.Equal(0.25, accuracy.CharacterErrorRate, 6);

        // And one whole word wrong, because a part number is one token.
        Assert.Equal(1, accuracy.WordErrors);
    }

    [Fact]
    public void AnInsertionDoesNotWreckEverythingAfterIt()
    {
        // The reason this is edit distance rather than a positional comparison. A speck read as a
        // comma near the start would make a naive measure call the whole page garbage.
        var truth = "set the frequency to ten megahertz and observe the output";
        var accuracy = ErrorRate.Measure(truth, "set the, frequency to ten megahertz and observe the output");

        Assert.Equal(1, accuracy.CharacterErrors);
        Assert.True(accuracy.CharacterErrorRate < 0.02,
            $"one inserted character should be a rounding error, got {accuracy.CharacterErrorRate:P1}");
    }

    [Fact]
    public void LineBreaksAndCaseAreNotMistakes()
    {
        // Where an engine wraps is a property of the page, not of the text, and an engine should
        // not be marked down for it.
        var accuracy = ErrorRate.Measure(
            "The instrument is shipped\nwith the selector set.",
            "THE INSTRUMENT IS SHIPPED WITH THE SELECTOR SET.");

        Assert.Equal(0, accuracy.CharacterErrors);
        Assert.Equal(0, accuracy.WordErrors);
    }

    [Fact]
    public void PunctuationIsNotNormalisedAway()
    {
        // Deliberately strict. A manual that renders a tolerance as +0.5 instead of ±0.5 has made
        // exactly the kind of mistake this is meant to catch, and forgiving it would flatter the
        // result.
        var accuracy = ErrorRate.Measure("±0.5 dB", "+0.5 dB");

        Assert.Equal(1, accuracy.CharacterErrors);
    }

    [Fact]
    public void RecognisingNothingIsTotalFailureRatherThanSuccess()
    {
        var accuracy = ErrorRate.Measure("the calibration procedure", "");

        Assert.Equal(1.0, accuracy.CharacterErrorRate, 6);
        Assert.Equal(1.0, accuracy.WordErrorRate, 6);
    }

    [Fact]
    public void InventingTextCanScoreWorseThanFindingNone()
    {
        // A badly deskewed page produces confetti, and a rate that capped at 1.0 would make that
        // look no worse than a blank page. It is worse.
        var accuracy = ErrorRate.Measure("ok", "ok l;'~4 |ll| ,,, ~~~ oo0O");

        Assert.True(accuracy.CharacterErrorRate > 1.0,
            $"hallucinated text should exceed 1.0, got {accuracy.CharacterErrorRate:F2}");
    }

    [Fact]
    public void AnEmptyTruthPageScoresZeroRatherThanDividingByZero()
    {
        var accuracy = ErrorRate.Measure("", "anything at all");

        Assert.Equal(0, accuracy.CharacterErrorRate);
        Assert.Equal(0, accuracy.WordErrorRate);
    }

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "abc", 0)]
    [InlineData("kitten", "sitting", 3)]     // the textbook case
    [InlineData("flaw", "lawn", 2)]
    public void EditDistanceMatchesKnownValues(string a, string b, int expected)
        => Assert.Equal(expected, ErrorRate.Distance(a.AsSpan(), b.AsSpan()));

    [Fact]
    public void WordErrorsCountWholeWordsNotCharacters()
    {
        // Two characters wrong, but they are in two different words, so two words are wrong.
        var accuracy = ErrorRate.Measure("the quick brown fox", "the qu1ck brown f0x");

        Assert.Equal(2, accuracy.CharacterErrors);
        Assert.Equal(2, accuracy.WordErrors);
        Assert.Equal(4, accuracy.TruthWords);
        Assert.Equal(0.5, accuracy.WordErrorRate, 6);
    }

    [Fact]
    public void AccuraciesAddUpAcrossPages()
    {
        // A run's overall rate has to be errors over characters across the whole set, not the mean
        // of per-page rates - otherwise a two-word page counts as much as a dense schematic.
        var small = ErrorRate.Measure("ab", "ax");           // 1 of 2
        var large = ErrorRate.Measure(new string('a', 998), new string('a', 998));

        var total = small + large;

        Assert.Equal(1, total.CharacterErrors);
        Assert.Equal(1000, total.TruthCharacters);
        Assert.Equal(0.001, total.CharacterErrorRate, 6);
    }

    [Fact]
    public void NormalisationCollapsesWhitespaceWithoutJoiningWords()
    {
        Assert.Equal("a b c", ErrorRate.Normalise("  A\n\tB   c  "));
        Assert.Equal(string.Empty, ErrorRate.Normalise("   \n  "));
        Assert.Equal(string.Empty, ErrorRate.Normalise(null));
    }
}
