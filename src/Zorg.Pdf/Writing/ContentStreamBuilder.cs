using System;
using System.Buffers;

namespace Zorg.Pdf.Writing;

/// <summary>
/// Builds a page content stream (ISO 32000-1, 8 and 9) one operator per line. Operands use the same number formatting
/// as the object writer, so content is deterministic too.
/// </summary>
internal sealed class ContentStreamBuilder
{
    private readonly ArrayBufferWriter<byte> _buffer = new(1024);

    public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

    /// <summary><c>BT</c>: begins a text object.</summary>
    public ContentStreamBuilder BeginText() => Operator("BT"u8);

    /// <summary><c>ET</c>: ends a text object.</summary>
    public ContentStreamBuilder EndText() => Operator("ET"u8);

    /// <summary><c>Tm</c>: places the start of the next line at (x, y), with no rotation or scale.</summary>
    public ContentStreamBuilder SetTextPosition(double x, double y)
    {
        Write("1 0 0 1 "u8);
        Number(x);
        Number(y);
        return Operator("Tm"u8);
    }

    /// <summary><c>cm</c>: moves the origin to (x, y) for what follows, until the state is restored.</summary>
    public ContentStreamBuilder Translate(double x, double y)
    {
        Write("1 0 0 1 "u8);
        Number(x);
        Number(y);
        return Operator("cm"u8);
    }

    /// <summary><c>cm</c>: concatenates a matrix (a b c d e f) to the current transform.</summary>
    public ContentStreamBuilder Transform(double a, double b, double c, double d, double e, double f)
    {
        Number(a);
        Number(b);
        Number(c);
        Number(d);
        Number(e);
        Number(f);
        return Operator("cm"u8);
    }

    /// <summary><c>gs</c>: applies a graphics state from the page resources, such as an opacity.</summary>
    public ContentStreamBuilder SetGraphicsState(ReadOnlySpan<byte> resourceName)
    {
        PdfFormat.WriteName(_buffer, resourceName);
        Space();
        return Operator("gs"u8);
    }

    /// <summary><c>cm</c>: scales the unit square to <paramref name="width"/> × <paramref name="height"/> at (x, y), to draw an image.</summary>
    public ContentStreamBuilder Place(double x, double y, double width, double height)
    {
        Number(width);
        Write("0 0 "u8);
        Number(height);
        Number(x);
        Number(y);
        return Operator("cm"u8);
    }

    /// <summary><c>Tm</c> with a slant: places the next line at (x, y), sheared by <paramref name="shear"/> (tan of the angle).</summary>
    public ContentStreamBuilder SetTextMatrix(double shear, double x, double y)
    {
        Write("1 0 "u8);
        Number(shear);
        Write("1 "u8);
        Number(x);
        Number(y);
        return Operator("Tm"u8);
    }

    /// <summary><c>Tr</c>: text rendering mode. 0 fills; 2 fills then strokes, which thickens glyphs.</summary>
    public ContentStreamBuilder SetTextRenderingMode(int mode)
    {
        PdfFormat.WriteInteger(_buffer, mode);
        Space();
        return Operator("Tr"u8);
    }

    /// <summary><c>RG</c>: stroke colour in DeviceRGB, each component 0 to 1.</summary>
    public ContentStreamBuilder SetStrokeRgb(double r, double g, double b)
    {
        Number(r);
        Number(g);
        Number(b);
        return Operator("RG"u8);
    }

    /// <summary><c>q</c>: saves the graphics state.</summary>
    public ContentStreamBuilder SaveState() => Operator("q"u8);

    /// <summary><c>Q</c>: restores the graphics state.</summary>
    public ContentStreamBuilder RestoreState() => Operator("Q"u8);

    /// <summary><c>w</c>: line width in points.</summary>
    public ContentStreamBuilder SetLineWidth(double width)
    {
        Number(width);
        return Operator("w"u8);
    }

    /// <summary><c>G</c>: stroke colour as a grey level from 0 (black) to 1 (white).</summary>
    public ContentStreamBuilder SetStrokeGray(double gray)
    {
        Number(gray);
        return Operator("G"u8);
    }

