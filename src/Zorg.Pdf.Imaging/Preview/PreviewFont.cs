using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Zorg.Imaging.Drawing;

namespace Zorg.Pdf.Imaging.Preview;

/// <summary>
/// A Type0 font as Zorg.Pdf writes it: Identity-H, a CIDFontType2 whose CIDs are glyph IDs, its TrueType program in
/// <c>FontFile2</c>, and widths in <c>/W</c>. Glyph outlines are read from <c>glyf</c> and kept, in font units.
/// </summary>
internal sealed class PreviewFont
{
    private const int MaxCompositeDepth = 8;

    private readonly byte[] _program;
    private readonly int[] _loca;
    private readonly int _glyf;
    private readonly int _glyfLength;
    private readonly Dictionary<int, double> _widths;
    private readonly double _defaultWidth;
    private readonly Dictionary<int, Shape?> _shapes = [];

    private PreviewFont(byte[] program, int[] loca, int glyf, int glyfLength, int unitsPerEm, Dictionary<int, double> widths, double defaultWidth) =>
        (_program, _loca, _glyf, _glyfLength, UnitsPerEm, _widths, _defaultWidth) = (program, loca, glyf, glyfLength, unitsPerEm, widths, defaultWidth);

    public int UnitsPerEm { get; }

    public static PreviewFont Load(PreviewFile file, PdfDictionary font)
    {
        if (file.Resolve(font["Subtype"]) is not PdfName { Value: "Type0" } || file.Resolve(font["Encoding"]) is not PdfName { Value: "Identity-H" }
            || file.Resolve(font["DescendantFonts"]) is not PdfArray { Items.Count: 1 } descendants || file.Dictionary(descendants.Items[0]) is not { } cid
            || file.Resolve(cid["Subtype"]) is not PdfName { Value: "CIDFontType2" })
        {
            throw new PdfPreviewException("A font isn't a Type0 TrueType font with Identity-H encoding, as Zorg.Pdf writes: previews read only files Zorg.Pdf wrote.");
        }

        if (file.Resolve(cid["CIDToGIDMap"]) is not (PdfName { Value: "Identity" } or PdfNull))
        {
            throw new PdfPreviewException("A font maps CIDs to glyphs through a table, which Zorg.Pdf never writes.");
        }

        PdfDictionary descriptor = file.Dictionary(cid["FontDescriptor"]) ?? throw PdfPreviewException.Malformed("A font has no descriptor.");
        PdfStream stream = file.Stream(descriptor["FontFile2"]) ?? throw new PdfPreviewException("A font isn't embedded as TrueType (FontFile2).");
        byte[] program = file.Decode(stream);

        double defaultWidth = file.Resolve(cid["DW"]) is PdfNumber dw ? dw.Value : 1000;
        var widths = new Dictionary<int, double>();
        if (file.Resolve(cid["W"]) is PdfArray w)
        {
            for (int i = 0; i < w.Items.Count;)
            {
                int first = file.Resolve(w.Items[i]).Integer;
                PdfValue next = file.Resolve(i + 1 < w.Items.Count ? w.Items[i + 1] : null);
                if (next is PdfArray list)
                {
                    for (int j = 0; j < list.Items.Count && j < 65536; j++)
                    {
                        widths[first + j] = file.Resolve(list.Items[j]).Number;
                    }

                    i += 2;
                }
                else
                {
                    int last = next.Integer;
                    double width = file.Resolve(i + 2 < w.Items.Count ? w.Items[i + 2] : null).Number;
                    for (int g = first; g <= last && g - first < 65536; g++)
                    {
                        widths[g] = width;
                    }

                    i += 3;
                }
            }
        }

        (int head, _) = Table(program, "head"u8);
        (int maxp, _) = Table(program, "maxp"u8);
        (int locaOffset, int locaLength) = Table(program, "loca"u8);
        (int glyf, int glyfLength) = Table(program, "glyf"u8);
        if (head < 0 || maxp < 0 || locaOffset < 0 || glyf < 0 || head + 54 > program.Length || maxp + 6 > program.Length)
        {
            throw PdfPreviewException.Malformed("An embedded font lacks a TrueType table it needs (head, maxp, loca or glyf).");
        }

        int unitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(program.AsSpan(head + 18));
        bool longLoca = BinaryPrimitives.ReadInt16BigEndian(program.AsSpan(head + 50)) == 1;
        int glyphs = BinaryPrimitives.ReadUInt16BigEndian(program.AsSpan(maxp + 4));
        int[] loca = new int[glyphs + 1];
        for (int g = 0; g <= glyphs; g++)
        {
            int at = locaOffset + (g * (longLoca ? 4 : 2));
            if (at + (longLoca ? 4 : 2) > locaOffset + locaLength)
            {
                throw PdfPreviewException.Malformed("An embedded font's loca table is too short.");
            }

            loca[g] = longLoca ? (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(program.AsSpan(at)), int.MaxValue) : BinaryPrimitives.ReadUInt16BigEndian(program.AsSpan(at)) * 2;
        }

        return new PreviewFont(program, loca, glyf, glyfLength, unitsPerEm is >= 16 and <= 16384 ? unitsPerEm : 1000, widths, defaultWidth);
    }

