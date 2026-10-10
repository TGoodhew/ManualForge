using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace ManualForge.Core.Tests;

/// <summary>
/// Builds the synthetic documents the tests run against.
///
/// They are deliberately small but not trivial: a page carries a real image with a real filter, so
/// the structural comparison has image streams to compare rather than just page boxes, and the
/// text-bearing variant writes its own content stream rather than going through XGraphics, which
/// would need a font resolver and would embed a subset nobody here is testing.
/// </summary>
internal static class TestPdf
{
    public const double WidthPt = 612;
    public const double HeightPt = 792;

    /// <summary>
    /// A scanned page: one image and no text at all, which is what the classifier calls ImageOnly.
    /// </summary>
    public static string Scanned(
        string path,
        int pages = 2,
        string? ownerPassword = null,
        int rotation = 0)
    {
        using var document = NewDocument(path, pages, rotation, out var directory);

        for (var i = 0; i < pages; i++)
        {
            var page = document.Pages[i];
            var imagePath = Path.Combine(directory, $"scan-{60 + i * 10}x{40 + i * 10}.png");
            if (!File.Exists(imagePath))
                File.WriteAllBytes(imagePath, GreyscalePng(60 + i * 10, 40 + i * 10));

            using var gfx = XGraphics.FromPdfPage(page);
            using var image = XImage.FromFile(imagePath);
            gfx.DrawImage(image, 50, 50, 300, 200);
        }

        if (ownerPassword is not null)
        {
            var security = document.SecuritySettings;
            security.OwnerPassword = ownerPassword;
            security.PermitModifyDocument = false;
            security.PermitPrint = true;
        }

        document.Save(path);
        return path;
    }

    /// <summary>
    /// A scanned page that also carries text, written as a bare BT/ET block in Helvetica, one of
    /// the standard fourteen, so nothing has to be embedded. Hidden, it stands in for a manual with
    /// poor 2000s-era OCR already on it; visible, for a stamp or heading printed over the scan.
    /// </summary>
    public static string ScannedWithText(string path, string text, int pages = 1, bool hidden = false)
    {
        Scanned(path, pages);

        using var document = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        for (var i = 0; i < document.PageCount; i++)
            AddTextLayer(document.Pages[i], text, hidden);

        var temporary = path + ".tmp";
        document.Save(temporary);
        document.Dispose();
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// A document carrying what looks to every check we make like a real digital signature: an
    /// AcroForm with a signature field whose value has a /ByteRange. Nothing verifies the
    /// signature itself, here or in the wild, so what matters is only that it is detected.
    /// </summary>
    public static string Signed(string path, int pages = 1)
    {
        Scanned(path, pages);

        using var document = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);

        var value = new PdfDictionary(document);
        value.Elements["/Type"] = new PdfName("/Sig");
        value.Elements["/Filter"] = new PdfName("/Adobe.PPKLite");
        var byteRange = new PdfArray(document);
        foreach (var n in new[] { 0, 840, 960, 240 })
            byteRange.Elements.Add(new PdfInteger(n));
        value.Elements["/ByteRange"] = byteRange;

        var field = new PdfDictionary(document);
        field.Elements["/FT"] = new PdfName("/Sig");
        field.Elements["/T"] = new PdfString("Signature1");
        field.Elements["/V"] = value;
        document.Internals.AddObject(field);

        var fields = new PdfArray(document);
        fields.Elements.Add(field.Reference!);

        var acroForm = new PdfDictionary(document);
        acroForm.Elements["/Fields"] = fields;
        acroForm.Elements["/SigFlags"] = new PdfInteger(3);
        document.Internals.AddObject(acroForm);
        document.Internals.Catalog.Elements["/AcroForm"] = acroForm.Reference!;

        var temporary = path + ".tmp";
        document.Save(temporary);
        document.Dispose();
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// A scanned page with content of the test's own appended to it, for checks that need a precise
    /// arrangement of text operators. Helvetica is available to it as /TestF1.
    /// </summary>
    public static string ScannedWithContent(string path, string content, int pages = 1)
    {
        Scanned(path, pages);

        using var document = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        for (var i = 0; i < document.PageCount; i++)
        {
            AddTestFont(document.Pages[i]);
            document.Pages[i].Contents.AppendContent().CreateStream(Encoding.Latin1.GetBytes(content));
        }

        var temporary = path + ".tmp";
        document.Save(temporary);
        document.Dispose();
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// A scan with three lines of printed text in a font that decodes to nothing useful: every glyph
    /// is ink, and every one extracts as '#'. It stands in for oven.pdf and 83620A User.pdf, whose
    /// subset fonts number their glyphs their own way. A Type 3 font carries its own glyphs, so this
    /// is an embedded font without a font program or a resolver. <paramref name="readable"/>, if
    /// given, is printed below in Helvetica, which is not embedded and always decodes.
    /// </summary>
    public static string ScannedWithUnreadableText(string path, int pages = 1, string? readable = null)
    {
        Scanned(path, pages);

        using var document = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);

        // One glyph, a filled box, for every code from space to tilde.
        var glyph = new PdfDictionary(document);
        glyph.CreateStream(Encoding.ASCII.GetBytes("600 0 0 0 500 700 d1 0 0 500 700 re f\n"));
        document.Internals.AddObject(glyph);

        var procs = new PdfDictionary(document);
        procs.Elements["/box"] = glyph.Reference!;

        var differences = new PdfArray(document);
        differences.Elements.Add(new PdfInteger(32));
        for (var code = 32; code <= 126; code++)
            differences.Elements.Add(new PdfName("/box"));
        var encoding = new PdfDictionary(document);
        encoding.Elements["/Type"] = new PdfName("/Encoding");
        encoding.Elements["/Differences"] = differences;

        var widths = new PdfArray(document);
        for (var code = 32; code <= 126; code++)
            widths.Elements.Add(new PdfInteger(600));

        var hashes = string.Concat(Enumerable.Repeat("<0023>", 95));
        var toUnicode = new PdfDictionary(document);
        toUnicode.CreateStream(Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "/CMapName /Test-Hashes def\n/CMapType 2 def\n" +
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            $"1 beginbfrange\n<20> <7E> [{hashes}]\nendbfrange\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n"));
        document.Internals.AddObject(toUnicode);

        var font = new PdfDictionary(document);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/Type3");
        font.Elements["/FontBBox"] = new PdfArray(document,
            new PdfInteger(0), new PdfInteger(0), new PdfInteger(600), new PdfInteger(700));
        font.Elements["/FontMatrix"] = new PdfArray(document,
            new PdfReal(0.001), new PdfInteger(0), new PdfInteger(0), new PdfReal(0.001), new PdfInteger(0), new PdfInteger(0));
        font.Elements["/CharProcs"] = procs;
        font.Elements["/Encoding"] = encoding;
        font.Elements["/FirstChar"] = new PdfInteger(32);
        font.Elements["/LastChar"] = new PdfInteger(126);
        font.Elements["/Widths"] = widths;
        font.Elements["/Resources"] = new PdfDictionary(document);
        font.Elements["/ToUnicode"] = toUnicode.Reference!;
        document.Internals.AddObject(font);

        const string line = "Broadband Frequency Response of the 8340B synthesized sweeper";
        for (var i = 0; i < document.PageCount; i++)
        {
            var page = document.Pages[i];
            AddTestFont(page);
            page.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/Font")!.Elements["/TestT3"] = font.Reference!;

            var content = new StringBuilder("BT /TestT3 8 Tf 72 700 Td ");
            for (var row = 0; row < 3; row++)
                content.Append(CultureInfo.InvariantCulture, $"({line}) Tj 0 -12 Td ");
            content.Append("ET\n");
            if (readable is not null)
                content.Append(CultureInfo.InvariantCulture, $"BT /TestF1 11 Tf 72 600 Td ({readable}) Tj ET\n");
            page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(content.ToString()));
        }