    /// <summary><c>g</c>: fill colour as a grey level from 0 (black) to 1 (white).</summary>
    public ContentStreamBuilder SetFillGray(double gray)
    {
        Number(gray);
        return Operator("g"u8);
    }

    /// <summary><c>rg</c>: fill colour (text and shapes) in DeviceRGB, each component 0 to 1.</summary>
    public ContentStreamBuilder SetFillRgb(double r, double g, double b)
    {
        Number(r);
        Number(g);
        Number(b);
        return Operator("rg"u8);
    }

    /// <summary><c>re f</c>: fills a rectangle.</summary>
    public ContentStreamBuilder FillRectangle(double x, double y, double width, double height)
    {
        Number(x);
        Number(y);
        Number(width);
        Number(height);
        Operator("re"u8);
        return Operator("f"u8);
    }

    /// <summary><c>re S</c>: strokes a rectangle's outline.</summary>
    public ContentStreamBuilder StrokeRectangle(double x, double y, double width, double height)
    {
        Number(x);
        Number(y);
        Number(width);
        Number(height);
        Operator("re"u8);
        return Operator("S"u8);
    }

    /// <summary><c>m l S</c>: strokes a straight line.</summary>
    public ContentStreamBuilder StrokeLine(double x1, double y1, double x2, double y2)
    {
        Number(x1);
        Number(y1);
        Operator("m"u8);
        Number(x2);
        Number(y2);
        Operator("l"u8);
        return Operator("S"u8);
    }

    /// <summary><c>Do</c>: draws an XObject (an image or a form) from the page resources.</summary>
    public ContentStreamBuilder DrawXObject(ReadOnlySpan<byte> resourceName)
    {
        PdfFormat.WriteName(_buffer, resourceName);
        Space();
        return Operator("Do"u8);
    }

    /// <summary><c>BDC</c> with an MCID: starts marked content that belongs to a structure element.</summary>
    public ContentStreamBuilder BeginMarkedContent(ReadOnlySpan<byte> tag, int mcid)
    {
        PdfFormat.WriteName(_buffer, tag);
        Write(" <</MCID "u8);
        PdfFormat.WriteInteger(_buffer, mcid);
        Write(">> "u8);
        return Operator("BDC"u8);
    }

    /// <summary><c>/Span &lt;&lt;/Lang (de)&gt;&gt; BDC</c>: a passage in another language; end it with <see cref="EndMarkedContent"/>.</summary>
    public ContentStreamBuilder BeginSpan(string language)
    {
        Write("/Span <</Lang "u8);
        PdfFormat.WriteLiteralString(_buffer, System.Text.Encoding.ASCII.GetBytes(language));
        Write(">> "u8);
        return Operator("BDC"u8);
    }

    /// <summary><c>/Span &lt;&lt;/ActualText &lt;FEFF...&gt;&gt;&gt; BDC</c>: drawn content whose text is <paramref name="text"/>; end it with <see cref="EndMarkedContent"/>.</summary>
    public ContentStreamBuilder BeginActualText(string text)
    {
        Write("/Span <</ActualText "u8);
        byte[] utf16 = new byte[2 + (text.Length * 2)];
        (utf16[0], utf16[1]) = (0xFE, 0xFF);
        System.Text.Encoding.BigEndianUnicode.GetBytes(text, 0, text.Length, utf16, 2);
        PdfFormat.WriteHexString(_buffer, utf16);
        Write(">> "u8);
        return Operator("BDC"u8);
    }

    /// <summary>
    /// <c>BDC</c> for an artifact: content that isn't part of the document's text, such as page numbers, rules and
    /// backgrounds. Screen readers skip it. <paramref name="subtype"/> is Header, Footer or Watermark for pagination
    /// artifacts, or empty for a plain layout artifact.
    /// </summary>
    public ContentStreamBuilder BeginArtifact(ReadOnlySpan<byte> type, ReadOnlySpan<byte> subtype)
    {
        Write("/Artifact <</Type "u8);
        PdfFormat.WriteName(_buffer, type);
        if (!subtype.IsEmpty)
        {
            Write(" /Subtype "u8);
            PdfFormat.WriteName(_buffer, subtype);
        }

        Write(">> "u8);
        return Operator("BDC"u8);
    }

