using ManualForge.Core.Classification;

namespace ManualForge.Core.Tests;

public class ClassifierTests
{
    private static DocumentClassification ClassifyWords(
        IEnumerable<IEnumerable<string>> pages, ClassifierOptions? options = null, int glyphsPerPage = 0)
    {
        var classifier = new DocumentClassifier(options);
        var metrics = pages
            .Select((words, index) => TextMetricsCalculator.Measure(index + 1, words, glyphsPerPage))
            .ToList();
        return classifier.Summarise("test.pdf", metrics.Count, metrics);
    }

    /// <summary>
    /// What PdfPig returns for a page of a 1990s HP manual typeset with cdsdvips: a Type 1 font
    /// with a custom encoding and no /ToUnicode, so the glyphs are all there and almost none of
    /// them decode. Taken from page 20 of `8714 Service Guide.pdf`.
    /// </summary>
    private static string[] UndecodableGlyphs() => Dense(
        """&" '(") *++, 0707$ $07' !#4(5-<  "  ' +!+37 ' ) *++ , !"#$%&"()""".Split(' '), times: 3);

    [Fact]
    public void GlyphsWithoutCharactersAreATextLayerWeCannotRead()
    {
        // The expensive mistake this prevents: reading a font we cannot decode as no text at all,
        // then adding a second text layer on top of the first. Three files in the real library
        // were damaged that way. The affected pages drew 355 to 3,276 glyphs each while decoding
        // between 0 and 68 characters.
        var result = ClassifyWords([UndecodableGlyphs(), UndecodableGlyphs()], glyphsPerPage: 1200);

        Assert.Equal(TextClass.UnreadableTextLayer, result.Class);
        Assert.Contains("cannot make sense of", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void NoGlyphsAndNoCharactersIsGenuinelyImageOnly()
    {
        // The same undecodable text with no glyphs behind it is what a scanned page looks like:
        // nothing drawn, nothing decoded. This one really does want OCR.
        var result = ClassifyWords([UndecodableGlyphs(), UndecodableGlyphs()], glyphsPerPage: 0);

        Assert.Equal(TextClass.ImageOnly, result.Class);
    }

    [Fact]
    public void AScannedPageWithAStampedPageNumberIsStillImageOnly()
    {
        // A few stray glyphs - a stamped folio, a signature block - must not be mistaken for a
        // text layer, or every scanned manual would be refused.
        var result = ClassifyWords([["7"], ["8"]], glyphsPerPage: 3);

        Assert.Equal(TextClass.ImageOnly, result.Class);
    }

    [Fact]
    public void RealTextIsUnaffectedByHowManyGlyphsDrewIt()
    {
        // Glyph count only ever decides between "no text" and "text we cannot read". A page whose
        // text decodes is judged on the text.
        Assert.Equal(TextClass.GoodText, ClassifyWords([Prose(), Prose()], glyphsPerPage: 5000).Class);
    }

    /// <summary>
    /// Repeats a sample so the page carries a realistic amount of text. A real manual page has
    /// hundreds of characters on it, and the ImageOnly threshold is 100 per page, so a handful of
    /// tokens would be classified as having no text layer at all — correctly, but uninterestingly.
    /// </summary>
    private static string[] Dense(string[] sample, int times = 4) =>
        Enumerable.Repeat(sample, times).SelectMany(x => x).ToArray();

    private static string[] Prose() => Dense(
        ("The instrument is shipped with the line voltage selector set for operation on the voltage " +
         "marked on the rear panel. Check that the setting is correct before you connect the power " +
         "cable to the instrument and turn the power switch on.").Split(' '));

    /// <summary>
    /// What a poor 1950s scan actually produces once a 1990s OCR engine has been at it: words
    /// fragmented across stray marks, letters mistaken for punctuation, and speckle recognised as
    /// characters. Note that this is deliberately *not* leetspeak — digit-for-letter substitution
    /// leaves tokens structurally clean, so it is not what the plausibility metric detects, and a
    /// sample built from it would pass. The files this corpus actually flags score between 0.17
    /// and 0.55 on the plausible-token ratio, and this sample is built to sit in that band.
    /// </summary>
    private static string[] GarbledOcr() => Dense(
        ("T'|1e i(~strurne(~t i~ sh\"pped w!~h ~he l,ne v0l'~age se|. ec'~0r se~ " +
         "f{)r 0pera'~i0n ,. ;' ~, l' .l (~ ~) '' \"\" ;; ~~ .. ,, " +
         "0(~ ~he v0l'~age rn&rked 0(~ ~he re&r p&(~e| C'|1eck ~h&~ ~he se~'~i(~g " +
         "i~ c0rrec'~ bef0re y0u c0(~(~ec'~ ~he p0wer c&b|e").Split(' '));

    [Fact]
    public void CleanProseIsGoodText()
    {
        var result = ClassifyWords([Prose(), Prose(), Prose()]);
        Assert.Equal(TextClass.GoodText, result.Class);
    }

    [Fact]
    public void GarbledTextIsSuspect()
    {
        var result = ClassifyWords([GarbledOcr(), GarbledOcr(), GarbledOcr()]);
        Assert.Equal(TextClass.SuspectText, result.Class);
    }

    [Fact]
    public void AnEmptyTextLayerIsImageOnly()
    {
        var result = ClassifyWords([[], [], []]);
        Assert.Equal(TextClass.ImageOnly, result.Class);
    }

    [Fact]
    public void APageOrTwoOfStrayMarksIsStillImageOnly()
    {
        // A scanned page often yields a handful of stray characters from a stamp or a page number.
        var result = ClassifyWords([["3-14"], ["A"], ["1977"]]);
        Assert.Equal(TextClass.ImageOnly, result.Class);
    }

    /// <summary>
    /// The trap this whole design exists to avoid. A parts cross-reference has flawless text and
    /// almost no English words in it. Condemning it on common-word share would send thousands of
    /// pages back through OCR for nothing.
    /// </summary>
    [Fact]
    public void APartsCrossReferenceIsNotSuspectDespiteHavingNoProse()
    {
        string[] partNumbers =
        [
            "0180-0291", "1901-0518", "0757-0280", "1854-0071", "0160-4281", "5062-3731",
            "08340-60019", "1826-0139", "0698-3450", "2100-3210", "1200-0638", "0490-1176",
            "08350-60101", "1251-4787", "0361-1190", "1400-0249", "2950-0132", "3050-0227",
        ];

        var result = ClassifyWords([Dense(partNumbers), Dense(partNumbers), Dense(partNumbers)]);

        Assert.Equal(TextClass.GoodText, result.Class);
        Assert.True(result.CommonWordShare < 0.05, "A parts list has no prose, as expected.");
        Assert.True(result.PlausibleTokenRatio > 0.9, "But its tokens are perfectly well formed.");
    }

    [Fact]
    public void ScpiMnemonicsAreNotSuspect()
    {
        string[] mnemonics =
        [
            "SENSe:FREQuency:STARt", "CALCulate:MARKer:MAXimum", "INITiate:CONTinuous",
            "TRIGger:SEQuence:SOURce", "OUTPut:STATe", "SOURce:POWer:LEVel:IMMediate",
            "DISPlay:WINDow:TRACe:Y:SCALe:RLEVel", "MMEMory:STORe:STATe", "SYSTem:ERRor",
            "ABORt", "FETCh", "READ", "CONFigure:VOLTage:DC", "MEASure:CURRent:AC",
        ];

        var result = ClassifyWords([Dense(mnemonics), Dense(mnemonics), Dense(mnemonics)]);

        Assert.NotEqual(TextClass.SuspectText, result.Class);
        Assert.True(result.PlausibleTokenRatio > 0.9);
    }

    [Fact]
    public void StructuralSeparatorsInsideTechnicalTokensAreNotTreatedAsNoise()
    {
        // Regression: internal colons in a SCPI mnemonic used to count as junk, which made every
        // programming manual in the library look garbled and eligible for needless re-OCR.
        var scpi = TextMetricsCalculator.Measure(1, ["SENSe:FREQuency:STARt", "SOURce:POWer:LEVel:IMMediate"]);
        Assert.Equal(2, scpi.PlausibleTokenCount);

        var partNumbers = TextMetricsCalculator.Measure(1, ["08340-60019", "1250-0118", "5062-3731"]);
        Assert.Equal(3, partNumbers.PlausibleTokenCount);

        // Genuine noise must still fail.
        var noise = TextMetricsCalculator.Measure(1, ["@#$%", "~~^^", "&&&&"]);
        Assert.Equal(0, noise.PlausibleTokenCount);
    }

    [Fact]
    public void AMultilingualManualIsNotSuspect()
    {
        // Low English-word share, flawless text. Same trap, different cause.
        string[] german =
        ("Das Gerät wird mit dem Netzspannungswähler ausgeliefert der für den Betrieb mit der " +
         "auf der Rückseite angegebenen Spannung eingestellt ist").Split(' ');

        var result = ClassifyWords([Dense(german), Dense(german), Dense(german)]);

        Assert.NotEqual(TextClass.SuspectText, result.Class);
    }

    [Fact]
    public void ProseCanPromoteAMiddlingTokenScoreButNeverCondemnsCleanText()
    {
        // Clean tokens with no prose must not fall below GoodText...
        var noProse = ClassifyWords([Dense(["0180-0291", "1901-0518", "0757-0280", "1854-0071"], 12)]);
        Assert.Equal(TextClass.GoodText, noProse.Class);

        // ...and the rationale should say why, so the number is never taken on trust.
        Assert.Contains("not prose", noProse.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRationaleAlwaysCarriesTheNumbersBehindTheVerdict()
    {
        var result = ClassifyWords([Prose(), Prose()]);
        Assert.False(string.IsNullOrWhiteSpace(result.Rationale));
        Assert.Contains("%", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void MetricsPoolAcrossPagesRatherThanAveragingRatios()
    {
        // One dense good page and three nearly empty ones: the verdict should follow the text that
        // actually exists, not be dragged around by pages with three tokens on them.
        var result = ClassifyWords([Prose().Concat(Prose()).Concat(Prose()).ToArray(), ["ok"], ["ok"], ["ok"]]);
        Assert.Equal(TextClass.GoodText, result.Class);
    }

    [Theory]
    [InlineData(100, 8, 8)]
    [InlineData(4, 8, 4)]
    [InlineData(0, 8, 0)]
    public void SamplingReturnsTheExpectedNumberOfPages(int pageCount, int sampleSize, int expected)
        => Assert.Equal(expected, DocumentClassifier.SamplePageNumbers(pageCount, sampleSize).Count);

    [Fact]
    public void SamplingSkipsTheFrontAndBackMatter()
    {
        var pages = DocumentClassifier.SamplePageNumbers(400, 8);
        Assert.All(pages, p => Assert.InRange(p, 40, 360));
    }

    [Fact]
    public void SamplingStaysWithinTheDocument()
    {
        foreach (var count in new[] { 1, 2, 9, 37, 151, 828 })
        {
            var pages = DocumentClassifier.SamplePageNumbers(count, 8);
            Assert.All(pages, p => Assert.InRange(p, 1, count));
            Assert.Equal(pages.Distinct().Count(), pages.Count);
        }
    }

    [Fact]
    public void DefaultPolicyOnlyOcrsWhatHasNoTextAtAll()
    {
        var policy = new ClassificationPolicy();

        Assert.Equal(ClassAction.Ocr, policy.ActionFor(TextClass.ImageOnly));
        Assert.Equal(ClassAction.Skip, policy.ActionFor(TextClass.GoodText));
        Assert.Equal(ClassAction.Skip, policy.ActionFor(TextClass.SuspectText));
        Assert.Equal(ClassAction.Skip, policy.ActionFor(TextClass.ProbablyGood));
        Assert.Equal(ClassAction.Skip, policy.ActionFor(TextClass.Unreadable));
    }

    [Fact]
    public void PolicyCanBeOverriddenPerClass()
    {
        var policy = ClassificationPolicy.Parse("ImageOnly=ocr,SuspectText=redo,ProbablyGood=skip");

        Assert.Equal(ClassAction.Ocr, policy.ActionFor(TextClass.ImageOnly));
        Assert.Equal(ClassAction.StripAndRedo, policy.ActionFor(TextClass.SuspectText));
        Assert.Equal(ClassAction.Skip, policy.ActionFor(TextClass.ProbablyGood));
    }

    [Theory]
    [InlineData("NotAClass=ocr")]
    [InlineData("ImageOnly=explode")]
    [InlineData("ImageOnly")]
    public void AMalformedPolicyIsRejectedRatherThanIgnored(string specification)
        => Assert.Throws<ArgumentException>(() => ClassificationPolicy.Parse(specification));

    [Fact]
    public void WordsAreCountedFromRealTokensNotFromAGlyphRun()
    {
        // The bug this guards: measuring a PDF that positions words rather than emitting spaces.
        // Fed the unbroken run, nothing is a common word; fed real words, the prose is obvious.
        var runTogether = TextMetricsCalculator.Measure(1, ["Theinstrumentisshippedwiththelinevoltageselector"]);
        var separated = TextMetricsCalculator.Measure(1, Prose());

        Assert.Equal(0, runTogether.CommonWordCount);
        Assert.True(separated.CommonWordShare > 0.3,
            $"Real words should read as prose, got {separated.CommonWordShare:P0}.");
    }
}
