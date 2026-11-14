using System;
using System.Collections.Generic;
using System.Text;

namespace Zorg.Pdf.Fonts;

/// <summary>
/// A parsed TrueType font (OpenType with <c>glyf</c> outlines): the metrics, the character map and the glyph data
/// needed to lay out text and embed a subset. Fonts with CFF outlines and font collections aren't supported yet.
/// </summary>
/// <remarks>
/// Parsing checks every offset and count against the file, and caps the size of the character map, so a hostile file
/// fails with a <see cref="PdfException"/> rather than an unrelated exception or unbounded memory.
/// </remarks>
internal sealed partial class TrueTypeFont
{
    /// <summary>The largest character map accepted. Unicode has about 1.1 million code points.</summary>
    internal const int MaxMappings = 0x110000;

    private readonly ReadOnlyMemory<byte> _file;
    private readonly Dictionary<uint, (int Offset, int Length)> _tables;
    private readonly Dictionary<int, ushort> _cmap;
    private readonly ushort[] _advances;
    private readonly int[] _loca;

    private TrueTypeFont(ReadOnlyMemory<byte> file, Dictionary<uint, (int Offset, int Length)> tables)
    {
        _file = file;
        _tables = tables;

        FontData head = Table("head");
        if (head.Length < 54)
        {
            throw head.Damaged("the table is too short");
        }

        UnitsPerEm = head.U16(18);
        if (UnitsPerEm is < 16 or > 16384)
        {
            throw head.Damaged($"unitsPerEm is {UnitsPerEm}, outside 16 to 16384");
        }

        BoundingBox = (head.I16(36), head.I16(38), head.I16(40), head.I16(42));
        bool longLoca = head.I16(50) switch
        {
            0 => false,
            1 => true,
            short other => throw head.Damaged($"indexToLocFormat is {other}"),
        };

        FontData maxp = Table("maxp");
        GlyphCount = maxp.U16(4);
        if (GlyphCount == 0)
        {
            throw maxp.Damaged("the font has no glyphs");
        }

        FontData hhea = Table("hhea");
        Ascender = hhea.I16(4);
        Descender = hhea.I16(6);
        LineGap = hhea.I16(8);
        int metrics = hhea.U16(34);
        if (metrics == 0 || metrics > GlyphCount)
        {
            throw hhea.Damaged($"numberOfHMetrics is {metrics} for {GlyphCount} glyphs");
        }

        _advances = ReadAdvances(Table("hmtx"), metrics, GlyphCount);
        _loca = ReadLoca(Table("loca"), longLoca, GlyphCount, TableLength("glyf"));
        _cmap = ReadCmap(Table("cmap"), GlyphCount);

        ReadOs2();
        ReadPost();
        PostScriptName = ReadPostScriptName();
        ReadKerning();
    }

    public int UnitsPerEm { get; }

    public int GlyphCount { get; }

    public (short XMin, short YMin, short XMax, short YMax) BoundingBox { get; }

    public short Ascender { get; }

    public short Descender { get; }

    public short LineGap { get; }

    /// <summary>
    /// The ascender used for layout: OS/2's typographic one when the font sets USE_TYPO_METRICS, otherwise hhea's,
    /// as Word and browsers do. The ascender, descender and line gap always come from the same set.
    /// </summary>
    public short LayoutAscender { get; private set; }

    /// <summary>The descender used for layout (negative), from the same set as <see cref="LayoutAscender"/>.</summary>
    public short LayoutDescender { get; private set; }

    /// <summary>The line gap used for layout, from the same set as <see cref="LayoutAscender"/>.</summary>
    public short LayoutLineGap { get; private set; }

    /// <summary>OS/2 capital height, or the ascender when the font doesn't say.</summary>
    public short CapHeight { get; private set; }

    public int WeightClass { get; private set; } = 400;

    /// <summary>The OS/2 embedding permissions (fsType). 0 when there is no OS/2 table, which means installable.</summary>
    public ushort EmbeddingFlags { get; private set; }

