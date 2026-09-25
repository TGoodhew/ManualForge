using ManualForge.Core.Benchmarking;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// Where a word's baseline sits relative to its ink, which is the difference between the box a
/// recogniser returns and the line the text has to be written on.
/// </summary>
public sealed class BaselineProbeTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "manualforge-baseline-" + Guid.NewGuid().ToString("N"));

    public BaselineProbeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static BaselineSample Sample(double fraction, bool descender = false, double height = 20) =>
        new("a.pdf", 1, "word", fraction, descender, height);

    [Fact]
    public void ADescenderPutsInkBelowTheBaselineAndAnAscenderDoesNot()
    {
        // The whole measurement in one page. "happy" drops below its baseline and "them" does not,
        // so the first must measure more negative than the second - and by roughly a fifth of the
        // ink height, which is what a descender is.
        var path = TestPdf.TypesetOnly(
            Path.Combine(_directory, "words.pdf"),
            "happy puppy pygmy gypsy\nthem three inert on\nhappy puppy pygmy gypsy\nthem three inert on");

        var samples = new BaselineProbe().Measure(path, 1);

        var descenders = samples.Where(s => s.HasDescender).ToArray();
        var others = samples.Where(s => !s.HasDescender).ToArray();

        Assert.NotEmpty(descenders);
        Assert.NotEmpty(others);

        var withDescender = descenders.Average(s => s.Fraction);
        var without = others.Average(s => s.Fraction);

        Assert.True(
            withDescender < without - 0.1,
            $"a descender should sit well below the baseline: {withDescender:F3} against {without:F3}");

        // And a word without one sits on its own ink, give or take the antialiased edge of a
        // rendered glyph. This is the check that says the measurement itself is sound: if it fails,
        // nothing else the probe reports means anything.
        Assert.InRange(without, -0.12, 0.02);
    }

    [Fact]
    public void ThePageIsSkippedRatherThanGuessedAtWhenItIsTurned()
    {
        // Rotated pages are not handled, deliberately, and returning nothing is the honest form of
        // that. Silently measuring them against the wrong axis would be the other kind.
        var path = TestPdf.Scanned(Path.Combine(_directory, "turned.pdf"), pages: 1, rotation: 90);

        Assert.Empty(new BaselineProbe().Measure(path, 1));
    }

    [Fact]
    public void TheRecommendationIsTheConstantThatCostsLeast()
    {
        // Three quarters of words have no descender and sit at zero; the rest sit at -0.2. One
        // constant has to serve both, and the cheapest is not the average of the two.
        var samples = Enumerable.Repeat(Sample(0.0), 75)
            .Concat(Enumerable.Repeat(Sample(-0.2, descender: true), 25))
            .ToArray();

        var recommendation = BaselineProbe.Recommend(samples, pointsPerPixel: 72.0 / 300);

        Assert.Equal(0.0, recommendation.Fraction, 2);
        Assert.True(recommendation.MeanErrorPt <= recommendation.MeanErrorAtZeroPt);
    }

    [Fact]
    public void AConsistentOffsetIsFoundExactly()
    {
        var samples = Enumerable.Repeat(Sample(-0.05), 50).ToArray();

        var recommendation = BaselineProbe.Recommend(samples, pointsPerPixel: 72.0 / 300);

        Assert.Equal(-0.05, recommendation.Fraction, 2);
        Assert.Equal(0, recommendation.MeanErrorPt, 3);
    }

    [Fact]
    public void NothingMeasuredIsNotAnOpinion()
    {
        Assert.Equal(0, BaselineProbe.Recommend([], 0.24).Samples);
    }
}
