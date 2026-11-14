using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Zorg.Pdf.Fonts;

namespace Zorg.Pdf.Tests.Fonts;

public sealed class TrueTypeFontTests
{
    [Fact]
    public void ReadsMetricsAndCharacterMap()
    {
        TrueTypeFont font = TrueTypeFont.Parse(TestFonts.Require());

        Assert.InRange(font.UnitsPerEm, 1000, 4096);
        Assert.True(font.GlyphCount > 200);
        Assert.True(font.MappedCharacterCount > 200);
        Assert.True(font.Ascender > 0 && font.Descender < 0);
        Assert.True(font.LayoutAscender > 0 && font.LayoutDescender < 0);
        Assert.NotEqual("Font", font.PostScriptName);
        Assert.DoesNotContain(' ', font.PostScriptName);

        ushort a = font.GlyphFor('A');
        ushort space = font.GlyphFor(' ');
        Assert.NotEqual(0, a);
        Assert.NotEqual(0, font.GlyphFor('ø'));
        Assert.Equal(0, font.GlyphFor(0x10FFFD)); // Private use, never in a text font.
        Assert.True(font.AdvanceWidth(a) > 0);
        Assert.False(font.GlyphData(a).IsEmpty);
        Assert.True(font.GlyphData(space).IsEmpty);
    }

    [Theory]
    [InlineData(new byte[] { 0x4F, 0x54, 0x54, 0x4F }, "CFF")]
    [InlineData(new byte[] { 0x74, 0x74, 0x63, 0x66 }, "collection")]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46 }, "isn't a TrueType font")]
    public void RefusesOtherFormatsWithAReason(byte[] signature, string reason)
    {
        byte[] file = new byte[64];
        signature.CopyTo(file, 0);
        PdfException error = Assert.Throws<PdfException>(() => TrueTypeFont.Parse(file));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DamageGivesPdfExceptionOrAFontNeverAnythingElse()
    {
        byte[] original = TestFonts.Require();
        var random = new Random(20261114);
        int parsed = 0;
        int refused = 0;

        // Truncations, including inside the table directory.
        foreach (int length in new[] { 0, 5, 11, 12, 30, 200, 2000, original.Length / 3, original.Length - 1 })
        {
            Count(Attempt(original.AsSpan(0, length).ToArray()));
        }

        // Damage to the directory and to the start of each table, where the counts and offsets live.
        List<(int Offset, int Length)> tables = Tables(original);
        for (int round = 0; round < 300; round++)
        {
            byte[] damaged = (byte[])original.Clone();
            (int offset, int length) = round % 4 == 0 ? (0, 12 + (tables.Count * 16)) : tables[random.Next(tables.Count)];
            int span = Math.Min(length, 128);
            for (int hits = random.Next(1, 6); hits > 0 && span > 0; hits--)
            {
                damaged[offset + random.Next(span)] = (byte)(random.Next(4) switch { 0 => 0x00, 1 => 0xFF, 2 => 0x7F, _ => random.Next(256) });
            }

            Count(Attempt(damaged));
        }

        Assert.True(parsed > 0 && refused > 0, $"parsed {parsed}, refused {refused}: the damage should produce both.");

        void Count(bool ok)
        {
            if (ok)
            {
                parsed++;
            }
            else
            {
                refused++;
            }
        }
    }

    /// <summary>True if the font parsed and a subset could be written; false for a PdfException. Anything else fails the test.</summary>
    private static bool Attempt(byte[] file)
    {
        try
        {
            TrueTypeFont font = TrueTypeFont.Parse(file);
            var embedded = new EmbeddedFont(font);
            ushort[] glyphs = [.. Enumerable.Range(0, Math.Min(font.GlyphCount, 300)).Select(g => (ushort)g)];
            SortedSet<ushort> closed = TrueTypeSubsetter.Close(font, glyphs);
            TrueTypeSubsetter.Subset(font, closed);
            _ = embedded.LineHeight;
            return true;
        }
        catch (PdfException)
        {
            return false;
        }
    }

    private static List<(int Offset, int Length)> Tables(byte[] file)
    {
        var tables = new List<(int, int)>();
        int count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int entry = 12 + (i * 16);
            tables.Add(((int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry + 8)), (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry + 12))));
        }

        return tables;
    }
}
