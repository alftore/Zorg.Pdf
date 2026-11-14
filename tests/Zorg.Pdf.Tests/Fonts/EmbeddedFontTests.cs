using System;
using System.Buffers.Binary;
using Xunit;
using Zorg.Pdf.Fonts;

namespace Zorg.Pdf.Tests.Fonts;

public sealed class EmbeddedFontTests
{
    [Fact]
    public void EncodesTextToGlyphsAndMeasuresIt()
    {
        var font = new EmbeddedFont(TrueTypeFont.Parse(TestFonts.Require()));

        ushort[] glyphs = font.Encode("Hei på deg");
        Assert.Equal(10, glyphs.Length);
        Assert.Equal(glyphs[3], font.Encode(" ")[0]);
        Assert.Equal(8, font.GlyphsUsed); // H e i space p å d g
        double width = font.Measure(glyphs, 12);
        Assert.InRange(width, 30, 90);
        Assert.InRange(font.LineHeight, 1.0, 1.4);
    }

    [Fact]
    public void SupplementaryCharactersCountAsOneCharacter()
    {
        var font = new EmbeddedFont(TrueTypeFont.Parse(TestFonts.Require()));

        // U+F0000 (supplementary private use) is in no text font; DejaVu Sans even has emoji. The index reported is in
        // UTF-16 units, where the surrogate pair starts.
        PdfException error = Assert.Throws<PdfException>(() => font.Encode("ab\U000F0000c"));
        Assert.Contains("U+F0000", error.Message, StringComparison.Ordinal);
        Assert.Contains("at index 2", error.Message, StringComparison.Ordinal);
        Assert.Contains(font.Font.PostScriptName, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x0002, "doesn't allow embedding")]
    [InlineData(0x0100, "forbids subsetting")]
    [InlineData(0x0200, "forbids subsetting")]
    public void HonoursTheFontsEmbeddingPermissions(int fsType, string reason)
    {
        byte[] file = (byte[])TestFonts.Require().Clone();
        int os2 = TableOffset(file, "OS/2");
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(os2 + 8), (ushort)fsType);

        PdfException error = Assert.Throws<PdfException>(() => new EmbeddedFont(TrueTypeFont.Parse(file)));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    private static int TableOffset(byte[] file, string tag)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int entry = 12 + (i * 16);
            if (System.Text.Encoding.ASCII.GetString(file, entry, 4) == tag)
            {
                return (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry + 8));
            }
        }

        Assert.Skip($"The test font has no '{tag}' table.");
        return -1;
    }
}
