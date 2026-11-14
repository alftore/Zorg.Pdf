using System;
using System.Collections.Generic;
using System.Numerics;
using Zorg.Imaging;
using Bitmap = Zorg.Imaging.Image;
using Zorg.Imaging.Drawing;

namespace Zorg.Pdf.Imaging.Preview;

/// <summary>
/// Draws one page's content streams onto a canvas: the operators <c>ContentStreamBuilder</c> writes (graphics state,
/// rectangles and lines, colours, opacity, text with Type0 fonts, images, forms). Anything else is refused, naming it.
/// </summary>
internal sealed class PageRasterizer(PreviewFile file, PdfDictionary resources, Canvas canvas, PdfPreviewLimits limits)
{
    private const int MaxStateDepth = 64;
    private const float SyntheticBoldEm = 0.03f;

    private readonly Stack<State> _saved = new();
    private readonly Dictionary<string, PreviewFont> _fonts = [];
    private readonly Dictionary<string, Bitmap> _images = [];
    private readonly List<PdfValue> _operands = [];
    private State _state = new();
    private Shape _path = new();

    /// <summary>The path so far when it's one straight segment (a table rule): drawn with square ends, as PDF's default butt caps.</summary>
    private (Vector2 Start, Vector2? End)? _segment;
    private Matrix3x2 _textMatrix = Matrix3x2.Identity;
    private Matrix3x2 _lineMatrix = Matrix3x2.Identity;
    private int _operators;
    private int _depth;

    public void Draw(byte[] content)
    {
        var lexer = new PdfLexer(content);
        while (lexer.Read(operators: true) is { } value)
        {
            if (value is not PdfOperator op)
            {
                if (_operands.Count > 64)
                {
                    throw PdfPreviewException.Malformed("An operator has too many operands.");
                }

                _operands.Add(value);
                continue;
            }

            if (++_operators > limits.MaxOperators)
            {
                throw new PdfPreviewException($"The page has more than {limits.MaxOperators:N0} operators (PdfPreviewLimits.MaxOperators).");
            }

            Execute(op.Name);
            _operands.Clear();
        }
    }

    public void Dispose()
    {
        foreach (Bitmap image in _images.Values)
        {
            image.Dispose();
        }
    }

    private float N(int i) => i < _operands.Count && _operands[i] is PdfNumber n ? (float)n.Value : throw PdfPreviewException.Malformed("An operator is missing a number.");

    private Matrix3x2 Operands6() => new(N(0), N(1), N(2), N(3), N(4), N(5));

