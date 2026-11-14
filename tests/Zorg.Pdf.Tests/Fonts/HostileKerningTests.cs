using System;
using System.Buffers.Binary;
using Xunit;
using Zorg.Pdf.Fonts;

namespace Zorg.Pdf.Tests.Fonts;

/// <summary>Targeted damage to GPOS that random mutation is unlikely to reach.</summary>
public sealed class HostileKerningTests
{
    [Theory]
    [InlineData(0xFFFFFFF0u)] // Past int.MaxValue: once an OverflowException from a checked cast.
    [InlineData(0x7FFFFFF0u)] // Inside int but past the table.
    public void AnExtensionOffsetPastTheTableIsAPdfException(uint offset)
    {
        byte[] file = (byte[])TestFonts.Require().Clone();
        (int lookup, int subtable) = FirstKernLookup(file);

        // Turn the lookup into an extension lookup whose only subtable points far away.
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(lookup), 9);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(subtable), 1); // posFormat
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(subtable + 2), 2); // extensionLookupType: pair adjustment
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(subtable + 4), offset);

        PdfException error = Assert.Throws<PdfException>(() => TrueTypeFont.Parse(file));
        Assert.Contains("GPOS", error.Message, StringComparison.Ordinal);
        Assert.Contains("extension", error.Message, StringComparison.Ordinal);
    }

    /// <summary>File offsets of the first lookup used by a 'kern' feature, and of its first subtable.</summary>
    private static (int Lookup, int Subtable) FirstKernLookup(byte[] file)
    {
        int gpos = -1;
        int count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int entry = 12 + (i * 16);
            if (System.Text.Encoding.ASCII.GetString(file, entry, 4) == "GPOS")
            {
                gpos = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry + 8));
            }
        }

        if (gpos < 0)
        {
            Assert.Skip("The test font has no GPOS table.");
        }

        int U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(at));
        int features = gpos + U16(gpos + 6);
        int lookups = gpos + U16(gpos + 8);
        for (int f = 0; f < U16(features); f++)
        {
            int record = features + 2 + (f * 6);
            if (System.Text.Encoding.ASCII.GetString(file, record, 4) == "kern" && U16(features + U16(record + 4) + 2) > 0)
            {
                int index = U16(features + U16(record + 4) + 4);
                int lookup = lookups + U16(lookups + 2 + (index * 2));
                return (lookup, lookup + U16(lookup + 6));
            }
        }

        Assert.Skip("The test font has no kern feature.");
        return default;
    }
}