    public double ItalicAngle { get; private set; }

    public bool IsFixedPitch { get; private set; }

    /// <summary>The PostScript name (name ID 6), cleaned to the characters a PDF name may hold.</summary>
    public string PostScriptName { get; }

    /// <summary>The number of characters the font maps to a glyph.</summary>
    public int MappedCharacterCount => _cmap.Count;

    public static TrueTypeFont Parse(ReadOnlyMemory<byte> file)
    {
        var data = new FontData(file.Span, "table directory");
        if (data.Length < 12)
        {
            throw data.Damaged("the file is too short to be a font");
        }

        uint version = data.U32(0);
        switch (version)
        {
            case 0x00010000:
            case 0x74727565: // 'true', older Apple fonts.
                break;
            case 0x4F54544F: // 'OTTO'
                throw new PdfException("The font has CFF (PostScript) outlines. Only TrueType outlines are supported so far; use a .ttf font.");
            case 0x74746366: // 'ttcf'
                throw new PdfException("The file is a font collection (.ttc). Collections aren't supported yet; use a single .ttf font.");
            default:
                throw data.Damaged($"the signature 0x{version:X8} isn't a TrueType font");
        }

        int count = data.U16(4);
        if (count is 0 or > 512)
        {
            throw data.Damaged($"it lists {count} tables");
        }

        var tables = new Dictionary<uint, (int, int)>(count);
        for (int i = 0; i < count; i++)
        {
            int at = 12 + (i * 16);
            uint tag = data.U32(at);
            uint offset = data.U32(at + 8);
            uint length = data.U32(at + 12);
            if ((ulong)offset + length > (ulong)data.Length)
            {
                throw data.Damaged($"table '{TagName(tag)}' runs past the end of the file");
            }

            if (!tables.TryAdd(tag, ((int)offset, (int)length)))
            {
                throw data.Damaged($"table '{TagName(tag)}' is listed twice");
            }
        }

        foreach (string required in new[] { "head", "hhea", "maxp", "hmtx", "loca", "glyf", "cmap" })
        {
            if (!tables.ContainsKey(Tag(required)))
            {
                throw new PdfException($"The font has no '{required}' table, so it can't be used.");
            }
        }

        return new TrueTypeFont(file, tables);
    }

    /// <summary>The glyph for a code point, or 0 (.notdef) when the font has none.</summary>
    public ushort GlyphFor(int codePoint) => _cmap.TryGetValue(codePoint, out ushort glyph) ? glyph : (ushort)0;

    /// <summary>The advance width in font units.</summary>
    public int AdvanceWidth(int glyph) => _advances[glyph];

    /// <summary>The outline data of one glyph. Empty for a glyph with no outline, such as the space.</summary>
    public ReadOnlySpan<byte> GlyphData(int glyph)
    {
        (int glyfOffset, _) = _tables[Tag("glyf")];
        return _file.Span.Slice(glyfOffset + _loca[glyph], _loca[glyph + 1] - _loca[glyph]);
    }

    /// <summary>A table's bytes, or empty when the font has no such table.</summary>
    public ReadOnlySpan<byte> RawTable(string tag) =>
        _tables.TryGetValue(Tag(tag), out (int Offset, int Length) t) ? _file.Span.Slice(t.Offset, t.Length) : [];

    public bool HasTable(string tag) => _tables.ContainsKey(Tag(tag));

