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
    /// A scanned page that also carries a text layer, standing in for a manual with poor
    /// 2000s-era OCR already on it. The text is written as a bare BT/ET block in Helvetica, one of
    /// the standard fourteen, so nothing has to be embedded.
    /// </summary>
    public static string ScannedWithText(string path, string text, int pages = 1)
    {
        Scanned(path, pages);

        using var document = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        for (var i = 0; i < document.PageCount; i++)
            AddTextLayer(document.Pages[i], text);

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

    private static void AddTextLayer(PdfPage page, string text)
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

        // Parentheses and backslashes delimit and escape a PDF literal string, so they have to be
        // escaped before the text goes into one.
        var escaped = text.Replace(@"\", @"\\").Replace("(", @"\(").Replace(")", @"\)");
        var content = page.Contents.AppendContent();
        content.CreateStream(Encoding.ASCII.GetBytes(
            $"BT /TestF1 11 Tf 72 700 Td ({escaped}) Tj ET\n"));
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

    /// <summary>A minimal valid greyscale PNG, written by hand to avoid a drawing dependency.</summary>
    public static byte[] GreyscalePng(int width, int height)
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
                raw[y * (width + 1) + 1 + x] = (byte)((x * 7 + y * 13) % 256);
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
}
