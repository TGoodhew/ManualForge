using System.Text;
using ManualForge.Core.Geometry;
using ManualForge.Core.Ocr;
using ManualForge.Core.Pipeline;
using ManualForge.Core.Text;
using ManualForge.Core.Verification;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ManualForge.Core.Tests;

/// <summary>
/// Writes a text layer with PDFsharp and reads it back with PdfPig, which parses the content
/// stream and applies the matrices independently. Agreement between the two means the geometry is
/// right rather than merely self-consistent.
///
/// These tests need no OCR models and no GPU: the word boxes are given, so what is under test is
/// purely the placement.
/// </summary>
public class TextLayerRoundTripTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));

    /// <summary>How far a word may land from its intended position before the test fails.</summary>
    /// <remarks>
    /// Content-stream numbers are written to four decimal places, so a fraction of a hundredth of
    /// a point is expected. Anything beyond that is a real geometry error, not rounding.
    /// </remarks>
    private const double TolerancePt = 0.02;

    private const int Dpi = 300;

    public TextLayerRoundTripTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Word boxes in image pixels for a page rasterised at 300 dpi.</summary>
    private static RecognisedWord[] SampleWords() =>
    [
        new("HEWLETT", new RectD(300, 400, 520, 62), 0.98),
        new("PACKARD", new RectD(860, 400, 540, 62), 0.97),
        new("59401A", new RectD(300, 520, 400, 58), 0.95),
        new("Bus", new RectD(300, 1200, 180, 46), 0.93),
        new("System", new RectD(510, 1200, 330, 46), 0.91),
        new("Analyzer", new RectD(870, 1200, 400, 46), 0.90),
        new("frequency-controlling", new RectD(300, 2400, 1180, 44), 0.88),
    ];

    private string CreateSyntheticPage(
        string name,
        double widthPt,
        double heightPt,
        int rotation,
        double llx = 0,
        double lly = 0)
    {
        var path = Path.Combine(_directory, name);
        using var document = new PdfDocument();
        var page = document.AddPage();
        var box = new PdfRectangle(
            new PdfSharp.Drawing.XPoint(llx, lly),
            new PdfSharp.Drawing.XPoint(llx + widthPt, lly + heightPt));
        page.MediaBox = box;
        page.CropBox = box;
        page.Rotate = rotation;

        // Give the page real content, and deliberately leave the graphics state dirty with an
        // unbalanced q. A scanned page that sets a CTM for its image and never restores it is
        // common, and the writer has to survive it.
        var content = page.Contents.AppendContent();
        content.CreateStream(Encoding.ASCII.GetBytes(
            $"q 0.85 g {llx + 10:0.##} {lly + 10:0.##} 80 40 re f\nq 2 0 0 2 100 100 cm\n"));

        document.Save(path);
        return path;
    }

    private static PageGeometry GeometryFor(double widthPt, double heightPt, int rotation, double llx, double lly)
    {
        var quarterTurned = PageGeometry.NormaliseRotation(rotation) is 90 or 270;
        var pxW = (int)Math.Round((quarterTurned ? heightPt : widthPt) * Dpi / 72.0);
        var pxH = (int)Math.Round((quarterTurned ? widthPt : heightPt) * Dpi / 72.0);
        return new PageGeometry(llx, lly, widthPt, heightPt, rotation, pxW, pxH);
    }

    private string WriteLayer(string sourcePath, PageGeometry geometry, IReadOnlyList<RecognisedWord> words)
    {
        var outputPath = Path.Combine(_directory, Path.GetFileNameWithoutExtension(sourcePath) + ".ocr.pdf");

        using (var document = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify))
        {
            var font = new InvisibleFont(document);
            var writer = new TextLayerWriter();
            var result = writer.WritePage(document.Pages[0], geometry, words, font);
            Assert.Equal(words.Count, result.WordsWritten);
            font.Finalise();
            document.Save(outputPath);
        }

        return outputPath;
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(90, 0, 0)]
    [InlineData(180, 0, 0)]
    [InlineData(270, 0, 0)]
    [InlineData(0, 36, 48)]
    [InlineData(90, 36, 48)]
    [InlineData(180, -20, 15)]
    [InlineData(270, 36, 48)]
    public void EveryWordLandsWhereItWasPlaced(int rotation, double llx, double lly)
    {
        const double w = 612, h = 792;
        var words = SampleWords();
        var geometry = GeometryFor(w, h, rotation, llx, lly);
        var source = CreateSyntheticPage($"page-{rotation}-{llx}-{lly}.pdf", w, h, rotation, llx, lly);

        var output = WriteLayer(source, geometry, words);
        var verification = TextLayerVerifier.VerifyPage(output, 1, words, geometry);

        Assert.Equal(words.Length, verification.Alignments.Count);
        Assert.Equal(words.Length, verification.MatchedWords);

        foreach (var alignment in verification.Alignments)
        {
            Assert.True(
                alignment.MaxDeviationPt < TolerancePt,
                $"'{alignment.ExpectedText}' landed {alignment.MaxDeviationPt:F4} pt from its target " +
                $"(baseline expected {alignment.ExpectedBaselineStart} to {alignment.ExpectedBaselineEnd}, " +
                $"got {alignment.ExtractedBaselineStart} to {alignment.ExtractedBaselineEnd}).");
            Assert.True(
                alignment.WidthDeviationPt < TolerancePt,
                $"'{alignment.ExpectedText}' came out {alignment.WidthDeviationPt:F4} pt off in width " +
                $"(expected {alignment.ExpectedWidthPt:F3}, got {alignment.ExtractedWidthPt:F3}).");
        }
    }

    [Fact]
    public void TextComesBackOutExactlyAsItWentIn()
    {
        const double w = 612, h = 792;
        var words = SampleWords();
        var geometry = GeometryFor(w, h, 0, 0, 0);
        var source = CreateSyntheticPage("text.pdf", w, h, 0);

        var output = WriteLayer(source, geometry, words);

        using var document = UglyToad.PdfPig.PdfDocument.Open(output);
        var extracted = string.Concat(document.GetPage(1).Letters.Select(l => l.Value));

        Assert.Equal(string.Concat(words.Select(word => word.Text)), extracted);
    }

    [Fact]
    public void CharactersOutsideLatinOneSurviveTheRoundTrip()
    {
        // Test-equipment manuals are full of these, and they are exactly what a WinAnsi-encoded
        // text layer would lose: ohms, micro, degrees, plus-or-minus.
        const string tricky = "50Ω±3dB@25°C µV √Hz";
        const double w = 612, h = 792;
        var words = new RecognisedWord[] { new(tricky, new RectD(300, 400, 900, 60), 0.9) };
        var geometry = GeometryFor(w, h, 0, 0, 0);
        var source = CreateSyntheticPage("unicode.pdf", w, h, 0);

        var output = WriteLayer(source, geometry, words);

        using var document = UglyToad.PdfPig.PdfDocument.Open(output);
        var extracted = string.Concat(document.GetPage(1).Letters.Select(l => l.Value));

        Assert.Equal(tricky, extracted);
    }

    [Fact]
    public void WordWidthMatchesTheDetectedBoxRatherThanTheFontsNaturalWidth()
    {
        // Without the horizontal-scaling operator the run would keep the font's own advance
        // widths, so a long word in a narrow box would overhang badly. Give one word a box far
        // narrower than its character count implies and check the text still fits it exactly.
        const double w = 612, h = 792;
        var squeezed = new RecognisedWord[]
        {
            new("ABCDEFGHIJKLMNOPQRSTUVWXYZ", new RectD(100, 100, 300, 50), 0.9),
        };
        var geometry = GeometryFor(w, h, 0, 0, 0);
        var source = CreateSyntheticPage("squeeze.pdf", w, h, 0);

        var output = WriteLayer(source, geometry, squeezed);
        var verification = TextLayerVerifier.VerifyPage(output, 1, squeezed, geometry);

        var alignment = Assert.Single(verification.Alignments);
        var expectedWidthPt = 300 * 72.0 / Dpi;

        Assert.Equal(expectedWidthPt, alignment.ExtractedWidthPt, TolerancePt);
    }

    [Fact]
    public void LowConfidenceWordsAreLeftOut()
    {
        const double w = 612, h = 792;
        var words = new RecognisedWord[]
        {
            new("GOOD", new RectD(100, 100, 200, 50), 0.95),
            new("noise", new RectD(100, 200, 200, 50), 0.05),
        };
        var geometry = GeometryFor(w, h, 0, 0, 0);
        var source = CreateSyntheticPage("confidence.pdf", w, h, 0);
        var outputPath = Path.Combine(_directory, "confidence.ocr.pdf");

        using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
        {
            var font = new InvisibleFont(document);
            var result = new TextLayerWriter().WritePage(document.Pages[0], geometry, words, font);
            Assert.Equal(1, result.WordsWritten);
            Assert.Equal(1, result.WordsSkipped);
            font.Finalise();
            document.Save(outputPath);
        }

        using var read = UglyToad.PdfPig.PdfDocument.Open(outputPath);
        Assert.Equal("GOOD", string.Concat(read.GetPage(1).Letters.Select(l => l.Value)));
    }

    [Fact]
    public void VerificationStaysInStepWhenTheWriterSkipsWords()
    {
        // Regression: verification used to walk every recognised word against the extracted
        // letters in order. Any word the writer dropped — for low confidence, or an implausible
        // box — shifted the whole comparison by one and made a perfectly aligned page look wildly
        // wrong. The writer now reports what it actually emitted, and that is what gets checked.
        const double w = 612, h = 792;
        var words = new RecognisedWord[]
        {
            new("FIRST", new RectD(100, 100, 250, 50), 0.95),
            new("dropped", new RectD(100, 200, 250, 50), 0.01),   // below the confidence floor
            new("SECOND", new RectD(100, 300, 300, 50), 0.94),
            new("tiny", new RectD(100, 400, 250, 1), 0.95),       // box too short to be real
            new("THIRD", new RectD(100, 500, 250, 50), 0.93),
        };

        var geometry = GeometryFor(w, h, 0, 0, 0);
        var source = CreateSyntheticPage("skips.pdf", w, h, 0);
        var outputPath = Path.Combine(_directory, "skips.ocr.pdf");

        TextLayerPageResult result;
        using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
        {
            var font = new InvisibleFont(document);
            result = new TextLayerWriter().WritePage(document.Pages[0], geometry, words, font);
            font.Finalise();
            document.Save(outputPath);
        }

        Assert.Equal(3, result.WordsWritten);
        Assert.Equal(2, result.WordsSkipped);
        Assert.Equal(["FIRST", "SECOND", "THIRD"], result.Written.Select(word => word.Text));

        // Verifying against what was written lines up exactly...
        var good = TextLayerVerifier.VerifyPage(outputPath, 1, result.Written, geometry);
        Assert.Equal(3, good.MatchedWords);
        Assert.True(good.WorstDeviationPt < TolerancePt, $"worst was {good.WorstDeviationPt:F4} pt");

        // ...whereas verifying against everything offered would not, which is the bug this guards.
        var naive = TextLayerVerifier.VerifyPage(outputPath, 1, words, geometry);
        Assert.True(naive.MatchedWords < words.Length);
    }

    [Fact]
    public void ReadingAPagesGeometryDoesNotAddACropBoxToIt()
    {
        // Regression, and an expensive one. PDFsharp's CropBox getter *materialises* the entry when
        // it is absent, so simply reading page.CropBox to work out the geometry wrote
        // /CropBox [0 0 0 0] into every page that had none. PDFium ignores that invalid rectangle,
        // but PdfPig honours it and reports the page as zero-sized, which threw every extracted
        // coordinate out by a whole page dimension. Reading a page must not change it.
        const double w = 612, h = 792;
        var path = Path.Combine(_directory, "nocrop.pdf");

        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            page.MediaBox = new PdfRectangle(
                new PdfSharp.Drawing.XPoint(0, 0), new PdfSharp.Drawing.XPoint(w, h));
            page.Rotate = 90;   // deliberately no CropBox
            var content = page.Contents.AppendContent();
            content.CreateStream("q 0.85 g 10 10 80 40 re f Q\n"u8.ToArray());
            document.Save(path);
        }

        var saved = Path.Combine(_directory, "nocrop-after.pdf");
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            var page = document.Pages[0];
            Assert.False(page.Elements.ContainsKey("/CropBox"), "The fixture should start without a crop box.");

            // The call under test. It must read the boxes without writing any.
            var geometry = SearchablePdfBuilder.CreateGeometry(page, 2550, 3300, 1);

            Assert.False(page.Elements.ContainsKey("/CropBox"),
                "Reading the geometry added a /CropBox to a page that had none.");
            Assert.Equal(90, geometry.Rotation);
            Assert.Equal(w, geometry.CropWidth, 0.01);
            Assert.Equal(h, geometry.CropHeight, 0.01);

            document.Save(saved);
        }

        // And it must still be absent once written out, where a reader would see it.
        using (var written = PdfReader.Open(saved, PdfDocumentOpenMode.Modify))
            Assert.False(written.Pages[0].Elements.ContainsKey("/CropBox"));

        using var read = UglyToad.PdfPig.PdfDocument.Open(saved);
        var readPage = read.GetPage(1);
        Assert.True(readPage.Width > 0 && readPage.Height > 0,
            $"PdfPig reports a {readPage.Width}x{readPage.Height} page, so the crop box is degenerate.");
    }

    [Fact]
    public void WordsLandCorrectlyOnAPageThatHasNoCropBox()
    {
        const double w = 612, h = 792;
        var path = Path.Combine(_directory, "nocrop2.pdf");

        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            page.MediaBox = new PdfRectangle(
                new PdfSharp.Drawing.XPoint(0, 0), new PdfSharp.Drawing.XPoint(w, h));
            page.Rotate = 90;
            var content = page.Contents.AppendContent();
            content.CreateStream("q 0.85 g 10 10 80 40 re f Q\n"u8.ToArray());
            document.Save(path);
        }

        var words = SampleWords();
        var outputPath = Path.Combine(_directory, "nocrop2.ocr.pdf");

        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            var page = document.Pages[0];
            var geometry = SearchablePdfBuilder.CreateGeometry(page, 3300, 2550, 1);
            var font = new InvisibleFont(document);
            new TextLayerWriter().WritePage(page, geometry, words, font);
            font.Finalise();
            document.Save(outputPath);
        }

        using (var document = PdfReader.Open(outputPath, PdfDocumentOpenMode.Modify))
        {
            var geometry = SearchablePdfBuilder.CreateGeometry(document.Pages[0], 3300, 2550, 1);
            var verification = TextLayerVerifier.VerifyPage(outputPath, 1, words, geometry);

            Assert.Equal(words.Length, verification.MatchedWords);
            Assert.True(verification.WorstDeviationPt < TolerancePt,
                $"worst deviation {verification.WorstDeviationPt:F3} pt on a page with no crop box");
        }
    }

    [Fact]
    public void ThePageKeepsItsOriginalContent()
    {
        const double w = 612, h = 792;
        var geometry = GeometryFor(w, h, 0, 0, 0);
        var source = CreateSyntheticPage("preserve.pdf", w, h, 0);
        var before = File.ReadAllBytes(source).Length;

        var output = WriteLayer(source, geometry, SampleWords());

        // The original fill operator must still be in the page's content streams, and the page
        // box must not have moved.
        using (var written = PdfReader.Open(output, PdfDocumentOpenMode.Modify))
        {
            var page = written.Pages[0];
            var streams = Encoding.ASCII.GetString(page.Contents.CreateSingleContent().Stream.UnfilteredValue);

            Assert.Contains("80 40 re", streams, StringComparison.Ordinal);
            Assert.Equal(w, page.Width.Point, 1e-6);
            Assert.Equal(h, page.Height.Point, 1e-6);
        }

        using var read = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(w, read.GetPage(1).Width, 1e-6);
        Assert.True(File.ReadAllBytes(output).Length > before, "The overlay should only add to the file.");
    }
}
