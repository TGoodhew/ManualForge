using System.Globalization;
using System.Text;
using PdfSharp.Pdf;

namespace ManualForge.Core.Text;

/// <summary>
/// The embedded font used for the invisible text layer, built up as a document is written.
///
/// Characters are assigned glyph IDs in first-seen order, so the font subsets itself to whatever
/// the OCR actually produced. The PDF objects are created immediately, because pages need to
/// reference the font while they are being written, but the font program and the ToUnicode CMap
/// can only be generated once the last page is done. <see cref="Finalise"/> fills them in, and
/// must be called before the document is saved.
/// </summary>
public sealed class InvisibleFont
{
    // Identity-H means the two-byte code in the content stream *is* the CID, and an Identity
    // CIDToGIDMap means the CID is also the glyph index. So code == GID throughout, and the only
    // table we need is code -> Unicode, which is exactly what ToUnicode carries.
    private readonly Dictionary<int, ushort> _gidByCodepoint = [];
    private readonly List<int> _codepointByGid = [0];   // index 0 is .notdef
    private readonly PdfDocument _document;
    private readonly PdfDictionary _type0;
    private readonly PdfDictionary _cidFont;
    private readonly PdfDictionary _descriptor;
    private bool _finalised;

    public InvisibleFont(PdfDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));

        _descriptor = NewIndirectDictionary();
        _descriptor.Elements["/Type"] = new PdfName("/FontDescriptor");
        _descriptor.Elements["/FontName"] = new PdfName("/" + GlyphlessTrueTypeFont.PostScriptName);
        // Symbolic: the font does not use the standard Latin character set, which is true of an
        // Identity-encoded CID font and stops readers second-guessing our encoding.
        _descriptor.Elements["/Flags"] = new PdfInteger(4);
        _descriptor.Elements["/FontBBox"] = new PdfArray(_document,
            new PdfInteger(GlyphlessTrueTypeFont.GlyphXMin),
            new PdfInteger(GlyphlessTrueTypeFont.GlyphYMin),
            new PdfInteger(GlyphlessTrueTypeFont.GlyphXMax),
            new PdfInteger(GlyphlessTrueTypeFont.GlyphYMax));
        _descriptor.Elements["/ItalicAngle"] = new PdfInteger(0);
        _descriptor.Elements["/Ascent"] = new PdfInteger(GlyphlessTrueTypeFont.Ascender);
        _descriptor.Elements["/Descent"] = new PdfInteger(GlyphlessTrueTypeFont.Descender);
        _descriptor.Elements["/CapHeight"] = new PdfInteger(GlyphlessTrueTypeFont.CapHeight);
        _descriptor.Elements["/StemV"] = new PdfInteger(80);

        _cidFont = NewIndirectDictionary();
        _cidFont.Elements["/Type"] = new PdfName("/Font");
        _cidFont.Elements["/Subtype"] = new PdfName("/CIDFontType2");
        _cidFont.Elements["/BaseFont"] = new PdfName("/" + GlyphlessTrueTypeFont.PostScriptName);
        var systemInfo = new PdfDictionary(_document);
        systemInfo.Elements["/Registry"] = new PdfString("Adobe");
        systemInfo.Elements["/Ordering"] = new PdfString("Identity");
        systemInfo.Elements["/Supplement"] = new PdfInteger(0);
        _cidFont.Elements["/CIDSystemInfo"] = systemInfo;
        _cidFont.Elements["/FontDescriptor"] = _descriptor.Reference!;
        // Every glyph advances by the same amount, so the default width covers all of them and no
        // /W array is needed. This is what makes the Tz calculation exact.
        _cidFont.Elements["/DW"] = new PdfInteger(GlyphlessTrueTypeFont.AdvanceWidth);
        _cidFont.Elements["/CIDToGIDMap"] = new PdfName("/Identity");

        _type0 = NewIndirectDictionary();
        _type0.Elements["/Type"] = new PdfName("/Font");
        _type0.Elements["/Subtype"] = new PdfName("/Type0");
        _type0.Elements["/BaseFont"] = new PdfName("/" + GlyphlessTrueTypeFont.PostScriptName);
        _type0.Elements["/Encoding"] = new PdfName("/Identity-H");
        _type0.Elements["/DescendantFonts"] = new PdfArray(_document, _cidFont.Reference!);
    }

    /// <summary>The Type0 font object that page resource dictionaries should reference.</summary>
    public PdfDictionary FontDictionary => _type0;

    /// <summary>Number of distinct characters encoded so far, excluding .notdef.</summary>
    public int GlyphCount => _codepointByGid.Count - 1;

    /// <summary>
    /// Encodes text as a hex string of two-byte glyph codes, registering any characters not seen
    /// before. Returns null when the text contains nothing encodable.
    /// </summary>
    public string? EncodeToHex(string text)
    {
        if (_finalised)
            throw new InvalidOperationException("The font has been finalised and can no longer encode text.");
        if (string.IsNullOrEmpty(text))
            return null;

        var hex = new StringBuilder(text.Length * 4);
        var enumerator = StringInfo.GetTextElementEnumerator(text);

        // Walk by codepoint rather than by char so that astral characters map to one glyph whose
        // ToUnicode entry carries the full surrogate pair.
        for (var i = 0; i < text.Length;)
        {
            int codepoint;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codepoint = char.ConvertToUtf32(text[i], text[i + 1]);
                i += 2;
            }
            else
            {
                codepoint = text[i];
                i += 1;
            }

            // Control characters carry no ink and confuse extractors; drop them.
            if (codepoint < 0x20 && codepoint != 0x09)
                continue;

            var gid = GetOrAddGlyph(codepoint);
            hex.Append(gid.ToString("X4", CultureInfo.InvariantCulture));
        }

        _ = enumerator;
        return hex.Length == 0 ? null : hex.ToString();
    }

    /// <summary>Number of glyphs a hex string produced by <see cref="EncodeToHex"/> represents.</summary>
    public static int GlyphCountOfHex(string hex) => hex.Length / 4;

    private ushort GetOrAddGlyph(int codepoint)
    {
        if (_gidByCodepoint.TryGetValue(codepoint, out var existing))
            return existing;

        if (_codepointByGid.Count > 65535)
            throw new InvalidOperationException("The invisible font has run out of glyph slots (65535).");

        var gid = (ushort)_codepointByGid.Count;
        _codepointByGid.Add(codepoint);
        _gidByCodepoint[codepoint] = gid;
        return gid;
    }

    /// <summary>
    /// Generates the font program and ToUnicode CMap for the characters seen so far. Call once,
    /// after the last page has been written and before saving.
    /// </summary>
    public void Finalise()
    {
        if (_finalised)
            return;
        _finalised = true;

        var glyphCount = _codepointByGid.Count;
        var program = GlyphlessTrueTypeFont.Build(glyphCount);

        var fontFile = NewIndirectDictionary();
        fontFile.CreateStream(program);
        // The stream is stored uncompressed, so Length1 is simply its length.
        fontFile.Elements["/Length1"] = new PdfInteger(program.Length);
        _descriptor.Elements["/FontFile2"] = fontFile.Reference!;

        var toUnicode = NewIndirectDictionary();
        toUnicode.CreateStream(Encoding.ASCII.GetBytes(BuildToUnicodeCMap()));
        _type0.Elements["/ToUnicode"] = toUnicode.Reference!;
    }

    private string BuildToUnicodeCMap()
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n");
        sb.Append("12 dict begin\n");
        sb.Append("begincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n");
        sb.Append("/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

        // Glyph 0 is .notdef and deliberately gets no mapping: an extractor that hits it should
        // produce nothing rather than a spurious character.
        var mappings = new List<(ushort Gid, int Codepoint)>(_codepointByGid.Count);
        for (var gid = 1; gid < _codepointByGid.Count; gid++)
            mappings.Add(((ushort)gid, _codepointByGid[gid]));

        // The spec caps a bfchar section at 100 entries.
        const int chunkSize = 100;
        for (var start = 0; start < mappings.Count; start += chunkSize)
        {
            var chunk = mappings.Skip(start).Take(chunkSize).ToList();
            sb.Append(CultureInfo.InvariantCulture, $"{chunk.Count} beginbfchar\n");
            foreach (var (gid, codepoint) in chunk)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<{gid:X4}> <{Utf16BeHex(codepoint)}>\n");
            }
            sb.Append("endbfchar\n");
        }

        sb.Append("endcmap\n");
        sb.Append("CMapName currentdict /CMap defineresource pop\n");
        sb.Append("end\nend\n");
        return sb.ToString();
    }

    private static string Utf16BeHex(int codepoint)
    {
        if (codepoint <= 0xFFFF)
            return codepoint.ToString("X4", CultureInfo.InvariantCulture);

        // Astral plane: emit the surrogate pair, which is what a ToUnicode value must contain.
        var adjusted = codepoint - 0x10000;
        var high = 0xD800 + (adjusted >> 10);
        var low = 0xDC00 + (adjusted & 0x3FF);
        return string.Create(CultureInfo.InvariantCulture, $"{high:X4}{low:X4}");
    }

    private PdfDictionary NewIndirectDictionary()
    {
        var dictionary = new PdfDictionary(_document);
        _document.Internals.AddObject(dictionary);
        return dictionary;
    }
}
