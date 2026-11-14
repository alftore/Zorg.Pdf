using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Zorg.Pdf.Fonts;

/// <summary>
/// Builds a smaller TrueType font holding only the glyphs a document uses, plus the glyphs composite glyphs are built
/// from. Glyph IDs are kept, so the PDF can use <c>/CIDToGIDMap /Identity</c>: unused glyphs stay in the tables with
/// no outline, which costs 4 bytes each in <c>loca</c> and <c>hmtx</c>.
/// </summary>
/// <remarks>
/// Only the tables a PDF reader needs for a CIDFontType2 are kept (ISO 32000-1, 9.9): <c>head</c>, <c>hhea</c>,
/// <c>maxp</c>, <c>hmtx</c>, <c>loca</c>, <c>glyf</c>, and the hinting tables <c>cvt</c>, <c>fpgm</c> and <c>prep</c>
/// when present. Character maps, names and layout tables are dropped; the PDF carries the text mapping itself.
/// </remarks>
internal static class TrueTypeSubsetter
{
    private static readonly string[] KeptTables = ["cvt ", "fpgm", "glyf", "head", "hhea", "hmtx", "loca", "maxp", "prep"];

    /// <summary>Adds every glyph that a composite glyph in <paramref name="glyphs"/> refers to, recursively.</summary>
    public static SortedSet<ushort> Close(TrueTypeFont font, IEnumerable<ushort> glyphs)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(glyphs);

        var closed = new SortedSet<ushort>();
        var pending = new Stack<ushort>(glyphs);
        pending.Push(0); // .notdef is always kept.
        while (pending.Count > 0)
        {
            ushort glyph = pending.Pop();
            if (glyph >= font.GlyphCount || !closed.Add(glyph))
            {
                continue;
            }

            foreach (ushort component in Components(font.GlyphData(glyph)))
            {
                pending.Push(component);
            }
        }

        return closed;
    }

    /// <summary>Writes the subset font file.</summary>
    public static byte[] Subset(TrueTypeFont font, SortedSet<ushort> glyphs)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(glyphs);

        // New glyf and loca (always the long format, so no glyph needs to be 2-byte aligned).
        using var glyf = new MemoryStream();
        byte[] loca = new byte[(font.GlyphCount + 1) * 4];
        for (int g = 0; g < font.GlyphCount; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(g * 4), (uint)glyf.Length);
            if (glyphs.Contains((ushort)g))
            {
                glyf.Write(font.GlyphData(g));
                Pad(glyf);
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(font.GlyphCount * 4), (uint)glyf.Length);

        byte[] head = font.RawTable("head").ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0); // checkSumAdjustment, set at the end.
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1); // indexToLocFormat: long.

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string tag in KeptTables)
        {
            byte[]? data = tag switch
            {
                "glyf" => glyf.ToArray(),
                "loca" => loca,
                "head" => head,
                _ => font.HasTable(tag) ? font.RawTable(tag).ToArray() : null,
            };
            if (data is not null)
            {
                tables.Add(tag, data);
            }
        }

        byte[] file = Assemble(tables);
        uint adjustment = unchecked(0xB1B0AFBA - Checksum(file));
        int headOffset = TableOffset(file, "head");
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(headOffset + 8), adjustment);
        return file;
    }

    /// <summary>The glyph IDs a composite glyph is made from. Empty for a simple glyph.</summary>
    internal static List<ushort> Components(ReadOnlySpan<byte> glyph)
    {
        var components = new List<ushort>();
        if (glyph.Length < 10 || BinaryPrimitives.ReadInt16BigEndian(glyph) >= 0)
        {
            return components;
        }

        var data = new FontData(glyph, "glyf");
        int at = 10;
        ushort flags;
        do
        {
            flags = data.U16(at);
            components.Add(data.U16(at + 2));
            at += 4;
            at += (flags & 0x0001) != 0 ? 4 : 2; // ARG_1_AND_2_ARE_WORDS
            if ((flags & 0x0008) != 0)
            {
                at += 2; // WE_HAVE_A_SCALE
            }
            else if ((flags & 0x0040) != 0)
            {
                at += 4; // WE_HAVE_AN_X_AND_Y_SCALE
            }
            else if ((flags & 0x0080) != 0)
            {
                at += 8; // WE_HAVE_A_TWO_BY_TWO
            }

            if (components.Count > 1024)
            {
                throw data.Damaged("a composite glyph has more than 1024 components");
            }
        }
        while ((flags & 0x0020) != 0); // MORE_COMPONENTS

        return components;
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        int count = tables.Count;
        int selector = (int)Math.Log2(count);
        int searchRange = (1 << selector) * 16;

        int size = 12 + (count * 16);
        foreach (byte[] table in tables.Values)
        {
            size += (table.Length + 3) & ~3;
        }

        byte[] file = new byte[size];
        Span<byte> span = file;
        BinaryPrimitives.WriteUInt32BigEndian(span, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)selector);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)((count * 16) - searchRange));

        int entry = 12;
        int offset = 12 + (count * 16);
        foreach ((string tag, byte[] table) in tables)
        {
            table.CopyTo(span[offset..]);
            BinaryPrimitives.WriteUInt32BigEndian(span[entry..], TrueTypeFont.Tag(tag));
            BinaryPrimitives.WriteUInt32BigEndian(span[(entry + 4)..], Checksum(span.Slice(offset, (table.Length + 3) & ~3)));
            BinaryPrimitives.WriteUInt32BigEndian(span[(entry + 8)..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(span[(entry + 12)..], (uint)table.Length);
            entry += 16;
            offset += (table.Length + 3) & ~3;
        }

        return file;
    }

    private static int TableOffset(byte[] file, string tag)
    {
        uint wanted = TrueTypeFont.Tag(tag);
        int count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int entry = 12 + (i * 16);
            if (BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry)) == wanted)
            {
                return (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry + 8));
            }
        }

        throw new InvalidOperationException($"The subset has no '{tag}' table.");
    }

    /// <summary>The sum of big-endian 32-bit words, with the data padded to a multiple of 4.</summary>
    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int whole = data.Length & ~3;
        for (int i = 0; i < whole; i += 4)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));
        }

        if (whole < data.Length)
        {
            Span<byte> last = stackalloc byte[4];
            last.Clear();
            data[whole..].CopyTo(last);
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(last));
        }

        return sum;
    }

    private static void Pad(MemoryStream stream)
    {
        while (stream.Length % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }
}