    /// <summary><c>EMC</c>: ends marked content.</summary>
    public ContentStreamBuilder EndMarkedContent() => Operator("EMC"u8);

    /// <summary><c>Tf</c>: selects a font resource and size.</summary>
    public ContentStreamBuilder SetFont(ReadOnlySpan<byte> resourceName, double size)
    {
        PdfFormat.WriteName(_buffer, resourceName);
        Space();
        PdfFormat.WriteReal(_buffer, size);
        Space();
        return Operator("Tf"u8);
    }

    /// <summary><c>Td</c>: moves to the start of the next line, offset from the start of the current one.</summary>
    public ContentStreamBuilder MoveText(double x, double y)
    {
        PdfFormat.WriteReal(_buffer, x);
        Space();
        PdfFormat.WriteReal(_buffer, y);
        Space();
        return Operator("Td"u8);
    }

    /// <summary><c>Tj</c>: shows a string, already encoded for the current font.</summary>
    public ContentStreamBuilder ShowText(ReadOnlySpan<byte> encoded)
    {
        PdfFormat.WriteLiteralString(_buffer, encoded);
        Space();
        return Operator("Tj"u8);
    }

    /// <summary><c>Tj</c> with 2-byte glyph IDs, for fonts with <c>Identity-H</c> encoding.</summary>
    public ContentStreamBuilder ShowGlyphs(ReadOnlySpan<ushort> glyphs)
    {
        byte[] bytes = new byte[glyphs.Length * 2];
        for (int i = 0; i < glyphs.Length; i++)
        {
            bytes[i * 2] = (byte)(glyphs[i] >> 8);
            bytes[(i * 2) + 1] = (byte)glyphs[i];
        }

        PdfFormat.WriteHexString(_buffer, bytes);
        Space();
        return Operator("Tj"u8);
    }

    /// <summary>
    /// <c>TJ</c>: shows 2-byte glyph IDs with extra space after some of them, for kerning and justification.
    /// <paramref name="extraAfter"/> is in thousandths of the font size, positive for more space. Runs with no
    /// adjustment share one string, and a line with none at all is written as a plain <c>Tj</c>.
    /// </summary>
    public ContentStreamBuilder ShowGlyphs(ReadOnlySpan<ushort> glyphs, ReadOnlySpan<double> extraAfter)
    {
        if (extraAfter.Length != glyphs.Length)
        {
            throw new ArgumentException("There must be one adjustment per glyph.", nameof(extraAfter));
        }

        if (glyphs.IsEmpty)
        {
            return this;
        }

        bool any = false;
        foreach (double extra in extraAfter[..^1])
        {
            any |= Math.Round(extra, 4) != 0;
        }

        if (!any)
        {
            return ShowGlyphs(glyphs);
        }

        _buffer.Write("["u8);
        int runStart = 0;
        for (int i = 0; i < glyphs.Length; i++)
        {
            bool last = i == glyphs.Length - 1;
            double extra = last ? 0 : Math.Round(extraAfter[i], 4);
            if (extra != 0 || last)
            {
                WriteGlyphRun(glyphs[runStart..(i + 1)]);
                if (extra != 0)
                {
                    // TJ numbers move the next glyph left, so more space is a negative number.
                    PdfFormat.WriteReal(_buffer, -extra);
                }

                runStart = i + 1;
            }
        }

        _buffer.Write("]"u8);
        Space();
        return Operator("TJ"u8);
    }

    /// <summary><c>m</c>: starts a new subpath at (x, y).</summary>
    public ContentStreamBuilder MoveTo(double x, double y)
    {
        Number(x);
        Number(y);
        return Operator("m"u8);
    }

    /// <summary><c>l</c>: a straight segment to (x, y).</summary>
    public ContentStreamBuilder LineTo(double x, double y)
    {
        Number(x);
        Number(y);
        return Operator("l"u8);
    }

