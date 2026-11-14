using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Fonts;

/// <summary>
/// A TrueType font as a document uses it: text is encoded to glyph IDs as it's laid out, the glyphs used are
/// recorded, and at the end the font is written as a subset Type0 font with <c>Identity-H</c> encoding, a
/// <c>ToUnicode</c> map (so text can be copied, searched and read aloud) and widths for every glyph used.
/// </summary>
internal sealed class EmbeddedFont
{
    // OS/2 fsType bits (OpenType spec): restricted licence, no subsetting, bitmap embedding only.
    private const ushort RestrictedLicence = 0x0002;
    private const ushort NoSubsetting = 0x0100;
    private const ushort BitmapOnly = 0x0200;

    private readonly SortedDictionary<ushort, string> _text = [];

    public EmbeddedFont(TrueTypeFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        ushort flags = font.EmbeddingFlags;
        if ((flags & 0x000F) == RestrictedLicence)
        {
            throw new PdfException($"The font '{font.PostScriptName}' doesn't allow embedding (its licence is restricted). Use a font that allows embedding.");
        }

        if ((flags & (NoSubsetting | BitmapOnly)) != 0)
        {
            throw new PdfException($"The font '{font.PostScriptName}' forbids subsetting or outline embedding, which isn't supported. Use a font that allows embedding.");
        }

        Font = font;
    }

    public TrueTypeFont Font { get; }

    /// <summary>The number of distinct glyphs encoded so far.</summary>
    public int GlyphsUsed => _text.Count;

    /// <summary>
    /// Maps text to glyph IDs, one per character for now (no shaping, ligatures or kerning yet). A character the font
    /// has no glyph for throws, naming the character and where it is: text is never dropped silently.
    /// </summary>
    public ushort[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var glyphs = new List<ushort>(text.Length);
        int index = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            glyphs.Add(Glyph(rune, index));
            index += rune.Utf16SequenceLength;
        }

