using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Zorg.Pdf.Fonts;

namespace Zorg.Pdf.Tests.Fonts;

public sealed class TrueTypeSubsetterTests
{
    [Fact]
    public void KeepsUsedGlyphsAndEmptiesTheRest()
    {
        TrueTypeFont font = TrueTypeFont.Parse(TestFonts.Require());
        ushort a = font.GlyphFor('A');
        ushort b = font.GlyphFor('B');
        SortedSet<ushort> glyphs = TrueTypeSubsetter.Close(font, [a]);
        byte[] subset = TrueTypeSubsetter.Subset(font, glyphs);

        Dictionary<string, (int Offset, int Length)> tables = Directory(subset);
        Assert.Equal(["glyf", "head", "hhea", "hmtx", "loca", "maxp"], tables.Keys.Where(t => t is not ("cvt " or "fpgm" or "prep")).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("cmap", tables.Keys);
        Assert.True(subset.Length < font.RawTable("glyf").Length / 4, "The subset should be far smaller than the font.");

        AssertSameGlyph(font.GlyphData(a), Glyph(subset, tables, a));
        AssertSameGlyph(font.GlyphData(0), Glyph(subset, tables, 0));
        Assert.Equal(0, Glyph(subset, tables, b).Length);
    }

    [Fact]
    public void ChecksumsAreValid()
    {
        TrueTypeFont font = TrueTypeFont.Parse(TestFonts.Require());
        byte[] subset = TrueTypeSubsetter.Subset(font, TrueTypeSubsetter.Close(font, [font.GlyphFor('x')]));

        // The whole file, including head.checkSumAdjustment, sums to the magic number.
        Assert.Equal(0xB1B0AFBAu, Sum(subset));

        int count = BinaryPrimitives.ReadUInt16BigEndian(subset.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int entry = 12 + (i * 16);
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(subset.AsSpan(entry + 4));
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(subset.AsSpan(entry + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(subset.AsSpan(entry + 12));
            byte[] table = subset.AsSpan(offset, length).ToArray();
            if (BinaryPrimitives.ReadUInt32BigEndian(subset.AsSpan(entry)) == 0x68656164) // 'head' is summed with the adjustment zeroed.
            {
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8), 0);
            }

            Assert.Equal(stored, Sum(table));
        }
    }

    [Fact]
    public void CompositeGlyphsBringTheirComponents()
    {
        TrueTypeFont font = TrueTypeFont.Parse(TestFonts.Require());
        int composite = Enumerable.Range(1, font.GlyphCount - 1).FirstOrDefault(g => TrueTypeSubsetter.Components(font.GlyphData(g)).Count > 0);
        Assert.True(composite > 0, "The test font should have a composite glyph (accented letters usually are).");

        List<ushort> components = TrueTypeSubsetter.Components(font.GlyphData(composite));
        SortedSet<ushort> closed = TrueTypeSubsetter.Close(font, [(ushort)composite]);

        Assert.Contains((ushort)composite, closed);
        Assert.All(components, c => Assert.Contains(c, closed));
        Assert.Contains((ushort)0, closed);
    }

    private static ReadOnlySpan<byte> Glyph(byte[] subset, Dictionary<string, (int Offset, int Length)> tables, int glyph)
    {
        int loca = tables["loca"].Offset;
        int start = (int)BinaryPrimitives.ReadUInt32BigEndian(subset.AsSpan(loca + (glyph * 4)));
        int end = (int)BinaryPrimitives.ReadUInt32BigEndian(subset.AsSpan(loca + ((glyph + 1) * 4)));
        return subset.AsSpan(tables["glyf"].Offset + start, end - start);
    }

    /// <summary>The subset pads each glyph with up to 3 zero bytes, so it can be a little longer than the original.</summary>
    private static void AssertSameGlyph(ReadOnlySpan<byte> original, ReadOnlySpan<byte> subset)
    {
        Assert.InRange(subset.Length - original.Length, 0, 3);
        Assert.True(subset[..original.Length].SequenceEqual(original));
        Assert.True(subset[original.Length..].IndexOfAnyExcept((byte)0) < 0);
    }

    private static Dictionary<string, (int Offset, int Length)> Directory(byte[] file)
    {
        var tables = new Dictionary<string, (int, int)>();
        int count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int entry = 12 + (i * 16);
            string tag = System.Text.Encoding.ASCII.GetString(file, entry, 4);
            tables.Add(tag, ((int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry + 8)), (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry + 12))));
        }

        return tables;
    }

    private static uint Sum(byte[] data)
    {
        uint sum = 0;
        byte[] padded = new byte[(data.Length + 3) & ~3];
        data.CopyTo(padded, 0);
        for (int i = 0; i < padded.Length; i += 4)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(padded.AsSpan(i)));
        }

        return sum;
    }
}
