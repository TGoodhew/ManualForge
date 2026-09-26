using ManualForge.Core.Benchmarking;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using UglyToad.PdfPig;
using PdfDocument = PdfSharp.Pdf.PdfDocument;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace ManualForge.Core.Tests;

/// <summary>
/// A page book exists so two recognisers can be compared on the same pixels. That only holds if the
/// book carries no text of its own - otherwise one engine could read a text layer the other had to
/// recognise, and the comparison would measure nothing.
/// </summary>
public sealed class PageBookTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));

    public PageBookTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A document whose pages carry real, extractable text.</summary>
    private string CreateDocumentWithText(string name, int pages = 3)
    {
        var path = Path.Combine(_directory, name);
        using var document = new PdfDocument();

        for (var i = 1; i <= pages; i++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);

            DrawText(page, $"Klystron reflector page {i}");
        }

        document.Save(path);
        return path;
    }


    /// <summary>
    /// Writes visible text as a raw content stream using a base-14 font. PdfSharp's XFont needs a
    /// font resolver and real font files; Helvetica needs neither, and this fixture only has to
    /// produce something a text extractor can find.
    /// </summary>
    private static void DrawText(PdfPage page, string text)
    {
        var font = new PdfDictionary(page.Owner);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfName("/Helvetica");
        page.Owner.Internals.AddObject(font);

        if (page.Elements.GetDictionary("/Resources") is not { } resources)
        {
            resources = new PdfDictionary(page.Owner);
            page.Elements["/Resources"] = resources;
        }

        if (resources.Elements.GetDictionary("/Font") is not { } fonts)
        {
            fonts = new PdfDictionary(page.Owner);
            resources.Elements["/Font"] = fonts;
        }

        fonts.Elements["/F1"] = font.Reference;

        var content = $"BT /F1 24 Tf 72 200 Td ({text}) Tj ET";
        var stream = new PdfDictionary(page.Owner);
        stream.CreateStream(System.Text.Encoding.ASCII.GetBytes(content));
        page.Owner.Internals.AddObject(stream);

        var contents = new PdfArray(page.Owner);
        contents.Elements.Add(stream.Reference!);
        page.Elements["/Contents"] = contents;
    }

    private static string TextOf(string pdf)
    {
        using var document = PigDocument.Open(pdf);
        return string.Join(" ", document.GetPages().Select(p => p.Text));
    }

    [Fact]
    public void TheSourceReallyDoesHaveTextToLose()
    {
        // Guards the test below: if the fixture stopped carrying text, "no text in the book" would
        // pass for the wrong reason and prove nothing.
        var source = CreateDocumentWithText("source.pdf");
        Assert.Contains("Klystron", TextOf(source), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheAssembledBookCarriesNoTextAtAll()
    {
        var source = CreateDocumentWithText("source.pdf");
        var output = Path.Combine(_directory, "book.pdf");

        var outcomes = new PageBook().Assemble(
            [new PageReference(source, 1), new PageReference(source, 3)], output);

        Assert.All(outcomes, o => Assert.Null(o.Skipped));
        Assert.Equal(2, outcomes.Count);

        var text = TextOf(output).Trim();
        Assert.True(text.Length == 0, $"the book should hold no text, but held: {text[..Math.Min(80, text.Length)]}");
    }

    [Fact]
    public void PagesComeOutInTheOrderTheyWereAskedFor()
    {
        var source = CreateDocumentWithText("source.pdf");
        var output = Path.Combine(_directory, "book.pdf");

        var outcomes = new PageBook().Assemble(
            [new PageReference(source, 3), new PageReference(source, 1)], output);

        Assert.Equal(1, outcomes[0].BookPage);
        Assert.Equal(3, outcomes[0].Source.PageNumber);
        Assert.Equal(2, outcomes[1].BookPage);
        Assert.Equal(1, outcomes[1].Source.PageNumber);
    }

    /// <summary>
    /// A page that cannot be rendered is reported rather than dropped. Silently skipping one would
    /// shift every later page number by one and make the manifest name the wrong source page for
    /// every result after it - a wrong answer that looks entirely normal.
    /// </summary>
    [Fact]
    public void AnUnrenderablePageIsReportedAndDoesNotShiftTheRest()
    {
        var source = CreateDocumentWithText("source.pdf", pages: 2);
        var output = Path.Combine(_directory, "book.pdf");

        var outcomes = new PageBook().Assemble(
            [new PageReference(source, 1), new PageReference(source, 99), new PageReference(source, 2)],
            output);

        Assert.Null(outcomes[0].Skipped);
        Assert.NotNull(outcomes[1].Skipped);
        Assert.Null(outcomes[2].Skipped);

        Assert.Equal(1, outcomes[0].BookPage);
        Assert.Equal(2, outcomes[2].BookPage);
    }

    [Fact]
    public void AskingForNothingRenderableIsAnErrorRatherThanAnEmptyFile()
    {
        var source = CreateDocumentWithText("source.pdf", pages: 1);
        var output = Path.Combine(_directory, "book.pdf");

        Assert.Throws<InvalidOperationException>(() =>
            new PageBook().Assemble([new PageReference(source, 50)], output));

        Assert.False(File.Exists(output));
    }
}
