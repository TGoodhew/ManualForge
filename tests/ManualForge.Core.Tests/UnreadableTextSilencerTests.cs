using ManualForge.Core.Classification;
using ManualForge.Core.Pdf;
using ManualForge.Core.Rendering;
using PdfSharp.Pdf.IO;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// Printed text that decodes to nothing useful is silenced rather than stripped: it has to stay
/// exactly as drawn, because it is the ink on the page, and stop extracting.
/// </summary>
public sealed class UnreadableTextSilencerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "manualforge-silence-" + Guid.NewGuid().ToString("N"));

    public UnreadableTextSilencerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static string[] Letters(string path)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        return document.GetPage(1).Letters.Select(l => l.Value).ToArray();
    }

    private string Silenced(string source)
    {
        var target = Path.Combine(_root, "silenced.pdf");
        using var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify);
        Assert.Equal(1, UnreadableTextSilencer.Silence(document));
        document.Save(target);
        return target;
    }

    [Fact]
    public void TheUnreadableTextStopsExtractingAndThePageLooksTheSame()
    {
        var source = TestPdf.ScannedWithUnreadableText(Path.Combine(_root, "oven.pdf"));
        Assert.Contains("#", Letters(source));

        var target = Silenced(source);

        // Every glyph is still there, drawn where it was; none of them says anything.
        Assert.Equal(Letters(source).Length, Letters(target).Length);
        Assert.All(Letters(target), l => Assert.True(string.IsNullOrWhiteSpace(l)));

        var raster = new PageRasteriser(new RasterOptions { Dpi = 100 });
        using var before = raster.Render(File.ReadAllBytes(source), 0);
        using var after = raster.Render(File.ReadAllBytes(target), 0);
        Assert.True(before.Bitmap.Bytes.AsSpan().SequenceEqual(after.Bitmap.Bytes), "the page should render identically");
    }

    [Fact]
    public void AFontThatIsNotEmbeddedKeepsItsText()
    {
        // Helvetica is drawn by the viewer through a standard encoding, so it always decodes - and
        // PdfPig would ignore a /ToUnicode on it anyway, leaving the two readers disagreeing.
        var source = TestPdf.ScannedWithUnreadableText(Path.Combine(_root, "front.pdf"), readable: "Agilent Technologies");

        var target = Silenced(source);

        Assert.Equal("AgilentTechnologies", string.Concat(Letters(target).Where(l => !string.IsNullOrWhiteSpace(l))));
    }

    [Fact]
    public void ALittleReadableTextIsSparseRatherThanUnreadable()
    {
        // HP419Mod.pdf: one printed footer on a scan, 90 of its 113 glyphs letters and digits. Too
        // few to be a text layer, but every one of them reads.
        var path = TestPdf.ScannedWithText(
            Path.Combine(_root, "footer.pdf"),
            @"C:\Users\Someone\Downloads\HP-419ModsShared\PCBExpress\HP-419A modification layout drawing revision 2.pdf");

        Assert.Equal(TextClass.ImageOnly, new DocumentClassifier().Classify(path).Class);
    }

    [Fact]
    public void TextThatDecodesToNothingIsAnUnreadableLayer()
    {
        var path = TestPdf.ScannedWithUnreadableText(Path.Combine(_root, "oven.pdf"));

        Assert.Equal(TextClass.UnreadableTextLayer, new DocumentClassifier().Classify(path).Class);
    }
}
