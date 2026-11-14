using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Zorg.Pdf.Fonts;

namespace Zorg.Pdf.Layout;

/// <summary>A font and the fonts tried, in order, for characters it has no glyph for.</summary>
internal sealed class FontChain(EmbeddedFont primary, IReadOnlyList<EmbeddedFont> fallbacks, bool syntheticBold = false, bool syntheticItalic = false)
{
    public EmbeddedFont Primary { get; } = primary;

    /// <summary>The family has no bold face: draw the regular one thickened (fill and stroke).</summary>
    public bool SyntheticBold { get; } = syntheticBold;

    /// <summary>The family has no italic face: draw the upright one slanted.</summary>
    public bool SyntheticItalic { get; } = syntheticItalic;

    public IReadOnlyList<EmbeddedFont> Fallbacks { get; } = fallbacks;

    /// <summary>The first font in the chain with a glyph for <paramref name="rune"/>, or the primary font's .notdef.</summary>
    public (EmbeddedFont Font, ushort Glyph) Glyph(Rune rune)
    {
        if (Primary.TryGlyph(rune, out ushort glyph))
        {
            return (Primary, glyph);
        }

        foreach (EmbeddedFont fallback in Fallbacks)
        {
            if (fallback.TryGlyph(rune, out glyph))
            {
                return (fallback, glyph);
            }
        }

        return (Primary, Primary.Notdef());
    }

    /// <summary>True when some font in the chain has a glyph for the code point. Records nothing.</summary>
    public bool Covers(int codePoint) => Primary.Font.GlyphFor(codePoint) != 0 || Fallbacks.Any(f => f.Font.GlyphFor(codePoint) != 0);
}

/// <summary>Text in one font chain, size and colour: a run of a paragraph.</summary>
/// <param name="Text">The text. Line feeds break the line.</param>
/// <param name="Fonts">The font and its fallbacks.</param>
/// <param name="Size">The font size in points.</param>
/// <param name="Color">The colour, or null for the default (black).</param>
internal sealed record TextSegment(string Text, FontChain Fonts, double Size, Color? Color)
{
    /// <summary>The language, when it differs from the paragraph's; null otherwise.</summary>
    public string? Language { get; init; }

    /// <summary>The index of the link this text belongs to, or -1.</summary>
    public int Link { get; init; } = -1;

    /// <summary>How its words may be hyphenated: soft hyphens only, also by a hyphenator, or never.</summary>
    public Hyphens Hyphens { get; init; }

    /// <summary>For <see cref="Hyphens.Auto"/>: the hyphenator and the text's language.</summary>
    public (IHyphenator Hyphenator, string Language)? Hyphenator { get; init; }
}

/// <summary>
/// Part of a line in one font, size and colour: the glyphs, the extra space after each (kerning and justification, in
/// thousandths of <see cref="Size"/>, as <c>TJ</c> takes it), and where it starts from the line's start, in points.
/// </summary>
internal sealed record LineRun(EmbeddedFont Font, double Size, Color? Color, ushort[] Glyphs, double[] ExtraAfter, double X)
{
    /// <summary>Drawn thickened, because the family has no bold face.</summary>
    public bool SyntheticBold { get; init; }

    /// <summary>The run's language, when it differs from its paragraph's; drawn as a Span with /Lang.</summary>
    public string? Language { get; init; }

    /// <summary>The index of the link the run belongs to, or -1.</summary>
    public int Link { get; init; } = -1;

    /// <summary>A hyphen added at a line end: it copies out as a soft hyphen (U+00AD), not as part of the word.</summary>
    public bool Inserted { get; init; }

    /// <summary>Drawn slanted, because the family has no italic face.</summary>
    public bool SyntheticItalic { get; init; }
}

/// <summary>
/// One laid-out line: its runs, its width in points, where it starts from the left edge, and its height and ascent
/// (the tallest run's, so mixed sizes share a baseline).
/// </summary>
internal sealed record LayoutLine(IReadOnlyList<LineRun> Runs, double Width, double Offset, double Ascent, double Height)
{
    /// <summary>All the line's glyphs, in order.</summary>
    public ushort[] Glyphs => [.. Runs.SelectMany(r => r.Glyphs)];

    /// <summary>The extra space after each glyph, in thousandths of its run's size. Meaningful for one-run lines.</summary>
    public double[] ExtraAfter => [.. Runs.SelectMany(r => r.ExtraAfter)];
}