    internal static uint Tag(string tag) => ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];

    private static string TagName(uint tag)
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            char c = (char)((tag >> (24 - (i * 8))) & 0xFF);
            chars[i] = c is >= ' ' and <= '~' ? c : '?';
        }

        return new string(chars);
    }

    private FontData Table(string tag)
    {
        (int offset, int length) = _tables[Tag(tag)];
        return new FontData(_file.Span.Slice(offset, length), tag);
    }

    private int TableLength(string tag) => _tables[Tag(tag)].Length;

    private static ushort[] ReadAdvances(FontData hmtx, int metrics, int glyphs)
    {
        var advances = new ushort[glyphs];
        for (int i = 0; i < metrics; i++)
        {
            advances[i] = hmtx.U16(i * 4);
        }

        // Glyphs past numberOfHMetrics share the last advance (monospaced tails).
        Array.Fill(advances, advances[metrics - 1], metrics, glyphs - metrics);
        return advances;
    }

    private static int[] ReadLoca(FontData loca, bool longFormat, int glyphs, int glyfLength)
    {
        var offsets = new int[glyphs + 1];
        for (int i = 0; i <= glyphs; i++)
        {
            long offset = longFormat ? loca.U32(i * 4) : loca.U16(i * 2) * 2L;
            if (offset > glyfLength || (i > 0 && offset < offsets[i - 1]))
            {
                throw loca.Damaged($"the offset of glyph {i} is out of order or past the 'glyf' table");
            }

            offsets[i] = (int)offset;
        }

        return offsets;
    }

    private static Dictionary<int, ushort> ReadCmap(FontData cmap, int glyphs)
    {
        // Prefer full Unicode (format 12), then the Basic Multilingual Plane (format 4).
        int best = -1;
        int bestRank = 0;
        int count = cmap.U16(2);
        for (int i = 0; i < count; i++)
        {
            int at = 4 + (i * 8);
            int platform = cmap.U16(at);
            int encoding = cmap.U16(at + 2);
            uint offset = cmap.U32(at + 4);
            if (offset >= (uint)cmap.Length)
            {
                throw cmap.Damaged($"subtable {i} starts past the end");
            }

            int format = cmap.U16((int)offset);
            int rank = (platform, encoding, format) switch
            {
                (3, 10, 12) => 4,
                (0, 4 or 6, 12) => 3,
                (3, 1, 4) => 2,
                (0, _, 4) => 1,
                _ => 0,
            };
            if (rank > bestRank)
            {
                (best, bestRank) = ((int)offset, rank);
            }
        }

        if (best < 0)
        {
            throw new PdfException("The font has no Unicode character map (cmap format 4 or 12). Symbol fonts aren't supported yet.");
        }

        var map = new Dictionary<int, ushort>();
        if (cmap.U16(best) == 12)
        {
            ReadFormat12(cmap, best, glyphs, map);
        }
        else
        {
            ReadFormat4(cmap, best, glyphs, map);
        }

        return map;
    }

    private static void ReadFormat4(FontData cmap, int at, int glyphs, Dictionary<int, ushort> map)
    {
        int segments = cmap.U16(at + 6) / 2;
        int ends = at + 14;
        int starts = ends + (segments * 2) + 2;
        int deltas = starts + (segments * 2);
        int ranges = deltas + (segments * 2);
        cmap.Slice(at, 16 + (segments * 8)); // Checks the four arrays are inside the table.

        // Segments may overlap in a hostile file, so cap the total work, not just each segment.
        long total = 0;
        for (int s = 0; s < segments; s++)
        {
            int end = cmap.U16(ends + (s * 2));
            int start = cmap.U16(starts + (s * 2));
            int delta = cmap.I16(deltas + (s * 2));
            int rangeOffset = cmap.U16(ranges + (s * 2));
            if (start > end || start == 0xFFFF)
            {
                continue;
            }

            total += end - start + 1;
            if (total > 0x10000)
            {
                throw cmap.Damaged("format 4 segments cover more than the 65,536 code points of the BMP");
            }

            for (int c = start; c <= end; c++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (c + delta) & 0xFFFF;
                }
                else
                {
                    // The offset is relative to its own position in the idRangeOffset array.
                    glyph = cmap.U16(ranges + (s * 2) + rangeOffset + ((c - start) * 2));
                    if (glyph != 0)
                    {
                        glyph = (glyph + delta) & 0xFFFF;
                    }
                }

                if (glyph != 0 && glyph < glyphs)
                {
                    map.TryAdd(c, (ushort)glyph);
                }
            }
        }
    }

    private static void ReadFormat12(FontData cmap, int at, int glyphs, Dictionary<int, ushort> map)
    {
        uint groups = cmap.U32(at + 12);
        if (groups > (uint)(cmap.Length / 12))
        {
            throw cmap.Damaged($"format 12 lists {groups} groups, more than fit");
        }

        long total = 0;
        for (int g = 0; g < groups; g++)
        {
            int entry = at + 16 + (g * 12);
            uint start = cmap.U32(entry);
            uint end = cmap.U32(entry + 4);
            uint glyph = cmap.U32(entry + 8);
            if (start > end || end > 0x10FFFF)
            {
                throw cmap.Damaged($"format 12 group {g} has the range {start:X}-{end:X}");
            }

            total += end - start + 1;
            if (total > MaxMappings)
            {
                throw cmap.Damaged("the character map is larger than Unicode");
            }

            for (uint c = start; c <= end; c++, glyph++)
            {
                if (glyph != 0 && glyph < glyphs)
                {
                    map.TryAdd((int)c, (ushort)glyph);
                }
            }
        }
    }

    private void ReadOs2()
    {
        LayoutAscender = Ascender;
        LayoutDescender = Descender;
        LayoutLineGap = LineGap;
        CapHeight = Ascender;
        if (!HasTable("OS/2"))
        {
            return;
        }

        FontData os2 = Table("OS/2");
        if (os2.Length < 78)
        {
            return; // Version 0 from old Apple fonts: nothing we need.
        }

        WeightClass = Math.Clamp((int)os2.U16(4), 1, 1000);
        EmbeddingFlags = os2.U16(8);
        const int UseTypoMetrics = 1 << 7;
        if ((os2.U16(62) & UseTypoMetrics) != 0)
        {
            LayoutAscender = os2.I16(68);
            LayoutDescender = os2.I16(70);
            LayoutLineGap = os2.I16(72);
        }
        if (os2.U16(0) >= 2 && os2.Length >= 90)
        {
            short capHeight = os2.I16(88);
            if (capHeight > 0)
            {
                CapHeight = capHeight;
            }
        }
    }

    private void ReadPost()
    {
        if (!HasTable("post"))
        {
            return;
        }

        FontData post = Table("post");
        if (post.Length >= 16)
        {
            ItalicAngle = post.I32(4) / 65536.0;
            IsFixedPitch = post.U32(12) != 0;
        }
    }

    private string ReadPostScriptName()
    {
        if (!HasTable("name"))
        {
            return "Font";
        }

        FontData name = Table("name");
        int count = name.U16(2);
        int strings = name.U16(4);
        string? mac = null;
        for (int i = 0; i < count; i++)
        {
            int at = 6 + (i * 12);
            int platform = name.U16(at);
            int encoding = name.U16(at + 2);
            int id = name.U16(at + 6);
            int length = name.U16(at + 8);
            int offset = name.U16(at + 10);
            if (id != 6)
            {
                continue;
            }

            ReadOnlySpan<byte> bytes = name.Slice(strings + offset, length);
            if (platform == 3 && encoding == 1)
            {
                return Clean(Encoding.BigEndianUnicode.GetString(bytes));
            }

            if (platform == 1 && encoding == 0)
            {
                mac ??= Clean(Encoding.Latin1.GetString(bytes));
            }
        }

        return mac ?? "Font";
    }

    /// <summary>Keeps the characters a PostScript name allows (Adobe Technical Note 5902), at most 63 of them.</summary>
    private static string Clean(string name)
    {
        var clean = new StringBuilder(Math.Min(name.Length, 63));
        foreach (char c in name)
        {
            if (c is > ' ' and <= '~' and not ('[' or ']' or '(' or ')' or '{' or '}' or '<' or '>' or '/' or '%') && clean.Length < 63)
            {
                clean.Append(c);
            }
        }

        return clean.Length == 0 ? "Font" : clean.ToString();
    }
}
