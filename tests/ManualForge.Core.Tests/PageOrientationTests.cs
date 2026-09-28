using ManualForge.Core.Geometry;
using ManualForge.Core.Ocr;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// The page-orientation classifier turns a page before detection, and on these scans it is wrong
/// about a third of the time. When it claims a rotation the page is read again as it stands, and
/// the reading the recogniser was surer of wins.
///
/// <para>
/// The measure has to favour the right reading in both directions: an upright page the classifier
/// turned, and a sideways page it rightly turned. What a wrong turn produces is known - vertical
/// stacks of one character, read long and unsure - so that is what these tests are built from.
/// </para>
/// </summary>
public sealed class PageOrientationTests
{
    private static RecognisedLine Line(params (string Text, double Confidence)[] words) =>
        new(
            string.Join(' ', words.Select(w => w.Text)),
            RectD.FromEdges(0, 0, 100, 20),
            words.Length == 0 ? 0 : words.Average(w => w.Confidence),
            words.Select((w, i) => new RecognisedWord(w.Text, RectD.FromEdges(i * 10, 0, i * 10 + 8, 20), w.Confidence)).ToList());

    [Fact]
    public void VerifyingIsTheDefault()
    {
        // The documented default, pinned: a flag wired backwards once went unnoticed for two
        // whole ingests because nothing asserted which way round it was.
        Assert.True(new OcrEngineOptions().VerifyPageOrientation);
    }

    [Fact]
    public void OnlyConfidentLettersAndDigitsCount()
    {
        var lines = new[]
        {
            Line(("0698-3155", 0.95), ("R-F", 0.9)),   // 8 + 2
            Line(("4.64K", 0.79)),                      // below the bar
        };

        Assert.Equal(10, PaddleOcrEngine.ConfidentCharacters(lines));
    }

    [Fact]
    public void AStackedMisreadingLosesToTheRowsItCameFrom()
    {
        // What page 60 of the table book looked like turned 90 degrees: the designator column's
        // five A2 prefixes read as one vertical word, long and unsure.
        var turned = new[] { Line(("AAAAA", 0.63), ("NNNNN", 0.7), ("55555", 0.99)) };
        var asItStands = new[]
        {
            Line(("A2", 0.97), ("R45", 0.95), ("1", 0.9)),
            Line(("A2", 0.97), ("R46", 0.96), ("6", 0.91)),
        };

        Assert.True(
            PaddleOcrEngine.ConfidentCharacters(asItStands) > PaddleOcrEngine.ConfidentCharacters(turned));
    }

    [Fact]
    public void AnEmptyReadingCountsForNothing()
    {
        Assert.Equal(0, PaddleOcrEngine.ConfidentCharacters([]));
        Assert.Equal(0, PaddleOcrEngine.ConfidentCharacters([Line()]));
    }
}