/// <summary>Something layout had to do that the author should know about. Code is stable; Index is in the source text.</summary>
internal readonly record struct TextDiagnostic(string Code, string Message, int Index);

/// <summary>
/// Breaks a paragraph of styled runs into lines that fit a width, greedily, with pair kerning within each run.
/// </summary>
/// <remarks>
/// <para>Line breaking is a subset of Unicode's algorithm (UAX #14), enough for European text and ideographs:</para>
/// <list type="bullet">
/// <item>Line feeds, carriage returns and U+2028/U+2029 always break. Each starts a new paragraph for alignment.</item>
/// <item>A break may follow a run of spaces, a hyphen or dash (not before a digit, so "-5" stays whole), a soft hyphen
/// (shown as a hyphen only when the line breaks there), a zero-width space, or any ideograph.</item>
/// <item>No-break spaces (U+00A0, U+2007, U+202F) never break.</item>
/// <item>Spaces at the end of a line hang outside the width and aren't drawn.</item>
/// </list>
/// <para>
/// A word wider than the whole width is broken between characters, and reported as diagnostic ZPDF-T001, never
/// clipped or dropped. A character no font in a run's chain has is drawn as .notdef; the renderer reports it first.
/// </para>
/// </remarks>
internal sealed class ParagraphLayout
{
    /// <summary>A word didn't fit the width and was broken inside.</summary>
    public const string WordTooWide = Validation.DiagnosticCodes.WordTooWide;

    private const double Tolerance = 1e-6;

    private ParagraphLayout(List<LayoutLine> lines, List<TextDiagnostic> diagnostics)
    {
        Lines = lines;
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<LayoutLine> Lines { get; }

    public IReadOnlyList<TextDiagnostic> Diagnostics { get; }

    /// <summary>One run of text in one font: the common case, and the one the spike renderer uses.</summary>
    public static ParagraphLayout Create(string text, EmbeddedFont font, double size, double width, TextAlign align) =>
        Create([new TextSegment(text, new FontChain(font, []), size, null)], width, align, font.LineHeight);

    /// <summary>Lays out runs of text as one paragraph.</summary>
    /// <param name="segments">The runs, in order. At least one, so an empty paragraph still has a size.</param>
    /// <param name="width">The width to fill, in points.</param>
    /// <param name="align">Horizontal alignment.</param>
    /// <param name="lineHeight">Line height as a multiple of each run's size; a line is as tall as its tallest run.</param>
    public static ParagraphLayout Create(IReadOnlyList<TextSegment> segments, double width, TextAlign align, double lineHeight)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentOutOfRangeException.ThrowIfZero(segments.Count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        foreach (TextSegment segment in segments)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segment.Size);
        }

