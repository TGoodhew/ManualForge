using System.Buffers.Binary;

namespace ManualForge.Core.Text;

/// <summary>
/// Builds a minimal, valid TrueType font in which every glyph is blank and every glyph advances
/// by exactly half an em.
///
/// This is the same trick tesseract's PDF renderer and ocrmypdf use, and it exists for two
/// reasons. First, invisible text has to be positioned, not drawn, so glyph outlines are pure
/// dead weight; a blank font keeps the overlay at a few kilobytes per document no matter how much
/// text it carries. Second, and more importantly, a uniform advance width makes the horizontal
/// scaling exact: the natural width of an n-character run is precisely
/// <c>0.5 * fontSize * n</c>, so the Tz factor that stretches the run onto the scanned word is a
/// closed-form number rather than a lookup into font metrics that may not match what the reader
/// resolves. Text-rendering mode 3 already hides the text; a font with no outlines means it stays
/// hidden even in a reader that mishandles the mode.
///
/// Glyphs carry a declared bounding box but zero contours, so extractors that ask the font
/// program for glyph extents get a sensible height instead of a degenerate one.
/// </summary>
public static class GlyphlessTrueTypeFont
{
    public const int UnitsPerEm = 1000;

    /// <summary>Advance width of every glyph, in font units. Half an em.</summary>
    public const int AdvanceWidth = 500;

    public const int Ascender = 800;
    public const int Descender = -200;
    public const int CapHeight = 700;

    /// <summary>Glyph bounding box declared for every glyph, in font units.</summary>
    public const int GlyphXMin = 0;
    public const int GlyphYMin = Descender;
    public const int GlyphXMax = AdvanceWidth;
    public const int GlyphYMax = Ascender;

    public const string PostScriptName = "ManualForgeInvisible";

    /// <summary>Bytes per glyph record in the glyf table: a header with no contour data.</summary>
    private const int GlyphRecordLength = 10;

    /// <summary>
    /// Produces the font program for <paramref name="glyphCount"/> glyphs, glyph 0 being .notdef.
    /// </summary>
    public static byte[] Build(int glyphCount)
    {
        if (glyphCount < 1)
            throw new ArgumentOutOfRangeException(nameof(glyphCount), glyphCount, "A font needs at least the .notdef glyph.");
        if (glyphCount > 65535)
            throw new ArgumentOutOfRangeException(nameof(glyphCount), glyphCount, "TrueType supports at most 65535 glyphs.");

        var tables = new (string Tag, byte[] Data)[]
        {
            ("OS/2", BuildOs2()),
            ("cmap", BuildCmap()),
            ("glyf", BuildGlyf(glyphCount)),
            ("head", BuildHead()),
            ("hhea", BuildHhea()),
            ("hmtx", BuildHmtx(glyphCount)),
            ("loca", BuildLoca(glyphCount)),
            ("maxp", BuildMaxp(glyphCount)),
            ("name", BuildName()),
            ("post", BuildPost()),
        };

        return Assemble(tables);
    }

    private static byte[] Assemble((string Tag, byte[] Data)[] tables)
    {
        var numTables = tables.Length;
        var directorySize = 12 + numTables * 16;

        // Lay tables out on 4-byte boundaries and record where each landed.
        var offsets = new int[numTables];
        var cursor = directorySize;
        for (var i = 0; i < numTables; i++)
        {
            offsets[i] = cursor;
            cursor += Align4(tables[i].Data.Length);
        }

        var font = new byte[cursor];
        var w = new BigEndianWriter(font);

        // Offset table. The search values are derived from the largest power of two <= numTables.
        var entrySelector = (int)Math.Floor(Math.Log2(numTables));
        var searchRange = (int)Math.Pow(2, entrySelector) * 16;
        w.WriteUInt32(0x00010000);              // sfntVersion: TrueType outlines
        w.WriteUInt16((ushort)numTables);
        w.WriteUInt16((ushort)searchRange);
        w.WriteUInt16((ushort)entrySelector);
        w.WriteUInt16((ushort)(numTables * 16 - searchRange));

        // Table directory. Entries must be sorted by tag; the caller's array already is.
        for (var i = 0; i < numTables; i++)
        {
            w.WriteTag(tables[i].Tag);
            w.WriteUInt32(TableChecksum(tables[i].Data));
            w.WriteUInt32((uint)offsets[i]);
            w.WriteUInt32((uint)tables[i].Data.Length);     // unpadded length, per spec
        }

        for (var i = 0; i < numTables; i++)
            tables[i].Data.CopyTo(font.AsSpan(offsets[i]));

        // checkSumAdjustment: written into head once the whole file exists.
        var headIndex = Array.FindIndex(tables, t => t.Tag == "head");
        var headOffset = offsets[headIndex];
        var adjustment = unchecked(0xB1B0AFBAu - WholeFontChecksum(font));
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(headOffset + 8), adjustment);