    /// <summary><c>c</c>: a cubic Bézier segment through two control points to (x, y).</summary>
    public ContentStreamBuilder CurveTo(double x1, double y1, double x2, double y2, double x, double y)
    {
        Number(x1);
        Number(y1);
        Number(x2);
        Number(y2);
        Number(x);
        Number(y);
        return Operator("c"u8);
    }

    /// <summary><c>h</c>: closes the current subpath with a straight segment to its start.</summary>
    public ContentStreamBuilder ClosePath() => Operator("h"u8);

    /// <summary><c>re</c>: appends a closed rectangle to the path, without painting it.</summary>
    public ContentStreamBuilder Rectangle(double x, double y, double width, double height)
    {
        Number(x);
        Number(y);
        Number(width);
        Number(height);
        return Operator("re"u8);
    }

    /// <summary><c>f</c> or <c>f*</c>: fills the path with the non-zero or even-odd rule.</summary>
    public ContentStreamBuilder Fill(bool evenOdd = false) => Operator(evenOdd ? "f*"u8 : "f"u8);

    /// <summary><c>S</c>: strokes the path.</summary>
    public ContentStreamBuilder Stroke() => Operator("S"u8);

    /// <summary><c>B</c> or <c>B*</c>: fills, then strokes the path.</summary>
    public ContentStreamBuilder FillAndStroke(bool evenOdd = false) => Operator(evenOdd ? "B*"u8 : "B"u8);

    /// <summary><c>n</c>: ends the path without painting it (after a clip).</summary>
    public ContentStreamBuilder EndPath() => Operator("n"u8);

    /// <summary><c>W</c> or <c>W*</c>: intersects the clip with the path; follow with a painting operator or <see cref="EndPath"/>.</summary>
    public ContentStreamBuilder Clip(bool evenOdd = false) => Operator(evenOdd ? "W*"u8 : "W"u8);

    /// <summary><c>J</c>: line ends: 0 butt (the default), 1 round, 2 projecting square.</summary>
    public ContentStreamBuilder SetLineCap(int cap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cap, 2);
        Number(cap);
        return Operator("J"u8);
    }

    /// <summary><c>j</c>: corners: 0 miter (the default), 1 round, 2 bevel.</summary>
    public ContentStreamBuilder SetLineJoin(int join)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(join, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(join, 2);
        Number(join);
        return Operator("j"u8);
    }

    /// <summary><c>d</c>: a dash pattern of on and off lengths, starting <paramref name="phase"/> into it; empty for solid.</summary>
    public ContentStreamBuilder SetDash(ReadOnlySpan<double> pattern, double phase = 0)
    {
        Write("["u8);
        for (int i = 0; i < pattern.Length; i++)
        {
            if (i > 0)
            {
                Space();
            }

            PdfFormat.WriteReal(_buffer, pattern[i]);
        }

        Write("] "u8);
        Number(phase);
        return Operator("d"u8);
    }

    private void WriteGlyphRun(ReadOnlySpan<ushort> glyphs)
    {
        byte[] bytes = new byte[glyphs.Length * 2];
        for (int i = 0; i < glyphs.Length; i++)
        {
            bytes[i * 2] = (byte)(glyphs[i] >> 8);
            bytes[(i * 2) + 1] = (byte)glyphs[i];
        }

        PdfFormat.WriteHexString(_buffer, bytes);
    }

    private void Number(double value)
    {
        PdfFormat.WriteReal(_buffer, value);
        Space();
    }

    private void Write(ReadOnlySpan<byte> ascii) => _buffer.Write(ascii);

    private void Space()
    {
        _buffer.GetSpan(1)[0] = (byte)' ';
        _buffer.Advance(1);
    }

    private ContentStreamBuilder Operator(ReadOnlySpan<byte> op)
    {
        _buffer.Write(op);
        _buffer.GetSpan(1)[0] = (byte)'\n';
        _buffer.Advance(1);
        return this;
    }
}