    private void Execute(string op)
    {
        switch (op)
        {
            case "q":
                if (_saved.Count >= MaxStateDepth)
                {
                    throw PdfPreviewException.Malformed("Graphics states are nested too deeply.");
                }

                _saved.Push(_state);
                break;
            case "Q":
                _state = _saved.Count > 0 ? _saved.Pop() : _state;
                break;
            case "cm":
                _state = _state with { Ctm = Operands6() * _state.Ctm };
                break;
            case "w":
                _state = _state with { LineWidth = N(0) };
                break;
            case "g":
                _state = _state with { Fill = Gray(N(0)) };
                break;
            case "G":
                _state = _state with { Stroke = Gray(N(0)) };
                break;
            case "rg":
                _state = _state with { Fill = Rgb(N(0), N(1), N(2)) };
                break;
            case "RG":
                _state = _state with { Stroke = Rgb(N(0), N(1), N(2)) };
                break;
            case "gs":
                ExtGState();
                break;
            case "re":
                _segment = null;
                _path.MoveTo(N(0), N(1)).LineTo(N(0) + N(2), N(1)).LineTo(N(0) + N(2), N(1) + N(3)).LineTo(N(0), N(1) + N(3)).Close();
                break;
            case "m":
                _segment = _path.IsEmpty ? (new Vector2(N(0), N(1)), null) : null;
                _path.MoveTo(N(0), N(1));
                break;
            case "l":
                _segment = _segment is ({ } start, null) ? (start, new Vector2(N(0), N(1))) : null;
                _path.LineTo(N(0), N(1));
                break;
            case "c":
                _segment = null;
                _path.CubicTo(N(0), N(1), N(2), N(3), N(4), N(5));
                break;
            case "h":
                _segment = null;
                _path.Close();
                break;
            case "f" or "F" or "f*":
                if (!_path.IsEmpty)
                {
                    canvas.Fill(_path, WithAlpha(_state.Fill, _state.FillAlpha), _state.Ctm, op == "f*" ? FillRule.EvenOdd : FillRule.NonZero);
                }

                _path = new Shape();
                break;
            case "B" or "B*":
                if (!_path.IsEmpty)
                {
                    canvas.Fill(_path, WithAlpha(_state.Fill, _state.FillAlpha), _state.Ctm, op == "B*" ? FillRule.EvenOdd : FillRule.NonZero);
                }

                Stroke();
                _path = new Shape();
                _segment = null;
                break;
            case "J" or "j" or "M" or "d":
                break; // Caps, joins and dashes: the canvas strokes solid, square-ended rules and round-cornered paths.
            case "S":
                Stroke();
                _path = new Shape();
                _segment = null;
                break;
            case "n":
                _path = new Shape();
                _segment = null;
                break;
            case "BT":
                _textMatrix = _lineMatrix = Matrix3x2.Identity;
                break;
            case "ET":
                break;
            case "Tf":
                _state = _state with { Font = _operands.Count > 0 && _operands[0] is PdfName name ? Font(name.Value) : throw PdfPreviewException.Malformed("Tf needs a font name."), FontSize = N(1) };
                break;
            case "Tm":
                _textMatrix = _lineMatrix = Operands6();
                break;
            case "Td":
                _lineMatrix = Matrix3x2.CreateTranslation(N(0), N(1)) * _lineMatrix;
                _textMatrix = _lineMatrix;
                break;
            case "Tr":
                _state = _state with { RenderingMode = (int)N(0) };
                break;
            case "Tj":
                Show(_operands.Count > 0 && _operands[0] is PdfString s ? s.Value : throw PdfPreviewException.Malformed("Tj needs a string."));
                break;
            case "TJ":
                if (_operands.Count == 0 || _operands[0] is not PdfArray parts)
                {
                    throw PdfPreviewException.Malformed("TJ needs an array.");
                }

                foreach (PdfValue part in parts.Items)
                {
                    if (part is PdfString text)
                    {
                        Show(text.Value);
                    }
                    else if (part is PdfNumber adjust)
                    {
                        _textMatrix = Matrix3x2.CreateTranslation((float)(-adjust.Value / 1000 * _state.FontSize), 0) * _textMatrix;
                    }
                }

                break;
            case "Do":
                DrawXObject(_operands.Count > 0 && _operands[0] is PdfName xobject ? xobject.Value : throw PdfPreviewException.Malformed("Do needs a name."));
                break;
            case "BDC" or "BMC" or "EMC" or "MP" or "DP":
                break; // Marked content: structure, not drawing.
            default:
                throw new PdfPreviewException($"The page uses the operator \"{op}\", which Zorg.Pdf never writes: previews read only files Zorg.Pdf wrote.");
        }
    }

    /// <summary>
    /// Strokes the path. Only a hairline (<c>0 w</c>, "the thinnest line the device can draw") is widened to a pixel;
    /// thinner lines than that are drawn at their width, faint, as MuPDF and pdf.js draw them.
    /// </summary>
    private void Stroke()
    {
        if (_path.IsEmpty)
        {
            return;
        }

        float width = Math.Max(_state.LineWidth, 0);
        Rgba32 color = WithAlpha(_state.Stroke, _state.StrokeAlpha);
        canvas.MinimumStrokeWidth = width == 0 ? 1 : 0;
        if (_segment is ({ } start, { } end))
        {
            Vector2 a = Vector2.Transform(start, _state.Ctm), b = Vector2.Transform(end, _state.Ctm);
            float scale = MathF.Sqrt(MathF.Abs(_state.Ctm.GetDeterminant()));
            canvas.StrokeLine(a.X, a.Y, b.X, b.Y, width * scale, color);
        }
        else
        {
            canvas.Stroke(_path, width, color, _state.Ctm);
        }
    }

    private void ExtGState()
    {
        string name = _operands.Count > 0 && _operands[0] is PdfName n ? n.Value : throw PdfPreviewException.Malformed("gs needs a name.");
        PdfDictionary state = file.Dictionary(file.Dictionary(resources["ExtGState"])?[name]) ?? throw PdfPreviewException.Malformed($"The graphics state {name} isn't in the page's resources.");
        _state = _state with
        {
            FillAlpha = file.Resolve(state["ca"]) is PdfNumber ca ? Math.Clamp(ca.Value, 0, 1) : _state.FillAlpha,
            StrokeAlpha = file.Resolve(state["CA"]) is PdfNumber sa ? Math.Clamp(sa.Value, 0, 1) : _state.StrokeAlpha,
        };
    }

    private PreviewFont Font(string name)
    {
        if (!_fonts.TryGetValue(name, out PreviewFont? font))
        {
            PdfDictionary dictionary = file.Dictionary(file.Dictionary(resources["Font"])?[name]) ?? throw PdfPreviewException.Malformed($"The font {name} isn't in the page's resources.");
            font = PreviewFont.Load(file, dictionary);
            _fonts[name] = font;
        }

        return font;
    }