        return font;
    }

    private static byte[] BuildHead()
    {
        var data = new byte[54];
        var w = new BigEndianWriter(data);
        w.WriteUInt32(0x00010000);              // version 1.0
        w.WriteUInt32(0x00010000);              // fontRevision 1.0
        w.WriteUInt32(0);                       // checkSumAdjustment, patched after assembly
        w.WriteUInt32(0x5F0F3CF5);              // magicNumber
        w.WriteUInt16(0x0003);                  // flags: baseline at y=0, lsb at x=0
        w.WriteUInt16(UnitsPerEm);
        w.WriteInt64(0);                        // created
        w.WriteInt64(0);                        // modified
        w.WriteInt16(GlyphXMin);
        w.WriteInt16(GlyphYMin);
        w.WriteInt16(GlyphXMax);
        w.WriteInt16(GlyphYMax);
        w.WriteUInt16(0);                       // macStyle
        w.WriteUInt16(3);                       // lowestRecPPEM
        w.WriteInt16(2);                        // fontDirectionHint: strongly left-to-right
        w.WriteInt16(1);                        // indexToLocFormat: long offsets
        w.WriteInt16(0);                        // glyphDataFormat
        return data;
    }

    private static byte[] BuildHhea()
    {
        var data = new byte[36];
        var w = new BigEndianWriter(data);
        w.WriteUInt32(0x00010000);              // version 1.0
        w.WriteInt16(Ascender);
        w.WriteInt16(Descender);
        w.WriteInt16(0);                        // lineGap
        w.WriteUInt16(AdvanceWidth);            // advanceWidthMax
        w.WriteInt16(0);                        // minLeftSideBearing
        w.WriteInt16(0);                        // minRightSideBearing
        w.WriteInt16(GlyphXMax);                // xMaxExtent
        w.WriteInt16(1);                        // caretSlopeRise
        w.WriteInt16(0);                        // caretSlopeRun
        w.WriteInt16(0);                        // caretOffset
        w.WriteInt16(0);                        // reserved x4
        w.WriteInt16(0);
        w.WriteInt16(0);
        w.WriteInt16(0);
        w.WriteInt16(0);                        // metricDataFormat
        // One metric for the whole font: every remaining glyph inherits the last advance width.
        w.WriteUInt16(1);                       // numberOfHMetrics
        return data;
    }

    private static byte[] BuildMaxp(int glyphCount)
    {
        var data = new byte[32];
        var w = new BigEndianWriter(data);
        w.WriteUInt32(0x00010000);              // version 1.0
        w.WriteUInt16((ushort)glyphCount);
        // Everything below describes outline complexity, and there are no outlines.
        for (var i = 0; i < 13; i++)
            w.WriteUInt16(0);
        return data;
    }

    private static byte[] BuildHmtx(int glyphCount)
    {
        // numberOfHMetrics == 1, so: one longHorMetric, then a left side bearing per extra glyph.
        var data = new byte[4 + (glyphCount - 1) * 2];
        var w = new BigEndianWriter(data);
        w.WriteUInt16(AdvanceWidth);
        w.WriteInt16(0);                        // leftSideBearing of glyph 0
        for (var i = 1; i < glyphCount; i++)
            w.WriteInt16(0);
        return data;
    }

    private static byte[] BuildLoca(int glyphCount)
    {
        // Long format, matching indexToLocFormat 1 in head. Every glyph occupies the same-sized
        // record, so offsets are a simple arithmetic series.
        var data = new byte[(glyphCount + 1) * 4];
        var w = new BigEndianWriter(data);
        for (var i = 0; i <= glyphCount; i++)
            w.WriteUInt32((uint)(i * GlyphRecordLength));
        return data;
    }

    private static byte[] BuildGlyf(int glyphCount)
    {
        // Each glyph is a header declaring zero contours plus a bounding box. Zero contours means
        // nothing is drawn; the bounding box means metrics queries get a real answer.
        var data = new byte[glyphCount * GlyphRecordLength];
        var w = new BigEndianWriter(data);
        for (var i = 0; i < glyphCount; i++)
        {
            w.WriteInt16(0);                    // numberOfContours
            w.WriteInt16(GlyphXMin);
            w.WriteInt16(GlyphYMin);
            w.WriteInt16(GlyphXMax);
            w.WriteInt16(GlyphYMax);
        }
        return data;
    }

    private static byte[] BuildCmap()
    {
        // A CIDFontType2 with an Identity CIDToGIDMap selects glyphs by index and never consults
        // cmap, but a font without one trips strict validators. A format 0 Macintosh subtable
        // mapping every byte to .notdef is the conventional filler.
        var data = new byte[4 + 8 + 6 + 256];
        var w = new BigEndianWriter(data);
        w.WriteUInt16(0);                       // version
        w.WriteUInt16(1);                       // numTables
        w.WriteUInt16(1);                       // platformID: Macintosh
        w.WriteUInt16(0);                       // encodingID: Roman
        w.WriteUInt32(12);                      // offset to the subtable
        w.WriteUInt16(0);                       // format 0
        w.WriteUInt16(262);                     // length
        w.WriteUInt16(0);                       // language
        // glyphIdArray: 256 zero bytes, already zeroed.
        return data;
    }

    private static byte[] BuildPost()
    {
        var data = new byte[32];
        var w = new BigEndianWriter(data);
        w.WriteUInt32(0x00030000);              // version 3.0: no glyph names stored
        w.WriteUInt32(0);                       // italicAngle
        w.WriteInt16(0);                        // underlinePosition
        w.WriteInt16(0);                        // underlineThickness
        w.WriteUInt32(1);                       // isFixedPitch: every advance is identical
        w.WriteUInt32(0);                       // minMemType42
        w.WriteUInt32(0);                       // maxMemType42
        w.WriteUInt32(0);                       // minMemType1
        w.WriteUInt32(0);                       // maxMemType1
        return data;
    }

    private static byte[] BuildOs2()
    {
        var data = new byte[96];                // version 4
        var w = new BigEndianWriter(data);
        w.WriteUInt16(4);                       // version
        w.WriteInt16(AdvanceWidth);             // xAvgCharWidth
        w.WriteUInt16(400);                     // usWeightClass: normal
        w.WriteUInt16(5);                       // usWidthClass: medium
        w.WriteUInt16(0);                       // fsType: installable embedding
        w.WriteInt16(0);                        // ySubscriptXSize
        w.WriteInt16(0);                        // ySubscriptYSize
        w.WriteInt16(0);                        // ySubscriptXOffset
        w.WriteInt16(0);                        // ySubscriptYOffset
        w.WriteInt16(0);                        // ySuperscriptXSize
        w.WriteInt16(0);                        // ySuperscriptYSize
        w.WriteInt16(0);                        // ySuperscriptXOffset
        w.WriteInt16(0);                        // ySuperscriptYOffset
        w.WriteInt16(0);                        // yStrikeoutSize
        w.WriteInt16(0);                        // yStrikeoutPosition
        w.WriteInt16(0);                        // sFamilyClass
        for (var i = 0; i < 10; i++)            // panose
            w.WriteByte(0);
        w.WriteUInt32(0);                       // ulUnicodeRange1..4
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteTag("MFRG");                     // achVendID
        w.WriteUInt16(0x0040);                  // fsSelection: REGULAR
        w.WriteUInt16(0);                       // usFirstCharIndex
        w.WriteUInt16(0xFFFF);                  // usLastCharIndex
        w.WriteInt16(Ascender);                 // sTypoAscender
        w.WriteInt16(Descender);                // sTypoDescender
        w.WriteInt16(0);                        // sTypoLineGap
        w.WriteUInt16(Ascender);                // usWinAscent
        w.WriteUInt16((ushort)(-Descender));    // usWinDescent
        w.WriteUInt32(0);                       // ulCodePageRange1
        w.WriteUInt32(0);                       // ulCodePageRange2
        w.WriteInt16(CapHeight);                // sxHeight, borrowing cap height
        w.WriteInt16(CapHeight);                // sCapHeight
        w.WriteUInt16(0);                       // usDefaultChar
        w.WriteUInt16(0);                       // usBreakChar
        w.WriteUInt16(1);                       // usMaxContext
        return data;
    }

    private static byte[] BuildName()
    {
        // Windows platform, UTF-16BE, US English. Four records is the conventional minimum.
        (ushort NameId, string Value)[] records =
        [
            (1, PostScriptName),                // family
            (2, "Regular"),                     // subfamily
            (4, PostScriptName),                // full name
            (6, PostScriptName),                // PostScript name
        ];

        var strings = records.Select(r => Utf16Be(r.Value)).ToArray();
        var storageOffset = 6 + records.Length * 12;
        var data = new byte[storageOffset + strings.Sum(s => s.Length)];
        var w = new BigEndianWriter(data);

        w.WriteUInt16(0);                       // format 0
        w.WriteUInt16((ushort)records.Length);
        w.WriteUInt16((ushort)storageOffset);

        var stringOffset = 0;
        for (var i = 0; i < records.Length; i++)
        {
            w.WriteUInt16(3);                   // platformID: Windows
            w.WriteUInt16(1);                   // encodingID: Unicode BMP
            w.WriteUInt16(0x0409);              // languageID: en-US
            w.WriteUInt16(records[i].NameId);
            w.WriteUInt16((ushort)strings[i].Length);
            w.WriteUInt16((ushort)stringOffset);
            stringOffset += strings[i].Length;
        }

        var cursor = storageOffset;
        foreach (var s in strings)
        {
            s.CopyTo(data.AsSpan(cursor));
            cursor += s.Length;
        }

        return data;
    }

    private static byte[] Utf16Be(string value)
    {
        var bytes = new byte[value.Length * 2];
        for (var i = 0; i < value.Length; i++)
        {
            bytes[i * 2] = (byte)(value[i] >> 8);
            bytes[i * 2 + 1] = (byte)(value[i] & 0xFF);
        }
        return bytes;
    }

    private static int Align4(int length) => (length + 3) & ~3;

    /// <summary>Sum of a table's contents as big-endian uint32s, zero-padded to a 4-byte multiple.</summary>
    private static uint TableChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var whole = data.Length / 4 * 4;
        for (var i = 0; i < whole; i += 4)
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));

        if (whole < data.Length)
        {
            uint tail = 0;
            for (var i = 0; i < 4; i++)
            {
                var index = whole + i;
                tail = (tail << 8) | (index < data.Length ? data[index] : 0u);
            }
            sum = unchecked(sum + tail);
        }

        return sum;
    }

    /// <summary>
    /// Checksum of the entire file, taken with checkSumAdjustment still zero, as the spec requires.
    /// The font is already 4-byte aligned by construction.
    /// </summary>
    private static uint WholeFontChecksum(ReadOnlySpan<byte> font) => TableChecksum(font);

    /// <summary>Sequential big-endian writer over a pre-sized buffer.</summary>
    private ref struct BigEndianWriter(Span<byte> buffer)
    {
        private readonly Span<byte> _buffer = buffer;
        private int _position = 0;

        public void WriteByte(byte value) => _buffer[_position++] = value;

        public void WriteUInt16(ushort value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(_buffer[_position..], value);
            _position += 2;
        }

        public void WriteUInt16(int value) => WriteUInt16(checked((ushort)value));

        public void WriteInt16(int value)
        {
            BinaryPrimitives.WriteInt16BigEndian(_buffer[_position..], checked((short)value));
            _position += 2;
        }

        public void WriteUInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_buffer[_position..], value);
            _position += 4;
        }

        public void WriteInt64(long value)
        {
            BinaryPrimitives.WriteInt64BigEndian(_buffer[_position..], value);
            _position += 8;
        }

        public void WriteTag(string tag)
        {
            if (tag.Length != 4)
                throw new ArgumentException($"A table tag must be four characters, got '{tag}'.", nameof(tag));
            foreach (var c in tag)
                WriteByte((byte)c);
        }
    }
}
