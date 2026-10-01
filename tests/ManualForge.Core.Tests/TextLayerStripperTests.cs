using System.Text;
using ManualForge.Core.Classification;
using ManualForge.Core.Pdf;
using ManualForge.Core.Rendering;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Core;

namespace ManualForge.Core.Tests;

/// <summary>
/// The stripper takes a hidden OCR layer off a page and nothing else. On 30 September 2026 it took
/// every text block, and a re-read removed a typeset contents page from one manual and a stamp or
/// footer from four others.
/// </summary>
public class TextLayerStripperTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));

    public TextLayerStripperTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string In(string name) => Path.Combine(_directory, name);

    private string Strip(string path, out StripResult result)
    {
        var stripped = In("stripped-" + Path.GetFileName(path));
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        result = TextLayerStripper.Strip(document);
        document.Save(stripped);
        return stripped;
    }

    /// <summary>Each page's letters, as (text, shown visibly), in the order they are drawn.</summary>
    private static List<(string Text, bool Visible, double X)> Letters(string path, int page = 1)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        return document.GetPage(page).Letters
            .Select(l => (l.Value, l.RenderingMode is not (TextRenderingMode.Neither or TextRenderingMode.NeitherClip),
                l.StartBaseLine.X))
            .ToList();
    }

    private static string VisibleText(string path, int page = 1)
        => string.Concat(Letters(path, page).Where(l => l.Visible).Select(l => l.Text));

    private static string HiddenText(string path, int page = 1)
        => string.Concat(Letters(path, page).Where(l => !l.Visible).Select(l => l.Text));

    private static bool RendersTheSame(string a, string b)
    {
        var rasteriser = new PageRasteriser(new RasterOptions { Dpi = 72 });
        var aBytes = File.ReadAllBytes(a);
        var bBytes = File.ReadAllBytes(b);
        for (var i = 0; i < PageRasteriser.GetPageCount(a); i++)
        {
            using var pa = rasteriser.Render(aBytes, i);
            using var pb = rasteriser.Render(bBytes, i);
            if (!pa.Bitmap.Bytes.AsSpan().SequenceEqual(pb.Bitmap.Bytes))
                return false;
        }

        return true;
    }

    [Fact]
    public void PrintedTextStaysAndOnlyTheHiddenLayerGoes()
    {
        var path = TestPdf.ScannedWithContent(In("mixed.pdf"),
            "BT /TestF1 11 Tf 72 740 Td (TABLE OF CONTENTS) Tj ET\n" +
            "BT /TestF1 11 Tf 3 Tr 72 600 Td (stale ocr) Tj ET\n");

        var stripped = Strip(path, out var result);

        Assert.Equal(1, result.TextBlocksRemoved);
        Assert.Equal(1, result.VisibleBlocksKept);
        Assert.Equal("TABLEOFCONTENTS", VisibleText(stripped).Replace(" ", ""));
        Assert.Equal("", HiddenText(stripped));
        Assert.True(RendersTheSame(path, stripped));
        Assert.Empty(TextLayerProbe.PagesWithHiddenText(stripped));
    }

    [Fact]
    public void TextLaterOnThePageKeepsTheStateAHiddenBlockSetForIt()
    {
        // The second block names no font and sets no render mode: it inherits both from the first,
        // because text state outlives ET. Cutting the first block whole would leave the heading
        // with no font, or drawn in whatever mode came before.
        var path = TestPdf.ScannedWithContent(In("inherits.pdf"),
            "BT /TestF1 11 Tf 3 Tr 72 600 Td (stale) Tj 0 Tr ET\n" +
            "BT 72 740 Td (HEADING) Tj ET\n");

        var stripped = Strip(path, out _);

        Assert.Equal("HEADING", VisibleText(stripped));
        Assert.Equal("", HiddenText(stripped));
        Assert.True(RendersTheSame(path, stripped));
    }

    [Fact]
    public void AHiddenStringAmongVisibleOnesGoesWhenTheNextIsPlacedAfresh()
    {
        var path = TestPdf.ScannedWithContent(In("interleaved.pdf"),
            "BT /TestF1 11 Tf 72 740 Td (KEEP) Tj 3 Tr 0 -20 Td (gone) Tj 0 Tr 0 -20 Td (ALSO) Tj ET\n");

        var stripped = Strip(path, out _);

        Assert.Equal("KEEPALSO", VisibleText(stripped));
        Assert.Equal("", HiddenText(stripped));

        // Td moves from the start of the line, not from where the last string ended, so ALSO is
        // still drawn where it was.
        Assert.Equal(72, Letters(stripped).First(l => l.Text == "A").X, precision: 1);
        Assert.True(RendersTheSame(path, stripped));
    }

    [Fact]
    public void AHiddenStringThatPlacesTheNextOneStaysAndTheCheckSeesIt()
    {
        // VISIBLE starts where "stuck" ends. Taking "stuck" out would move it, so it stays - and
        // the check before recognition then refuses the file rather than layering over it.
        var path = TestPdf.ScannedWithContent(In("stuck.pdf"),
            "BT /TestF1 11 Tf 72 740 Td 3 Tr (stuck) Tj 0 Tr (VISIBLE) Tj ET\n");

        var stripped = Strip(path, out _);

        Assert.Equal("VISIBLE", VisibleText(stripped));
        Assert.Equal("stuck", HiddenText(stripped));
        Assert.True(RendersTheSame(path, stripped));
        Assert.NotEmpty(TextLayerProbe.PagesWithHiddenText(stripped));
    }

    [Fact]
    public void FormsAreStrippedOnceAndTheirPrintedTextKept()
    {
        // Acrobat draws headers, footers and watermarks from form XObjects shared by every page.
        var path = TestPdf.Scanned(In("forms.pdf"), pages: 2);
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            var font = TestPdf.AddTestFont(document.Pages[0]);
            var hidden = Form(document, font, "BT /TestF1 10 Tf 3 Tr 72 500 Td (stale form ocr) Tj ET");
            var stamp = Form(document, font, "BT /TestF1 10 Tf 0 Tr 72 30 Td (Scans by Artekmedia) Tj ET");

            foreach (var page in document.Pages.Cast<PdfPage>())
            {
                var resources = page.Elements.GetDictionary("/Resources")!;
                var xobjects = resources.Elements.GetDictionary("/XObject");
                if (xobjects is null)
                {
                    xobjects = new PdfDictionary(document);
                    resources.Elements["/XObject"] = xobjects;
                }

                xobjects.Elements["/TestFm0"] = hidden.Reference!;
                xobjects.Elements["/TestFm1"] = stamp.Reference!;
                page.Contents.AppendContent().CreateStream("q /TestFm0 Do Q q /TestFm1 Do Q\n"u8.ToArray());
            }

            document.Save(path);
        }

        var stripped = Strip(path, out var result);

        // One hidden block, in one shared form, removed once.
        Assert.Equal(1, result.TextBlocksRemoved);
        for (var page = 1; page <= 2; page++)
        {
            Assert.Equal("ScansbyArtekmedia", VisibleText(stripped, page).Replace(" ", ""));
            Assert.Equal("", HiddenText(stripped, page));
        }

        Assert.True(RendersTheSame(path, stripped));
    }

    private static PdfDictionary Form(PdfDocument document, PdfDictionary font, string content)
    {
        var form = new PdfDictionary(document);
        form.Elements["/Type"] = new PdfName("/XObject");
        form.Elements["/Subtype"] = new PdfName("/Form");
        form.Elements["/BBox"] = new PdfArray(document,
            new PdfReal(0), new PdfReal(0), new PdfReal(TestPdf.WidthPt), new PdfReal(TestPdf.HeightPt));

        var fonts = new PdfDictionary(document);
        fonts.Elements["/TestF1"] = font.Reference!;
        var resources = new PdfDictionary(document);
        resources.Elements["/Font"] = fonts;
        form.Elements["/Resources"] = resources;

        form.CreateStream(Encoding.ASCII.GetBytes(content));
        document.Internals.AddObject(form);
        return form;
    }

    [Fact]
    public void InlineImagesComeThroughByteForByte()
    {
        // PDFsharp's content parser changed how ten pages of one manual rendered on a round trip
        // that removed nothing, every one of them carrying inline images. The bytes around a cut
        // are copied, never rewritten - including image data with "EI" inside it.
        var image = "q 100 0 0 100 300 300 cm BI /W 2 /H 2 /BPC 8 /CS /G ID \u0010EIÿ\u0080 EI Q\n";
        var path = TestPdf.ScannedWithContent(In("inline.pdf"),
            image + "BT /TestF1 11 Tf 3 Tr 72 600 Td (stale) Tj ET\n");

        var stripped = Strip(path, out var result);

        Assert.Equal(1, result.TextBlocksRemoved);
        using (var document = PdfReader.Open(stripped, PdfDocumentOpenMode.Import))
        {
            var content = document.Pages[0].Contents.Elements
                .Select(e => ((PdfDictionary)((PdfReference)e).Value).Stream.UnfilteredValue)
                .SelectMany(b => b)
                .ToArray();
            var text = Encoding.Latin1.GetString(content);
            Assert.Contains(image.TrimEnd('\n'), text, StringComparison.Ordinal);
            Assert.DoesNotContain("stale", text, StringComparison.Ordinal);
        }

        Assert.True(RendersTheSame(path, stripped));
    }

    [Fact]
    public void AnOcrLayerOfNothingButSpacesIsStillALayer()
    {
        // HP_419A's old OCR decodes to 28,590 spaces in hidden text. They are still letters an
        // extractor returns, sorted in among ours.
        var path = TestPdf.ScannedWithContent(In("spaces.pdf"),
            "BT /TestF1 11 Tf 3 Tr 72 600 Td (     ) Tj ET\n");

        Assert.NotEmpty(TextLayerProbe.PagesWithHiddenText(path));
        var stripped = Strip(path, out _);
        Assert.Empty(TextLayerProbe.PagesWithHiddenText(stripped));
    }
}