    /// <summary>The advance of a glyph in thousandths of the font size.</summary>
    public double Width(int glyph) => _widths.TryGetValue(glyph, out double w) ? w : _defaultWidth;

    /// <summary>The glyph's outline in font units (y up), or null for an empty glyph.</summary>
    public Shape? Outline(int glyph)
    {
        if (!_shapes.TryGetValue(glyph, out Shape? shape))
        {
            var built = new Shape();
            Append(built, glyph, 1, 0, 0, 1, 0, 0, 0);
            shape = built.IsEmpty ? null : built;
            _shapes[glyph] = shape;
        }

        return shape;
    }

    private static (int Offset, int Length) Table(byte[] font, ReadOnlySpan<byte> tag)
    {
        if (font.Length < 12)
        {
            return (-1, 0);
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int i = 0; i < count && 12 + ((i + 1) * 16) <= font.Length; i++)
        {
            ReadOnlySpan<byte> entry = font.AsSpan(12 + (i * 16), 16);
            if (entry[..4].SequenceEqual(tag))
            {
                long offset = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]), length = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
                return offset + length <= font.Length ? ((int)offset, (int)length) : (-1, 0);
            }
        }

        return (-1, 0);
    }

    /// <summary>Adds a glyph's contours, transformed by (a b c d e f), following composite components.</summary>
    private void Append(Shape shape, int glyph, double a, double b, double c, double d, double e, double f, int depth)
    {
        if (glyph < 0 || glyph + 1 >= _loca.Length || depth > MaxCompositeDepth)
        {
            return;
        }

        int start = _loca[glyph], end = _loca[glyph + 1];
        if (end <= start || end > _glyfLength)
        {
            return; // An empty glyph, such as a space, or one blanked by subsetting.
        }

        ReadOnlySpan<byte> data = _program.AsSpan(_glyf + start, end - start);
        if (data.Length < 10)
        {
            return;
        }

        int contours = BinaryPrimitives.ReadInt16BigEndian(data);
        if (contours >= 0)
        {
            Simple(shape, data, contours, a, b, c, d, e, f);
        }
        else
        {
            Composite(shape, data, a, b, c, d, e, f, depth);
        }
    }

    private static void Simple(Shape shape, ReadOnlySpan<byte> data, int contours, double a, double b, double c, double d, double e, double f)
    {
        int at = 10;
        if (data.Length < at + (contours * 2) + 2)
        {
            return;
        }

        int[] ends = new int[contours];
        for (int i = 0; i < contours; i++)
        {
            ends[i] = BinaryPrimitives.ReadUInt16BigEndian(data[(at + (i * 2))..]);
        }

        at += contours * 2;
        int points = contours == 0 ? 0 : ends[^1] + 1;
        int instructions = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
        at += 2 + instructions;
        if (points == 0 || at > data.Length)
        {
            return;
        }

        byte[] flags = new byte[points];
        for (int i = 0; i < points && at < data.Length;)
        {
            byte flag = data[at++];
            flags[i++] = flag;
            if ((flag & 8) != 0 && at < data.Length)
            {
                int repeat = data[at++];
                for (int r = 0; r < repeat && i < points; r++)
                {
                    flags[i++] = flag;
                }
            }
        }

        int[] xs = Coordinates(data, ref at, flags, 2, 16);
        int[] ys = Coordinates(data, ref at, flags, 4, 32);
        int first = 0;
        foreach (int last in ends)
        {
            if (last < first || last >= points)
            {
                return;
            }

            Contour(shape, flags, xs, ys, first, last, a, b, c, d, e, f);
            first = last + 1;
        }
    }

    private static int[] Coordinates(ReadOnlySpan<byte> data, ref int at, byte[] flags, int shortBit, int sameBit)
    {
        int[] values = new int[flags.Length];
        int value = 0;
        for (int i = 0; i < flags.Length; i++)
        {
            byte flag = flags[i];
            if ((flag & shortBit) != 0)
            {
                int delta = at < data.Length ? data[at++] : 0;
                value += (flag & sameBit) != 0 ? delta : -delta;
            }
            else if ((flag & sameBit) == 0)
            {
                value += at + 1 < data.Length ? BinaryPrimitives.ReadInt16BigEndian(data[at..]) : 0;
                at += 2;
            }

            values[i] = value;
        }

        return values;
    }

    /// <summary>One closed contour of on-curve and off-curve points, as quadratic curves.</summary>
    private static void Contour(Shape shape, byte[] flags, int[] xs, int[] ys, int first, int last, double a, double b, double c, double d, double e, double f)
    {
        int count = last - first + 1;
        (float X, float Y) Point(int i) => ((float)((a * xs[i]) + (c * ys[i]) + e), (float)((b * xs[i]) + (d * ys[i]) + f));
        bool On(int i) => (flags[i] & 1) != 0;
        int Index(int k) => first + (((k % count) + count) % count);

        // Start at an on-curve point, or the midpoint of two off-curve points.
        int startK = -1;
        for (int k = 0; k < count; k++)
        {
            if (On(Index(k)))
            {
                startK = k;
                break;
            }
        }

        (float X, float Y) start;
        if (startK < 0)
        {
            (float X, float Y) p0 = Point(Index(0)), p1 = Point(Index(1));
            start = ((p0.X + p1.X) / 2, (p0.Y + p1.Y) / 2);
            startK = 0;
            shape.MoveTo(start.X, start.Y);
            for (int k = 1; k <= count; k++)
            {
                (float X, float Y) control = Point(Index(k));
                (float X, float Y) next = Point(Index(k + 1));
                (float X, float Y) mid = k == count ? start : ((control.X + next.X) / 2, (control.Y + next.Y) / 2);
                shape.QuadTo(control.X, control.Y, mid.X, mid.Y);
            }

            shape.Close();
            return;
        }

        start = Point(Index(startK));
        shape.MoveTo(start.X, start.Y);
        (float X, float Y)? pending = null;
        for (int k = startK + 1; k <= startK + count; k++)
        {
            int i = Index(k);
            (float X, float Y) p = Point(i);
            if (On(i))
            {
                if (pending is { } control)
                {
                    shape.QuadTo(control.X, control.Y, p.X, p.Y);
                    pending = null;
                }
                else
                {
                    shape.LineTo(p.X, p.Y);
                }
            }
            else if (pending is { } control)
            {
                (float X, float Y) mid = ((control.X + p.X) / 2, (control.Y + p.Y) / 2);
                shape.QuadTo(control.X, control.Y, mid.X, mid.Y);
                pending = p;
            }
            else
            {
                pending = p;
            }
        }

        if (pending is { } last2)
        {
            shape.QuadTo(last2.X, last2.Y, start.X, start.Y);
        }

        shape.Close();
    }

    private void Composite(Shape shape, ReadOnlySpan<byte> data, double a, double b, double c, double d, double e, double f, int depth)
    {
        int at = 10;
        while (at + 4 <= data.Length)
        {
            int flags = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
            int component = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
            at += 4;
            double dx, dy;
            if ((flags & 1) != 0)
            {
                if (at + 4 > data.Length)
                {
                    return;
                }

                dx = BinaryPrimitives.ReadInt16BigEndian(data[at..]);
                dy = BinaryPrimitives.ReadInt16BigEndian(data[(at + 2)..]);
                at += 4;
            }
            else
            {
                if (at + 2 > data.Length)
                {
                    return;
                }

                dx = (sbyte)data[at];
                dy = (sbyte)data[at + 1];
                at += 2;
            }

            double sa = 1, sb = 0, sc = 0, sd = 1;
            if ((flags & 8) != 0 && at + 2 <= data.Length)
            {
                sa = sd = F2Dot14(data[at..]);
                at += 2;
            }
            else if ((flags & 0x40) != 0 && at + 4 <= data.Length)
            {
                sa = F2Dot14(data[at..]);
                sd = F2Dot14(data[(at + 2)..]);
                at += 4;
            }
            else if ((flags & 0x80) != 0 && at + 8 <= data.Length)
            {
                sa = F2Dot14(data[at..]);
                sb = F2Dot14(data[(at + 2)..]);
                sc = F2Dot14(data[(at + 4)..]);
                sd = F2Dot14(data[(at + 6)..]);
                at += 8;
            }

            // Offsets as points to match (flag 2 clear) aren't written by any font this library embeds; they're skipped.
            if ((flags & 2) != 0)
            {
                // Component transform, then the parent's.
                double na = (sa * a) + (sb * c), nb = (sa * b) + (sb * d);
                double nc = (sc * a) + (sd * c), nd = (sc * b) + (sd * d);
                double ne = (dx * a) + (dy * c) + e, nf = (dx * b) + (dy * d) + f;
                Append(shape, component, na, nb, nc, nd, ne, nf, depth + 1);
            }

            if ((flags & 0x20) == 0)
            {
                return;
            }
        }
    }

    private static double F2Dot14(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadInt16BigEndian(data) / 16384.0;
}
