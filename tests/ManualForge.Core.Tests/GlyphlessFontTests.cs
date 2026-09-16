using System.Buffers.Binary;
using ManualForge.Core.Text;

namespace ManualForge.Core.Tests;

/// <summary>
/// Structural checks on the generated font program. A malformed font does not fail loudly — the
/// reader silently falls back to a default and the text layer's metrics quietly stop matching what
/// the writer assumed — so it is worth asserting the file really is a well-formed TrueType.
/// </summary>
public class GlyphlessFontTests
{
    private static Dictionary<string, (int Offset, int Length)> ReadTableDirectory(byte[] font)
    {
        Assert.Equal(0x00010000u, BinaryPrimitives.ReadUInt32BigEndian(font));
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));

        var tables = new Dictionary<string, (int, int)>();
        for (var i = 0; i < numTables; i++)
        {
            var entry = font.AsSpan(12 + i * 16);
            var tag = System.Text.Encoding.ASCII.GetString(entry[..4]);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            tables[tag] = (offset, length);
        }
        return tables;
    }

    [Fact]
    public void CarriesEveryTableAReaderNeeds()
    {
        var tables = ReadTableDirectory(GlyphlessTrueTypeFont.Build(40));

        foreach (var required in new[] { "head", "hhea", "hmtx", "maxp", "loca", "glyf", "cmap", "name", "post", "OS/2" })
            Assert.True(tables.ContainsKey(required), $"Missing the {required} table.");
    }

    [Fact]
    public void TableDirectoryIsSortedByTagAsTheSpecRequires()
    {
        var font = GlyphlessTrueTypeFont.Build(40);
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));

        var tags = Enumerable.Range(0, numTables)
            .Select(i => System.Text.Encoding.ASCII.GetString(font.AsSpan(12 + i * 16, 4)))
            .ToArray();

        Assert.Equal(tags.Order(StringComparer.Ordinal).ToArray(), tags);
    }

    [Fact]
    public void HeadDeclaresTheAgreedEmSizeAndLongLocaFormat()
    {
        var font = GlyphlessTrueTypeFont.Build(40);
        var head = ReadTableDirectory(font)["head"];

        Assert.Equal(0x5F0F3CF5u, BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(head.Offset + 12)));
        Assert.Equal(GlyphlessTrueTypeFont.UnitsPerEm, BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(head.Offset + 18)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16BigEndian(font.AsSpan(head.Offset + 50)));   // long loca
    }

    [Fact]
    public void EveryGlyphAdvancesByExactlyHalfAnEm()
    {
        // This is the property the horizontal-scaling calculation depends on: if it stops being
        // true, every word in every document ends up the wrong width.
        var font = GlyphlessTrueTypeFont.Build(40);
        var tables = ReadTableDirectory(font);

        var numberOfHMetrics = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(tables["hhea"].Offset + 34));
        Assert.Equal(1, numberOfHMetrics);   // one metric, inherited by every later glyph

        var advance = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(tables["hmtx"].Offset));
        Assert.Equal(GlyphlessTrueTypeFont.AdvanceWidth, advance);
        Assert.Equal(GlyphlessTrueTypeFont.UnitsPerEm / 2, advance);
    }

    [Fact]
    public void MaxpAgreesWithTheRequestedGlyphCount()
    {
        var font = GlyphlessTrueTypeFont.Build(137);
        var maxp = ReadTableDirectory(font)["maxp"];

        Assert.Equal(137, BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(maxp.Offset + 4)));
    }

    [Fact]
    public void EveryGlyphHasZeroContoursAndARealBoundingBox()
    {
        const int glyphs = 12;
        var font = GlyphlessTrueTypeFont.Build(glyphs);
        var tables = ReadTableDirectory(font);
        var loca = tables["loca"];
        var glyf = tables["glyf"];

        for (var i = 0; i < glyphs; i++)
        {
            var start = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(loca.Offset + i * 4));
            var end = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(loca.Offset + (i + 1) * 4));
            Assert.Equal(10, end - start);

            var record = font.AsSpan(glyf.Offset + start);
            Assert.Equal(0, BinaryPrimitives.ReadInt16BigEndian(record));                  // no contours: draws nothing
            Assert.Equal(GlyphlessTrueTypeFont.GlyphYMin, BinaryPrimitives.ReadInt16BigEndian(record[4..]));
            Assert.Equal(GlyphlessTrueTypeFont.GlyphYMax, BinaryPrimitives.ReadInt16BigEndian(record[8..]));
        }
    }

    [Fact]
    public void ChecksumAdjustmentSatisfiesTheSpecFormula()
    {
        var font = GlyphlessTrueTypeFont.Build(64);
        var head = ReadTableDirectory(font)["head"];

        // Zeroing checkSumAdjustment and summing the whole file must give the magic constant back.
        var copy = (byte[])font.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(head.Offset + 8), 0);

        uint sum = 0;
        for (var i = 0; i + 4 <= copy.Length; i += 4)
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(i)));

        var stored = BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(head.Offset + 8));
        Assert.Equal(unchecked(0xB1B0AFBAu - sum), stored);
    }

    [Fact]
    public void FontIsFourByteAlignedThroughout()
    {
        var font = GlyphlessTrueTypeFont.Build(40);
        Assert.Equal(0, font.Length % 4);

        foreach (var (_, (offset, _)) in ReadTableDirectory(font))
            Assert.Equal(0, offset % 4);
    }

    [Fact]
    public void StaysSmallEvenWithAFullCharacterSet()
    {
        // A whole manual rarely uses more than a few hundred distinct characters. The overlay
        // should never become a meaningful part of the file size.
        var font = GlyphlessTrueTypeFont.Build(512);
        Assert.True(font.Length < 16 * 1024, $"Font grew to {font.Length} bytes.");
    }

    [Fact]
    public void RefusesAnEmptyGlyphSet()
        => Assert.Throws<ArgumentOutOfRangeException>(() => GlyphlessTrueTypeFont.Build(0));
}