        var temporary = path + ".tmp";
        document.Save(temporary);
        document.Dispose();
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    /// <summary>Helvetica, as an indirect font object the page's resources call /TestF1.</summary>
    public static PdfDictionary AddTestFont(PdfPage page)
    {
        var font = new PdfDictionary(page.Owner);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfName("/Helvetica");
        page.Owner.Internals.AddObject(font);

        var resources = page.Elements.GetDictionary("/Resources");
        if (resources is null)
        {
            resources = new PdfDictionary(page.Owner);
            page.Elements["/Resources"] = resources;
        }

        var fonts = resources.Elements.GetDictionary("/Font");
        if (fonts is null)
        {
            fonts = new PdfDictionary(page.Owner);
            resources.Elements["/Font"] = fonts;
        }

        fonts.Elements["/TestF1"] = font.Reference!;
        return font;
    }

    private static void AddTextLayer(PdfPage page, string text, bool hidden = false)
    {
        AddTestFont(page);

        // Parentheses and backslashes delimit and escape a PDF literal string, so they have to be
        // escaped before the text goes into one.
        var escaped = text.Replace(@"\", @"\\").Replace("(", @"\(").Replace(")", @"\)");
        var content = page.Contents.AppendContent();
        content.CreateStream(Encoding.ASCII.GetBytes(
            $"BT /TestF1 11 Tf {(hidden ? "3 Tr " : "")}72 700 Td ({escaped}) Tj ET\n"));
    }

    private static PdfDocument NewDocument(string path, int pages, int rotation, out string directory)
    {
        directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        var document = new PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(WidthPt);
            page.Height = XUnit.FromPoint(HeightPt);
            page.Rotate = rotation;
        }

        return document;
    }

    /// <summary>
    /// A greyscale PNG that is uniformly pale enough to count as blank. Stands in for the paper of
    /// a scan: it makes the page a raster without putting any ink on it.
    /// </summary>
    public static byte[] PalePng(int width, int height) => GreyscalePng(width, height, level: 250);

