using ManualForge.Core.Auditing;
using ManualForge.Core.Benchmarking;
using Xunit;

// Both namespaces have a PageKind and they mean different things: one is where a flagged page's
// missing content lives, the other is what a page mostly is. This file is about the second.
using PageKind = ManualForge.Core.Benchmarking.PageKind;

namespace ManualForge.Core.Tests;

/// <summary>
/// Choosing pages whose own text layer can stand as an answer key.
///
/// <para>
/// Every test here is about rejecting something, because the cost of a bad page is asymmetric: a
/// page wrongly rejected costs nothing at all — there are thousands more — while a page wrongly
/// accepted quietly corrupts every number the benchmark goes on to produce, and looks like the
/// recogniser's fault.
/// </para>
/// </summary>
public sealed class PublisherTruthTests
{
    private static PageAudit Page(
        int glyphs = 2000,
        int decoded = 1500,
        double imageCoverage = 0,
        int pathOps = 5,
        bool type3 = false,
        bool noToUnicode = false,
        PageVerdict verdict = PageVerdict.Fine)
        => new(
            PageNumber: 1,
            GlyphsDrawn: glyphs,
            CharactersDecoded: decoded,
            PathPaintOperations: pathOps,
            TextShowOperations: 200,
            ImageCount: imageCoverage > 0 ? 1 : 0,
            ImageCoverage: imageCoverage,
            HasType3Font: type3,
            HasFontWithoutToUnicode: noToUnicode,
            Ink: InkAnalysis.NotRendered,
            OutlineHeadingMissing: false,
            Verdict: verdict,
            Signals: []);

    [Fact]
    public void APageOfPlainTypeQualifies()
    {
        Assert.True(new PublisherTruth().IsPublisherType(Page()));
    }

    [Fact]
    public void AScannedPageDoesNot()
    {
        // The criterion the whole idea rests on. A scan carrying 2000s-era OCR also has a text
        // layer, and using it as truth would score this recogniser against another engine's
        // mistakes while calling the result accuracy.
        Assert.False(new PublisherTruth().IsPublisherType(Page(imageCoverage: 0.9)));
    }

    [Fact]
    public void NeitherDoesAPageCarryingAVectorFigure()
    {
        // Its text decodes perfectly; the trouble is the figure's labels, which are drawn rather
        // than set. The recogniser will read them off the render and be marked wrong for text the
        // answer key never contained.
        Assert.False(new PublisherTruth().IsPublisherType(Page(pathOps: 400)));
    }

    [Fact]
    public void AFontWithoutToUnicodeIsNotDisqualifying()
    {
        // Tried as a criterion first, and it excluded the entire library: most born-digital pages
        // here carry a subset font with no /ToUnicode and extract perfectly through its standard
        // encoding. Kept as a test so nobody reinstates it.
        Assert.True(new PublisherTruth().IsPublisherType(Page(noToUnicode: true)));
    }

    [Fact]
    public void TextConvertedToOutlinesIsDisqualifying()
    {
        Assert.False(new PublisherTruth().IsPublisherType(Page(glyphs: 2000, decoded: 200)));
        Assert.False(new PublisherTruth().IsPublisherType(Page(type3: true)));
    }

    [Fact]
    public void APageTheAuditFlaggedIsNotAnAnswerKey()
    {
        Assert.False(new PublisherTruth().IsPublisherType(Page(verdict: PageVerdict.UnderExtracted)));
    }

    [Fact]
    public void ProseThatReadsProperlyIsAccepted()
    {
        var prose = string.Join(' ', Enumerable.Repeat(
            "The following is a basic functional description of the oscilloscope and its vertical "
            + "attenuators, which are set by the front panel controls.", 6));

        Assert.True(new PublisherTruth().ReadsAsWritten(prose));
    }

    [Fact]
    public void LetterSpacedExtractionIsRejected()
    {
        // A real extraction from this library: a multi-column page with tight tracking, where the
        // words come back in pieces. It is complete, it is the publisher's own text, and it is
        // useless as an answer key.
        var mangled = string.Join(' ', Enumerable.Repeat(
            "Co nt inu e d fro m fro nt m a tte r Sa f e ty C o nsid e ra t ion s Wa r r ant y", 8));

        Assert.False(new PublisherTruth().ReadsAsWritten(mangled));
    }

    [Fact]
    public void APageWithHardlyAnyTextIsRejected()
    {
        Assert.False(new PublisherTruth().ReadsAsWritten("Figure 3-2. The status screen."));
    }

    [Theory]
    [InlineData("A parts table", "08340-60019 1 A3 assembly 5 dB\n1250-1487 2 connector 50 ohm\n"
        + "08340-60020 1 A4 board 12 V\n1250-1420 4 washer 3 mm\n0180-0197 2 capacitor 10 uF", PageKind.Table)]
    [InlineData("Running text", "The instrument responds to the command by setting the attenuator "
        + "to the value given, and the front panel indicator shows the new setting immediately.", PageKind.Prose)]
    public void KindIsDecidedOnShape(string why, string text, PageKind expected)
    {
        Assert.Equal(expected, PublisherTruth.KindOf(text));
        Assert.NotEmpty(why);
    }
}