    private void Show(byte[] text)
    {
        PreviewFont font = _state.Font ?? throw PdfPreviewException.Malformed("Text is shown before a font is set.");
        float size = (float)_state.FontSize;
        float unit = size / font.UnitsPerEm;
        Rgba32 fill = WithAlpha(_state.Fill, _state.FillAlpha);
        for (int i = 0; i + 1 < text.Length; i += 2)
        {
            int glyph = (text[i] << 8) | text[i + 1];
            if (font.Outline(glyph) is { } outline && _state.RenderingMode is 0 or 2)
            {
                Matrix3x2 place = Matrix3x2.CreateScale(unit) * _textMatrix * _state.Ctm;
                canvas.Fill(outline, fill, place);
                if (_state.RenderingMode == 2)
                {
                    canvas.MinimumStrokeWidth = 0;
                    canvas.Stroke(outline, SyntheticBoldEm * font.UnitsPerEm, WithAlpha(_state.Stroke, _state.StrokeAlpha), place);
                }
            }

            _textMatrix = Matrix3x2.CreateTranslation((float)(font.Width(glyph) / 1000 * size), 0) * _textMatrix;
        }
    }

    private void DrawXObject(string name)
    {
        PdfValue? reference = file.Dictionary(resources["XObject"])?[name];
        PdfStream stream = file.Stream(reference) ?? throw PdfPreviewException.Malformed($"The XObject {name} isn't in the page's resources.");
        if (file.Resolve(stream.Dictionary["Subtype"]) is PdfName { Value: "Form" })
        {
            DrawForm(stream);
            return;
        }

        if (file.Resolve(stream.Dictionary["Subtype"]) is not PdfName { Value: "Image" })
        {
            throw PdfPreviewException.Malformed($"The XObject {name} is neither an image nor a form.");
        }

        if (!_images.TryGetValue(name, out Bitmap? image))
        {
            image = PreviewImages.Load(file, stream, limits);
            _images[name] = image;
        }

        // Image space: the unit square, its first row at the top.
        Matrix3x2 place = Matrix3x2.CreateScale(1f / image.Width, -1f / image.Height) * Matrix3x2.CreateTranslation(0, 1) * _state.Ctm;
        canvas.DrawImage(image, place, opacity: _state.FillAlpha);
    }

    /// <summary>A form XObject, such as a letterhead background: its content drawn with its own resources, through its matrix.</summary>
    private void DrawForm(PdfStream stream)
    {
        if (_depth >= 4)
        {
            throw PdfPreviewException.Malformed("Forms are nested too deeply.");
        }

        Matrix3x2 matrix = Matrix3x2.Identity;
        if (file.Resolve(stream.Dictionary["Matrix"]) is PdfArray { Items.Count: 6 } m)
        {
            float V(int i) => (float)file.Resolve(m.Items[i]).Number;
            matrix = new Matrix3x2(V(0), V(1), V(2), V(3), V(4), V(5));
        }

        var inner = new PageRasterizer(file, file.Dictionary(stream.Dictionary["Resources"]) ?? resources, canvas, limits)
        {
            _depth = _depth + 1,
            _state = _state with { Ctm = matrix * _state.Ctm },
        };
        try
        {
            inner.Draw(file.Decode(stream));
        }
        finally
        {
            inner.Dispose();
        }
    }

    private static Rgba32 Gray(float g) => Rgb(g, g, g);

    private static Rgba32 Rgb(float r, float g, float b) =>
        new((byte)Math.Round(Math.Clamp(r, 0, 1) * 255), (byte)Math.Round(Math.Clamp(g, 0, 1) * 255), (byte)Math.Round(Math.Clamp(b, 0, 1) * 255));

    private static Rgba32 WithAlpha(Rgba32 color, double alpha) => new(color.R, color.G, color.B, (byte)Math.Round(alpha * 255));

    /// <summary>The graphics state <c>q</c> saves and <c>Q</c> restores.</summary>
    private sealed record State
    {
        public Matrix3x2 Ctm { get; init; } = Matrix3x2.Identity;

        public Rgba32 Fill { get; init; } = new(0, 0, 0);

        public Rgba32 Stroke { get; init; } = new(0, 0, 0);

        public double FillAlpha { get; init; } = 1;

        public double StrokeAlpha { get; init; } = 1;

        public float LineWidth { get; init; } = 1;

        public PreviewFont? Font { get; init; }

        public double FontSize { get; init; }

        public int RenderingMode { get; init; }
    }
}