        return [.. glyphs];
    }

    /// <summary>
    /// The glyph for one character, recorded as used. <paramref name="index"/> is where the character is in the
    /// caller's text, for the error message when the font has no glyph for it.
    /// </summary>
    public ushort Glyph(Rune rune, int index)
    {
        ushort glyph = Font.GlyphFor(rune.Value);
        if (glyph == 0)
        {
            throw new PdfException(string.Create(
                CultureInfo.InvariantCulture,
                $"The font '{Font.PostScriptName}' has no glyph for U+{rune.Value:X4} ('{rune}') at index {index}. Use a font that covers this text."));
        }

        _text.TryAdd(glyph, rune.ToString());
        return glyph;
    }

    /// <summary>The advance width of a glyph in thousandths of the font size, the unit PDF widths use.</summary>
    public double Width(ushort glyph) => Font.AdvanceWidth(glyph) * 1000.0 / Font.UnitsPerEm;

    /// <summary>The width of encoded text at a font size, in points.</summary>
    public double Measure(ReadOnlySpan<ushort> glyphs, double size)
    {
        double total = 0;
        foreach (ushort glyph in glyphs)
        {
            total += Width(glyph);
        }

        return total * size / 1000;
    }

    /// <summary>Line height from the typographic metrics, as a multiple of the font size.</summary>
    public double LineHeight => (Font.LayoutAscender - Font.LayoutDescender + Math.Max((int)Font.LayoutLineGap, 0)) / (double)Font.UnitsPerEm;

    /// <summary>Writes the Type0 font, its CIDFont, descriptor, subset font program and ToUnicode map.</summary>
    public void Write(PdfObjectWriter writer, PdfReference type0)
    {
        ArgumentNullException.ThrowIfNull(writer);
        SortedSet<ushort> glyphs = TrueTypeSubsetter.Close(Font, _text.Keys);
        string baseFont = SubsetTag(glyphs) + "+" + Font.PostScriptName;
        byte[] baseFontName = Encoding.ASCII.GetBytes(baseFont);

        PdfReference cidFont = writer.Allocate();
        PdfReference descriptor = writer.Allocate();
        PdfReference program = writer.Allocate();
        PdfReference toUnicode = writer.Allocate();

        writer.BeginObject(type0);
        writer.BeginDictionary();
        writer.Name("Type"u8);
        writer.Name("Font"u8);
        writer.Name("Subtype"u8);
        writer.Name("Type0"u8);
        writer.Name("BaseFont"u8);
        writer.Name(baseFontName);
        writer.Name("Encoding"u8);
        writer.Name("Identity-H"u8);
        writer.Name("DescendantFonts"u8);
        writer.BeginArray();
        writer.Reference(cidFont);
        writer.EndArray();
        writer.Name("ToUnicode"u8);
        writer.Reference(toUnicode);
        writer.EndDictionary();
        writer.EndObject();

        writer.BeginObject(cidFont);
        writer.BeginDictionary();
        writer.Name("Type"u8);
        writer.Name("Font"u8);
        writer.Name("Subtype"u8);
        writer.Name("CIDFontType2"u8);
        writer.Name("BaseFont"u8);
        writer.Name(baseFontName);
        writer.Name("CIDSystemInfo"u8);
        writer.BeginDictionary();
        writer.Name("Registry"u8);
        writer.LiteralString("Adobe"u8);
        writer.Name("Ordering"u8);
        writer.LiteralString("Identity"u8);
        writer.Name("Supplement"u8);
        writer.Integer(0);
        writer.EndDictionary();
        writer.Name("FontDescriptor"u8);
        writer.Reference(descriptor);
        writer.Name("DW"u8);
        writer.Real(Width(0));
        writer.Name("W"u8);
        WriteWidths(writer, glyphs);
        writer.Name("CIDToGIDMap"u8);
        writer.Name("Identity"u8);
        writer.EndDictionary();
        writer.EndObject();

        WriteDescriptor(writer, descriptor, baseFontName, program);

        byte[] subset = TrueTypeSubsetter.Subset(Font, glyphs);
        writer.BeginStreamObject(program);
        writer.Name("Length1"u8);
        writer.Integer(subset.Length);
        writer.EndStreamObject(subset, compress: true);

        writer.BeginStreamObject(toUnicode);
        writer.EndStreamObject(ToUnicodeCMap(), compress: true);
    }

    private void WriteDescriptor(PdfObjectWriter writer, PdfReference descriptor, byte[] baseFontName, PdfReference program)
    {
        double scale = 1000.0 / Font.UnitsPerEm;
        int flags = 32; // Nonsymbolic: the font uses standard Latin characters, whatever the encoding.
        if (Font.IsFixedPitch)
        {
            flags |= 1;
        }

        if (Font.ItalicAngle != 0)
        {
            flags |= 64;
        }

        // There's no stem width in TrueType. This estimate from the weight class is the common practice.
        double stemV = 10 + (220 * Math.Pow((Font.WeightClass - 50) / 900.0, 2));

        writer.BeginObject(descriptor);
        writer.BeginDictionary();
        writer.Name("Type"u8);
        writer.Name("FontDescriptor"u8);
        writer.Name("FontName"u8);
        writer.Name(baseFontName);
        writer.Name("Flags"u8);
        writer.Integer(flags);
        writer.Name("FontBBox"u8);
        writer.BeginArray();
        writer.Real(Font.BoundingBox.XMin * scale);
        writer.Real(Font.BoundingBox.YMin * scale);
        writer.Real(Font.BoundingBox.XMax * scale);
        writer.Real(Font.BoundingBox.YMax * scale);
        writer.EndArray();
        writer.Name("ItalicAngle"u8);
        writer.Real(Font.ItalicAngle);
        writer.Name("Ascent"u8);
        writer.Real(Font.LayoutAscender * scale);
        writer.Name("Descent"u8);
        writer.Real(Font.LayoutDescender * scale);
        writer.Name("CapHeight"u8);
        writer.Real(Font.CapHeight * scale);
        writer.Name("StemV"u8);
        writer.Real(Math.Round(stemV));
        writer.Name("FontFile2"u8);
        writer.Reference(program);
        writer.EndDictionary();
        writer.EndObject();
    }

    /// <summary>The /W array, with runs of consecutive glyph IDs grouped: <c>[first [w1 w2 ...] ...]</c>.</summary>
    private void WriteWidths(PdfObjectWriter writer, SortedSet<ushort> glyphs)
    {
        writer.BeginArray();
        int previous = -2;
        bool open = false;
        foreach (ushort glyph in glyphs)
        {
            if (glyph != previous + 1)
            {
                if (open)
                {
                    writer.EndArray();
                }

                writer.Integer(glyph);
                writer.BeginArray();
                open = true;
            }

            writer.Real(Width(glyph));
            previous = glyph;
        }

        if (open)
        {
            writer.EndArray();
        }

        writer.EndArray();
    }

    private byte[] ToUnicodeCMap()
    {
        var cmap = new StringBuilder(256 + (_text.Count * 20));
        cmap.Append(
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n" +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

        // At most 100 mappings per block (Adobe Technical Note 5014).
        var entries = new List<KeyValuePair<ushort, string>>(_text);
        for (int start = 0; start < entries.Count; start += 100)
        {
            int count = Math.Min(100, entries.Count - start);
            cmap.Append(CultureInfo.InvariantCulture, $"{count} beginbfchar\n");
            for (int i = start; i < start + count; i++)
            {
                cmap.Append(CultureInfo.InvariantCulture, $"<{entries[i].Key:X4}> <");
                foreach (char c in entries[i].Value)
                {
                    cmap.Append(CultureInfo.InvariantCulture, $"{(int)c:X4}"); // UTF-16BE, surrogate pairs included.
                }

                cmap.Append(">\n");
            }

            cmap.Append("endbfchar\n");
        }

        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Encoding.ASCII.GetBytes(cmap.ToString());
    }

    /// <summary>
    /// Six capital letters that tell readers the font is a subset (ISO 32000-1, 9.6.4). Derived from the glyphs and the
    /// font, so it's the same on every run but differs between different subsets of the same font.
    /// </summary>
    private string SubsetTag(SortedSet<ushort> glyphs)
    {
        byte[] input = new byte[(glyphs.Count * 2) + Encoding.UTF8.GetByteCount(Font.PostScriptName)];
        int at = 0;
        foreach (ushort glyph in glyphs)
        {
            input[at++] = (byte)(glyph >> 8);
            input[at++] = (byte)glyph;
        }

        Encoding.UTF8.GetBytes(Font.PostScriptName, input.AsSpan(at));
        byte[] hash = SHA256.HashData(input);
        Span<char> tag = stackalloc char[6];
        for (int i = 0; i < 6; i++)
        {
            tag[i] = (char)('A' + (hash[i] % 26));
        }

        return new string(tag);
    }
}