        var lines = new List<LayoutLine>();
        var diagnostics = new List<TextDiagnostic>();
        var builder = new LineBuilder(width, align, lineHeight, lines, diagnostics);
        builder.Layout(segments);
        return new ParagraphLayout(lines, diagnostics);
    }

    private static bool IsBreakingSpace(Rune rune) =>
        rune.Value is not (0x00A0 or 0x2007 or 0x202F) && Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator;

    private static bool IsBreakingHyphen(int c) => c is '-' or 0x2010 or 0x2013 or 0x2014;

    /// <summary>Ideographs and kana: a line may break before or after each one (UAX #14 class ID, simplified).</summary>
    private static bool IsIdeographic(int c) =>
        c is (>= 0x3040 and <= 0x30FF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF)
            or (>= 0x20000 and <= 0x3FFFF);

    /// <summary>One glyph and what line breaking needs to know about it. Widths are in points.</summary>
    private struct Item
    {
        public EmbeddedFont Font;
        public ushort Glyph;
        public double Size;
        public Color? Color;
        public string? Language;
        public int Link;
        public int Char;
        public bool Inserted;
        public (IHyphenator Hyphenator, string Language)? Hyphenator;
        public bool Bold;
        public bool Italic;
        public bool Space;
        public bool BreakAfter;
        public bool SoftHyphenAfter;
        public int Source;
        public double Advance;
        public double Kern; // Kerning to the next item, when it's in the same font and size.
    }

    private sealed class LineBuilder(double width, TextAlign align, double lineHeight, List<LayoutLine> lines, List<TextDiagnostic> diagnostics)
    {
        private readonly List<Item> _items = [];
        private readonly HashSet<int> _hyphenated = []; // Words already asked about, by their first item.
        private TextSegment _lastSegment = null!;

        public void Layout(IReadOnlyList<TextSegment> segments)
        {
            _lastSegment = segments[0];
            bool afterHyphen = false;
            int offset = 0;
            foreach (TextSegment segment in segments)
            {
                _lastSegment = segment;
                string text = segment.Text;
                int index = 0;
                while (index < text.Length)
                {
                    if (Rune.DecodeFromUtf16(text.AsSpan(index), out Rune rune, out int consumed) != System.Buffers.OperationStatus.Done)
                    {
                        throw new PdfException(string.Create(CultureInfo.InvariantCulture, $"The text has a lone surrogate at index {offset + index}."));
                    }

                    int at = offset + index;
                    index += consumed;
                    int c = rune.Value;
                    if (c is '\n' or '\r' or 0x2028 or 0x2029)
                    {
                        if (c == '\r' && index < text.Length && text[index] == '\n')
                        {
                            index++;
                        }

                        FinishHardLine();
                        afterHyphen = false;
                        continue;
                    }

                    if (c == 0x00AD || c == 0x200B)
                    {
                        // Soft hyphen and zero-width space: break opportunities with nothing to draw.
                        if (_items.Count > 0 && !(c == 0x00AD && segment.Hyphens == Hyphens.None))
                        {
                            Item last = _items[^1];
                            last.BreakAfter = true;
                            last.SoftHyphenAfter |= c == 0x00AD;
                            _items[^1] = last;
                        }

                        continue;
                    }

                    if (c == '\t')
                    {
                        rune = new Rune(' ');
                        c = ' ';
                    }
                    else if (c < 0x20 || c is >= 0x7F and < 0xA0)
                    {
                        throw new PdfException(string.Create(CultureInfo.InvariantCulture, $"The text has the control character U+{c:X4} at index {at}, which can't be drawn."));
                    }

                    if (afterHyphen && Rune.IsDigit(rune))
                    {
                        SetLastBreakAfter(false); // "10-5", "-5": keep the number with its sign.
                    }

                    bool space = IsBreakingSpace(rune);
                    bool ideograph = IsIdeographic(c);
                    if (ideograph && _items.Count > 0 && !_items[^1].Space)
                    {
                        SetLastBreakAfter(true);
                    }

                    if (space && _items.Count > 0 && _items[^1].Space)
                    {
                        SetLastBreakAfter(false); // Break after the last space of a run, never inside it.
                    }

                    bool hyphen = IsBreakingHyphen(c) && _items.Count > 0 && !_items[^1].Space;
                    (EmbeddedFont font, ushort glyph) = segment.Fonts.Glyph(rune);
                    _items.Add(new Item
                    {
                        Font = font,
                        Glyph = glyph,
                        Size = segment.Size,
                        Color = segment.Color,
                        Language = segment.Language,
                        Link = segment.Link,
                        Char = c,
                        Hyphenator = segment.Hyphenator,
                        Bold = segment.Fonts.SyntheticBold,
                        Italic = segment.Fonts.SyntheticItalic,
                        Space = space,
                        BreakAfter = space || ideograph || hyphen,
                        Source = at,
                        Advance = font.Width(glyph) * segment.Size / 1000,
                    });
                    afterHyphen = hyphen;
                }

                offset += text.Length;
            }

            FinishHardLine();
        }

        private void SetLastBreakAfter(bool value)
        {
            Item last = _items[^1]; // A struct: change the copy and put it back.
            last.BreakAfter = value;
            _items[^1] = last;
        }

        private void FinishHardLine()
        {
            for (int i = 0; i + 1 < _items.Count; i++)
            {
                Item a = _items[i];
                Item b = _items[i + 1];
                if (ReferenceEquals(a.Font, b.Font) && a.Size == b.Size)
                {
                    a.Kern = a.Font.Kerning(a.Glyph, b.Glyph) * a.Size / 1000;
                    _items[i] = a;
                }
            }

            if (_items.Count == 0)
            {
                // An empty line still takes its height, in the size of the run it's in.
                EmbeddedFont font = _lastSegment.Fonts.Primary;
                double size = _lastSegment.Size;
                lines.Add(new LayoutLine([], 0, 0, font.Font.LayoutAscender * size / font.Font.UnitsPerEm, size * lineHeight));
            }

            int start = 0;
            while (start < _items.Count)
            {
                start = NextLine(start);
            }

            _items.Clear();
            _hyphenated.Clear();
        }

        /// <summary>Fills one line from <paramref name="start"/> and returns where the next one starts.</summary>
        private int NextLine(int start)
        {
            double used = 0; // Everything so far, trailing spaces included.
            double visible = 0; // Up to the last glyph that isn't a space.
            int lastBreak = -1;
            double widthAtBreak = 0;
            int i = start;
            for (; i < _items.Count; i++)
            {
                double add = _items[i].Advance + (i > start ? _items[i - 1].Kern : 0);
                if (!_items[i].Space && used + add > width + Tolerance)
                {
                    break;
                }

                used += add;
                if (!_items[i].Space)
                {
                    visible = used;
                }

                if (_items[i].BreakAfter && i + 1 < _items.Count)
                {
                    double candidate = visible + (_items[i].SoftHyphenAfter ? HyphenWidth(_items[i]) : 0);
                    if (candidate <= width + Tolerance)
                    {
                        lastBreak = i;
                        widthAtBreak = candidate;
                    }
                }
            }

            if (i == _items.Count)
            {
                Emit(start, _items.Count - 1, visible, hyphenate: false, last: true);
                return _items.Count;
            }

            if (TryHyphenate(i, lastBreak))
            {
                return NextLine(start); // The overflowing word has break points now: fill the line again.
            }

            if (lastBreak >= start)
            {
                Emit(start, lastBreak, widthAtBreak, hyphenate: _items[lastBreak].SoftHyphenAfter, last: false);
                return lastBreak + 1;
            }

            // Nothing fits before the first break: break inside the word, keeping at least one glyph per line.
            int end = Math.Max(i, start + 1);
            double emergency = 0;
            for (int k = start; k < end; k++)
            {
                emergency += _items[k].Advance + (k > start ? _items[k - 1].Kern : 0);
            }

            // Once per word: the lines after the first piece of a broken word start mid-word.
            if (start == 0 || _items[start - 1].BreakAfter)
            {
                diagnostics.Add(new TextDiagnostic(
                    WordTooWide,
                    string.Create(CultureInfo.InvariantCulture, $"The word at index {_items[start].Source} is wider than the {width:0.##} pt column at {_items[start].Size:0.##} pt, so it was broken between characters."),
                    _items[start].Source));
            }

            Emit(start, end - 1, emergency, hyphenate: false, last: end == _items.Count);
            return end;
        }

        /// <summary>
        /// Asks the hyphenator where the word at <paramref name="overflow"/> may break, once per word, and marks those
        /// places as soft hyphens. Only letters count: a word with digits or of fewer than five letters is left alone.
        /// </summary>
        private bool TryHyphenate(int overflow, int lastBreak)
        {
            int at = overflow;
            if (at < _items.Count && !IsLetter(_items[at].Char) && at > 0)
            {
                at--; // Overflowing at the punctuation after a word: hyphenate the word.
            }

            if (at >= _items.Count || _items[at].Hyphenator is not { } context || !IsLetter(_items[at].Char))
            {
                return false;
            }

            int first = at, last = at;
            while (first > 0 && first - 1 > lastBreak && !_items[first - 1].BreakAfter && IsLetter(_items[first - 1].Char))
            {
                first--;
            }

            while (last + 1 < _items.Count && !_items[last].BreakAfter && IsLetter(_items[last + 1].Char))
            {
                last++;
            }

            if (last - first + 1 < 5 || !_hyphenated.Add(first) || (first > 0 && !_items[first - 1].BreakAfter && !_items[first - 1].Space && IsDigitish(_items[first - 1].Char)))
            {
                return false;
            }

            var word = new StringBuilder(last - first + 1);
            for (int k = first; k <= last; k++)
            {
                word.Append(new Rune(_items[k].Char).ToString());
            }

            if (word.Length != last - first + 1)
            {
                return false; // Letters outside the BMP: the indexes wouldn't line up; leave the word.
            }

            Span<bool> breaks = stackalloc bool[word.Length <= 256 ? word.Length : 0];
            if (breaks.IsEmpty || !context.Hyphenator.TryHyphenate(context.Language, word.ToString(), breaks))
            {
                return false;
            }

            bool any = false;
            for (int k = 0; k < breaks.Length - 1; k++)
            {
                if (breaks[k])
                {
                    Item item = _items[first + k];
                    item.BreakAfter = item.SoftHyphenAfter = true;
                    _items[first + k] = item;
                    any = true;
                }
            }

            return any;
        }

        private static bool IsLetter(int c) => c > 0 && Rune.IsValid(c) && (Rune.IsLetter(new Rune(c)) || Rune.GetUnicodeCategory(new Rune(c)) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark);

        private static bool IsDigitish(int c) => c is >= '0' and <= '9';

        private static double HyphenWidth(Item before)
        {
            ushort hyphen = before.Font.Font.GlyphFor('-');
            return hyphen == 0 ? 0 : (before.Font.Width(hyphen) + before.Font.Kerning(before.Glyph, hyphen)) * before.Size / 1000;
        }

        private void Emit(int start, int end, double lineWidth, bool hyphenate, bool last)
        {
            while (end > start && _items[end].Space)
            {
                end--; // Trailing spaces hang and aren't drawn.
            }

            var placed = new List<Item>(end - start + 2);
            var extra = new List<double>(end - start + 2); // Points after each placed item.
            for (int k = start; k <= end; k++)
            {
                placed.Add(_items[k]);
                extra.Add(k < end ? _items[k].Kern : 0);
            }

            if (hyphenate)
            {
                Item before = _items[end];
                if (before.Font.TryGlyph(new Rune('-'), out ushort hyphen))
                {
                    extra[^1] = before.Font.Kerning(before.Glyph, hyphen) * before.Size / 1000;
                    placed.Add(before with { Glyph = hyphen, Space = false, Inserted = true, Advance = before.Font.Width(hyphen) * before.Size / 1000 });
                    extra.Add(0);
                }
            }

            double points = lineWidth;
            double offset = align switch
            {
                TextAlign.Right => width - points,
                TextAlign.Center => (width - points) / 2,
                _ => 0,
            };

            if (align == TextAlign.Justify && !last)
            {
                int spaces = 0;
                for (int k = 0; k < placed.Count - 1; k++)
                {
                    spaces += placed[k].Space ? 1 : 0;
                }

                if (spaces > 0 && lineWidth < width)
                {
                    double each = (width - lineWidth) / spaces;
                    for (int k = 0; k < placed.Count - 1; k++)
                    {
                        if (placed[k].Space)
                        {
                            extra[k] += each;
                        }
                    }

                    points = width;
                }
            }

            lines.Add(new LayoutLine(Runs(placed, extra), points, Math.Max(offset, 0), Ascent(placed), Height(placed)));
        }

        /// <summary>Groups placed glyphs into runs of one font, size and colour, each placed from the line's start.</summary>
        private static List<LineRun> Runs(List<Item> placed, List<double> extra)
        {
            var runs = new List<LineRun>();
            double x = 0;
            int start = 0;
            for (int k = 0; k <= placed.Count; k++)
            {
                bool split = k == placed.Count
                    || !ReferenceEquals(placed[k].Font, placed[start].Font) || placed[k].Size != placed[start].Size || placed[k].Color != placed[start].Color
                    || placed[k].Bold != placed[start].Bold || placed[k].Italic != placed[start].Italic
                    || !string.Equals(placed[k].Language, placed[start].Language, StringComparison.Ordinal) || placed[k].Link != placed[start].Link || placed[k].Inserted != placed[start].Inserted;
                if (!split)
                {
                    continue;
                }

                Item first = placed[start];
                int count = k - start;
                ushort[] glyphs = new ushort[count];
                double[] after = new double[count];
                double runX = x;
                for (int g = 0; g < count; g++)
                {
                    glyphs[g] = placed[start + g].Glyph;
                    after[g] = g < count - 1 ? extra[start + g] * 1000 / first.Size : 0; // The last gap goes into the next run's X.
                    x += placed[start + g].Advance + extra[start + g];
                }

                runs.Add(new LineRun(first.Font, first.Size, first.Color, glyphs, after, runX) { SyntheticBold = first.Bold, SyntheticItalic = first.Italic, Language = first.Language, Link = first.Link, Inserted = first.Inserted });
                start = k;
            }

            return runs;
        }

        private static double Ascent(List<Item> placed) =>
            placed.Max(i => i.Font.Font.LayoutAscender * i.Size / i.Font.Font.UnitsPerEm);

        private double Height(List<Item> placed) => placed.Max(i => i.Size) * lineHeight;
    }
}
