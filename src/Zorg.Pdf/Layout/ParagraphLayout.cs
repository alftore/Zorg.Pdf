using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Zorg.Pdf.Fonts;

namespace Zorg.Pdf.Layout;

/// <summary>
/// One laid-out line: the glyphs to show, the extra space after each (kerning and justification, in thousandths of
/// the font size, as <c>TJ</c> takes it), the width it takes in points, and where it starts from the left edge.
/// </summary>
internal sealed record LayoutLine(ushort[] Glyphs, double[] ExtraAfter, double Width, double Offset);

/// <summary>Something layout had to do that the author should know about. Code is stable; Index is in the source text.</summary>
internal readonly record struct TextDiagnostic(string Code, string Message, int Index);

/// <summary>
/// Breaks a paragraph into lines that fit a width, greedily, with pair kerning.
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
/// clipped or dropped.
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

    public static ParagraphLayout Create(string text, EmbeddedFont font, double size, double width, TextAlign align)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);

        var lines = new List<LayoutLine>();
        var diagnostics = new List<TextDiagnostic>();
        int start = 0;
        while (true)
        {
            int end = FindHardBreak(text, start, out int next);
            var builder = new LineBuilder(font, size, width, align, start, lines, diagnostics);
            builder.Layout(text.AsSpan(start, end - start));
            if (next < 0)
            {
                break;
            }

            start = next;
        }

        return new ParagraphLayout(lines, diagnostics);
    }

    /// <summary>The end of the hard line starting at <paramref name="start"/>, and where the next one starts (-1 if none).</summary>
    private static int FindHardBreak(string text, int start, out int next)
    {
        for (int i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\r':
                    next = i + 1 < text.Length && text[i + 1] == '\n' ? i + 2 : i + 1;
                    return i;
                case '\n':
                case (char)0x2028:
                case (char)0x2029:
                    next = i + 1;
                    return i;
            }
        }

        next = -1;
        return text.Length;
    }

    private static bool IsBreakingSpace(Rune rune) =>
        rune.Value is not (0x00A0 or 0x2007 or 0x202F) && Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator;

    private static bool IsBreakingHyphen(int c) => c is '-' or 0x2010 or 0x2013 or 0x2014;

    /// <summary>Ideographs and kana: a line may break before or after each one (UAX #14 class ID, simplified).</summary>
    private static bool IsIdeographic(int c) =>
        c is (>= 0x3040 and <= 0x30FF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF)
            or (>= 0x20000 and <= 0x3FFFF);

    /// <summary>Lays out one hard line (no line feeds inside) into one or more lines.</summary>
    private sealed class LineBuilder(EmbeddedFont font, double size, double width, TextAlign align, int textOffset, List<LayoutLine> lines, List<TextDiagnostic> diagnostics)
    {
        private readonly List<ushort> _glyphs = [];
        private readonly List<bool> _space = [];
        private readonly List<bool> _breakAfter = [];
        private readonly List<bool> _softHyphenAfter = [];
        private readonly List<int> _source = [];

        // Advances and kerning in thousandths of the font size.
        private double[] _advance = [];
        private double[] _kern = [];

        private double Max => width * 1000 / size;

        public void Layout(ReadOnlySpan<char> line)
        {
            Collect(line);
            Measure();
            if (_glyphs.Count == 0)
            {
                lines.Add(new LayoutLine([], [], 0, 0)); // An empty line still takes its height.
                return;
            }

            int start = 0;
            while (start < _glyphs.Count)
            {
                start = NextLine(start);
            }
        }

        private void Collect(ReadOnlySpan<char> line)
        {
            bool afterHyphen = false;
            int index = 0;
            while (index < line.Length)
            {
                if (Rune.DecodeFromUtf16(line[index..], out Rune rune, out int consumed) != System.Buffers.OperationStatus.Done)
                {
                    throw new PdfException(string.Create(CultureInfo.InvariantCulture, $"The text has a lone surrogate at index {textOffset + index}."));
                }

                int at = textOffset + index;
                index += consumed;
                int c = rune.Value;
                if (c == 0x00AD || c == 0x200B)
                {
                    // Soft hyphen and zero-width space: break opportunities with nothing to draw.
                    if (_glyphs.Count > 0)
                    {
                        _breakAfter[^1] = true;
                        _softHyphenAfter[^1] |= c == 0x00AD;
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
                    _breakAfter[^1] = false; // "10-5", "-5": keep the number with its sign.
                }

                bool space = IsBreakingSpace(rune);
                bool ideograph = IsIdeographic(c);
                if (ideograph && _glyphs.Count > 0 && !_space[^1])
                {
                    _breakAfter[^1] = true;
                }

                if (space && _glyphs.Count > 0 && _space[^1])
                {
                    _breakAfter[^1] = false; // Break after the last space of a run, never inside it.
                }

                bool hyphen = IsBreakingHyphen(c) && _glyphs.Count > 0 && !_space[^1];
                _glyphs.Add(font.Glyph(rune, at));
                _space.Add(space);
                _breakAfter.Add(space || ideograph || hyphen);
                _softHyphenAfter.Add(false);
                _source.Add(at);
                afterHyphen = hyphen;
            }
        }

        private void Measure()
        {
            int count = _glyphs.Count;
            _advance = new double[count];
            _kern = new double[count];
            for (int i = 0; i < count; i++)
            {
                _advance[i] = font.Width(_glyphs[i]);
                _kern[i] = i + 1 < count ? font.Font.Kerning(_glyphs[i], _glyphs[i + 1]) * 1000.0 / font.Font.UnitsPerEm : 0;
            }
        }

        /// <summary>Fills one line from <paramref name="start"/> and returns where the next one starts.</summary>
        private int NextLine(int start)
        {
            double used = 0; // Everything so far, trailing spaces included.
            double visible = 0; // Up to the last glyph that isn't a space.
            int lastBreak = -1;
            double widthAtBreak = 0;
            int i = start;
            for (; i < _glyphs.Count; i++)
            {
                double add = _advance[i] + (i > start ? _kern[i - 1] : 0);
                if (!_space[i] && used + add > Max + Tolerance)
                {
                    break;
                }

                used += add;
                if (!_space[i])
                {
                    visible = used;
                }

                if (_breakAfter[i] && i + 1 < _glyphs.Count)
                {
                    double candidate = visible + (_softHyphenAfter[i] ? HyphenWidth(_glyphs[i]) : 0);
                    if (candidate <= Max + Tolerance)
                    {
                        lastBreak = i;
                        widthAtBreak = candidate;
                    }
                }
            }

            if (i == _glyphs.Count)
            {
                Emit(start, _glyphs.Count - 1, visible, hyphenate: false, last: true);
                return _glyphs.Count;
            }

            if (lastBreak >= start)
            {
                Emit(start, lastBreak, widthAtBreak, hyphenate: _softHyphenAfter[lastBreak], last: false);
                return lastBreak + 1;
            }

            // Nothing fits before the first break: break inside the word, keeping at least one glyph per line.
            int end = Math.Max(i, start + 1);
            double emergency = 0;
            for (int k = start; k < end; k++)
            {
                emergency += _advance[k] + (k > start ? _kern[k - 1] : 0);
            }

            // Once per word: the lines after the first piece of a broken word start mid-word.
            bool wordStart = start == 0 || _breakAfter[start - 1];
            if (wordStart)
            {
                diagnostics.Add(new TextDiagnostic(
                    WordTooWide,
                    string.Create(CultureInfo.InvariantCulture, $"The word at index {_source[start]} is wider than the {width:0.##} pt column at {size:0.##} pt, so it was broken between characters."),
                    _source[start]));
            }

            Emit(start, end - 1, emergency, hyphenate: false, last: end == _glyphs.Count);
            return end;
        }

        private double HyphenWidth(ushort before)
        {
            ushort hyphen = font.Font.GlyphFor('-');
            return hyphen == 0 ? 0 : font.Width(hyphen) + (font.Font.Kerning(before, hyphen) * 1000.0 / font.Font.UnitsPerEm);
        }

        private void Emit(int start, int end, double lineWidth, bool hyphenate, bool last)
        {
            while (end > start && _space[end])
            {
                end--; // Trailing spaces hang and aren't drawn.
            }

            var glyphs = new List<ushort>(end - start + 2);
            var extra = new List<double>(end - start + 2);
            for (int k = start; k <= end; k++)
            {
                glyphs.Add(_glyphs[k]);
                extra.Add(k < end ? _kern[k] : 0);
            }

            if (hyphenate)
            {
                ushort hyphen = font.Glyph(new Rune('-'), _source[end]);
                extra[^1] = font.Font.Kerning(_glyphs[end], hyphen) * 1000.0 / font.Font.UnitsPerEm;
                glyphs.Add(hyphen);
                extra.Add(0);
            }

            double points = lineWidth * size / 1000;
            double offset = align switch
            {
                TextAlign.Right => width - points,
                TextAlign.Center => (width - points) / 2,
                _ => 0,
            };

            if (align == TextAlign.Justify && !last)
            {
                int spaces = 0;
                for (int k = 0; k < glyphs.Count - 1; k++)
                {
                    spaces += _space[start + k] && start + k <= end ? 1 : 0;
                }

                if (spaces > 0 && lineWidth < Max)
                {
                    double each = (Max - lineWidth) / spaces;
                    for (int k = 0; k < glyphs.Count - 1; k++)
                    {
                        if (start + k <= end && _space[start + k])
                        {
                            extra[k] += each;
                        }
                    }

                    points = width;
                }
            }

            lines.Add(new LayoutLine([.. glyphs], [.. extra], points, Math.Max(offset, 0)));
        }
    }
}