    /// <summary>A minimal valid greyscale PNG, written by hand to avoid a drawing dependency.</summary>
    public static byte[] GreyscalePng(int width, int height, int? level = null)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteBe(ihdr, 0, width);
        WriteBe(ihdr, 4, height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 0;    // greyscale
        WriteChunk(writer, "IHDR", ihdr);

        // One filter byte per row, then a simple gradient so the image is not uniformly blank.
        var raw = new byte[height * (width + 1)];
        for (var y = 0; y < height; y++)
        {
            raw[y * (width + 1)] = 0;
            for (var x = 0; x < width; x++)
                raw[y * (width + 1) + 1 + x] = (byte)(level ?? ((x * 7 + y * 13) % 256));
        }

        using var deflated = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(
            deflated, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        WriteChunk(writer, "IDAT", deflated.ToArray());
        WriteChunk(writer, "IEND", []);

        writer.Flush();
        return ms.ToArray();
    }

    private static void WriteBe(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteChunk(BinaryWriter writer, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBe(length, 0, data.Length);
        writer.Write(length);

        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        writer.Write(typeAndData);

        var crc = Crc32(typeAndData);
        var crcBytes = new byte[4];
        WriteBe(crcBytes, 0, unchecked((int)crc));
        writer.Write(crcBytes);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }
    /// <summary>
    /// A page of the kind this whole exercise is about: a real heading in a real font, and a
    /// "figure" below it drawn as vector graphics — small filled rectangles standing in for the
    /// lettering on a syntax diagram. The heading extracts; the figure extracts as nothing at all.
    /// </summary>
    /// <param name="labels">
    /// How many glyph-sized marks the drawn figure carries. Above the detector's threshold this is
    /// a page worth flagging; below it, a figure number and not worth anybody's time.
    /// </param>
    /// <param name="behindAnImage">
    /// Put a near-white image across the page first, so the same marks are "inside an image" as far
    /// as the audit is concerned. A scanned page and a drawn page can carry identical ink; what
    /// tells them apart is whether there is a raster underneath, and that is worth a test.
    /// </param>
    public static string DrawnFigure(
        string path, string heading, int labels = 120, int pages = 1, bool behindAnImage = false)
    {
        using (var document = NewDocument(path, pages, rotation: 0, out var directory))
        {
            for (var i = 0; i < pages; i++)
            {
                using var gfx = XGraphics.FromPdfPage(document.Pages[i]);
                var brush = XBrushes.Black;

                if (behindAnImage)
                {
                    var imagePath = Path.Combine(directory, "pale.png");
                    if (!File.Exists(imagePath))
                        File.WriteAllBytes(imagePath, PalePng(120, 160));

                    using var image = XImage.FromFile(imagePath);
                    gfx.DrawImage(image, 20, 20, WidthPt - 40, HeightPt - 40);
                }

                // Glyph-shaped: about 4 pt by 6 pt, solid, laid out in rows the way a caption is.
                for (var n = 0; n < labels; n++)
                {
                    var column = n % 20;
                    var row = n / 20;
                    gfx.DrawRectangle(brush, 72 + column * 9, 200 + row * 14, 4, 6);
                }
            }

            document.Save(path);
        }

        using var toEdit = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        for (var i = 0; i < toEdit.PageCount; i++)
            AddTextLayer(toEdit.Pages[i], heading);

        var temporary = path + ".tmp";
        toEdit.Save(temporary);
        toEdit.Dispose();
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// A page carrying nothing but ruled lines and a heading — an empty table, a border, a plot
    /// frame. Plenty of ink, no lettering in it, and nothing for OCR to recover. The detector must
    /// leave this alone, because a detector that flags every schematic in a service manual is one
    /// everybody learns to ignore.
    /// </summary>
    public static string RuledPage(string path, string heading, int rules = 40)
    {
        using (var document = NewDocument(path, pages: 1, rotation: 0, out _))
        {
            using var gfx = XGraphics.FromPdfPage(document.Pages[0]);
            var pen = new XPen(XColors.Black, 1.5);

            for (var n = 0; n < rules; n++)
            {
                gfx.DrawLine(pen, 72, 150 + n * 14, 540, 150 + n * 14);
                gfx.DrawLine(pen, 72 + n * 11, 150, 72 + n * 11, 150 + rules * 14);
            }

            document.Save(path);
        }

        using var toEdit = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        AddTextLayer(toEdit.Pages[0], heading);

        var temporary = path + ".tmp";
        toEdit.Save(temporary);
        toEdit.Dispose();
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    /// <summary>A page with a text layer and nothing else. Nothing here is under-extracted.</summary>
    public static string TypesetOnly(string path, string text, int pages = 1)
    {
        using (var document = NewDocument(path, pages, rotation: 0, out _))
            document.Save(path);

        using var toEdit = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        for (var i = 0; i < toEdit.PageCount; i++)
            AddTextLayer(toEdit.Pages[i], text);

        var temporary = path + ".tmp";
        toEdit.Save(temporary);
        toEdit.Dispose();
        File.Move(temporary, path, overwrite: true);
        return path;
    }
}
