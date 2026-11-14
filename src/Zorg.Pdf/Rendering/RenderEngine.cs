using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Layout;
using Zorg.Pdf.Validation;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// Renders a document: checks everything it can before writing (validation, fonts, glyphs), then lays out blocks,
/// paginates them, tags them, and streams pages to the output as they finish.
/// </summary>
/// <remarks>
/// The pipeline is model → <see cref="Preflight"/> → <see cref="FontSet"/> → layout (<see cref="ParagraphLayout"/>) →
/// pagination (this class) → page content and tagging (<see cref="Tagger"/>) → <see cref="DocumentWriter"/> →
/// <see cref="PdfObjectWriter"/>. With <c>synchronous</c> set, every step completes without a pending task, so the
/// sync API never blocks on one.
/// </remarks>
internal sealed class RenderEngine : IDisposable
{
    /// <summary>Stroke width of a synthetic bold face, as a share of the font size: about the weight step from regular to bold.</summary>
    private const double SyntheticBoldStroke = 0.03;

    /// <summary>Slant of a synthetic italic face: tan 12°, the usual oblique angle.</summary>
    private const double SyntheticItalicShear = 0.2126;

    private readonly PdfDocument _document;
    private readonly RenderOptions _options;
    private readonly StyleResolver _styles;
    private readonly FontSet _fonts;
    private readonly string? _defaultFamily;
    private readonly DiagnosticSink _sink;
    private readonly PdfObjectWriter _writer;
    private readonly DocumentWriter _documentWriter;
    private readonly Tagger _tagger;
    private readonly PageGeometry _geometry;
    private readonly Dictionary<int, PreparedText> _prepared = [];
    private readonly HashSet<EmbeddedFont> _pageFonts = [];

    private ContentStreamBuilder _content = new();
    private int _page = -1;
    private double _y;
    private bool _pageEmpty = true;
    private Color _fill = Color.Black; // The page's fill colour: graphics state outlives BT/ET.
    private Color? _stroke;
    private double _strokeWidth = 1;
    private int _renderingMode; // Text state outlives BT/ET too.

    private RenderEngine(PdfDocument document, RenderOptions options, StyleResolver styles, FontSet fonts, string? defaultFamily, DiagnosticSink sink, Stream output, bool synchronous)
    {
        _document = document;
        _options = options;
        _styles = styles;
        _fonts = fonts;
        _defaultFamily = defaultFamily;
        _sink = sink;
        _writer = new PdfObjectWriter(output, PdfVersion.Pdf17, synchronous);
        _geometry = PageGeometry.From(document.Page);
        _documentWriter = new DocumentWriter(_writer, _geometry);
        _tagger = new Tagger(_writer);
    }

    public static async ValueTask<RenderResult> RenderAsync(PdfDocument document, Stream output, RenderOptions? options, bool synchronous, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        options ??= RenderOptions.Default;
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable.", nameof(output));
        }

        var sink = new DiagnosticSink();
        sink.AddRange(document.Validate().Diagnostics);
        var styles = new StyleResolver(document.Styles);
        string? defaultFamily = styles.DefaultFontFamily ?? options.Fonts.DefaultFamily;
        if (options.Fonts.Families.IsEmpty && options.FontResolver is null)
        {
            sink.Error(DiagnosticCodes.NoFonts, "The render has no fonts, and there are no system fonts by default. Add one: new RenderOptions { Fonts = FontCollection.Empty.Add(new FontFamily(\"Inter\") { Regular = FontSource.FromFile(\"Inter-Regular.ttf\") }) }, or opt in to installed fonts with FontResolver = FontResolvers.System().", "");
        }

        Dictionary<FaceKey, string> needed = sink.HasErrors ? [] : Preflight.NeededFaces(document, styles, defaultFamily, sink);
        FontSet fonts = await FontSet.LoadAsync(options, needed, sink, synchronous, cancellationToken).ConfigureAwait(false);
        Preflight.Check(document, styles, fonts, defaultFamily, options.Conformance, sink);
        if (sink.HasErrors)
        {
            throw new PdfDocumentException(sink.ToImmutable());
        }

        using var engine = new RenderEngine(document, options, styles, fonts, defaultFamily, sink, output, synchronous);
        await engine.RunAsync(cancellationToken).ConfigureAwait(false);
        return new RenderResult(engine._documentWriter.Pages.Count, engine._writer.Position, sink.ToImmutable());
    }

    public void Dispose() => _writer.Dispose();

    private async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        StartPage();
        for (int i = 0; i < _document.Content.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (_document.Content[i])
            {
                case Heading or Paragraph:
                    await PlaceTextAsync(i, cancellationToken).ConfigureAwait(false);
                    break;
                case Spacer spacer when !_pageEmpty:
                    _y -= spacer.Height.Points;
                    if (_y < _geometry.Bottom)
                    {
                        await NewPageAsync(cancellationToken).ConfigureAwait(false);
                    }

                    break;
                case PageBreak when !_pageEmpty:
                    await NewPageAsync(cancellationToken).ConfigureAwait(false);
                    break;
            }

            _prepared.Remove(i);
        }

        await FinishPageAsync(cancellationToken).ConfigureAwait(false);
        foreach ((EmbeddedFont font, _, PdfReference reference) in _fonts.Used)
        {
            font.Write(_writer, reference);
        }

        _tagger.Finish(_documentWriter.Pages);
        PdfReference info = _documentWriter.Finish(_document, _options, _tagger.StructTreeRoot);
        await _writer.FinishAsync(_documentWriter.Catalog, info, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Places a heading or paragraph line by line, breaking pages as needed. A heading, or a block with
    /// <see cref="Block.KeepWithNext"/>, moves to a new page when it and the first line of the next text block don't
    /// both fit.
    /// </summary>
    private async ValueTask PlaceTextAsync(int index, CancellationToken cancellationToken)
    {
        PreparedText text = Prepare(index);
        if (text.Keep && !_pageEmpty && NextText(index) is { } next)
        {
            double needed = text.Height + text.SpaceAfter + next.FirstLineHeight;
            if (_y - needed < _geometry.Bottom && needed <= _geometry.ContentHeight)
            {
                await NewPageAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (!_pageEmpty)
        {
            _y -= text.SpaceBefore;
        }

        Tagger.Element? element = null;
        bool open = false;
        EmbeddedFont? font = null;
        double size = 0;
        foreach (LayoutLine line in text.Lines)
        {
            if (_y - line.Height < _geometry.Bottom && !_pageEmpty)
            {
                if (open)
                {
                    EndText();
                    open = false;
                }

                await NewPageAsync(cancellationToken).ConfigureAwait(false);
            }

            if (line.Runs.Count > 0)
            {
                if (!open)
                {
                    element ??= _tagger.Begin(text.Role);
                    _content.BeginMarkedContent(Tagger.Name(text.Role), _tagger.Mark(element, _page)).BeginText();
                    (font, size) = (null, 0); // A new text object: set the font again.
                    open = true;
                }

                foreach (LineRun run in line.Runs)
                {
                    if (!ReferenceEquals(run.Font, font) || run.Size != size)
                    {
                        _content.SetFont(_fonts.Use(run.Font, _writer), run.Size);
                        (font, size) = (run.Font, run.Size);
                        _pageFonts.Add(run.Font);
                    }

                    Color color = run.Color ?? Color.Black;
                    if (color != _fill)
                    {
                        _content.SetFillRgb(color.R / 255.0, color.G / 255.0, color.B / 255.0);
                        _fill = color;
                    }

                    // A synthetic bold face fills and strokes the outline in the text colour; mode 0 is plain fill.
                    int mode = run.SyntheticBold ? 2 : 0;
                    if (run.SyntheticBold && (_stroke != color || _strokeWidth != run.Size * SyntheticBoldStroke))
                    {
                        _content.SetStrokeRgb(color.R / 255.0, color.G / 255.0, color.B / 255.0).SetLineWidth(run.Size * SyntheticBoldStroke);
                        (_stroke, _strokeWidth) = (color, run.Size * SyntheticBoldStroke);
                    }

                    if (mode != _renderingMode)
                    {
                        _content.SetTextRenderingMode(mode);
                        _renderingMode = mode;
                    }

                    double x = _geometry.Left + line.Offset + run.X;
                    double baseline = _y - line.Ascent;
                    if (run.SyntheticItalic)
                    {
                        _content.SetTextMatrix(SyntheticItalicShear, x, baseline);
                    }
                    else
                    {
                        _content.SetTextPosition(x, baseline);
                    }

                    _content.ShowGlyphs(run.Glyphs, run.ExtraAfter);
                }
            }

            _y -= line.Height;
            _pageEmpty = false;
        }

        if (open)
        {
            EndText();
        }

        if (element is not null)
        {
            _tagger.End(element, _documentWriter.Pages);
        }

        _y -= text.SpaceAfter;
    }

    /// <summary>Ends a text object and its marked content, back in fill mode.</summary>
    private void EndText()
    {
        if (_renderingMode != 0)
        {
            _content.SetTextRenderingMode(0);
            _renderingMode = 0;
        }

        _content.EndText().EndMarkedContent();
    }

    /// <summary>The next block, if it's text with nothing but spacers before it; null otherwise.</summary>
    private PreparedText? NextText(int index)
    {
        for (int i = index + 1; i < _document.Content.Length; i++)
        {
            switch (_document.Content[i])
            {
                case Heading or Paragraph:
                    return Prepare(i);
                case Spacer:
                    continue;
                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>Lays out a heading or paragraph once; the result is kept until the block is placed.</summary>
    private PreparedText Prepare(int index)
    {
        if (_prepared.TryGetValue(index, out PreparedText? cached))
        {
            return cached;
        }

        Block block = _document.Content[index];
        string path = $"/content/{index}";
        TextStyle style = _styles.For(block);
        double size = style.Size?.Points ?? 10;
        ImmutableArray<Inline> inlines = Preflight.Inlines(block)!.Value;
        (TextAlign align, StructureRole role, double before, double after) = block switch
        {
            Heading heading => (heading.Align, Tagger.Heading(heading.Level), size * 0.8, size * 0.4),
            Paragraph paragraph => (paragraph.Align, StructureRole.P, 0.0, paragraph.SpaceAfter?.Points ?? size * 0.6),
            _ => throw new InvalidOperationException($"Block {block.GetType().Name} isn't text."),
        };

        // One segment per run, in its own face, size and colour. Offsets map layout diagnostics back to their run.
        var segments = new List<TextSegment>();
        var starts = new List<(int Offset, int Inline)>();
        int offset = 0;
        for (int j = 0; j < inlines.Length; j++)
        {
            starts.Add((offset, j));
            (string? text, TextStyle runStyle) = inlines[j] switch
            {
                TextRun run => (run.Text, Preflight.RunStyle(style, run)),
                LineBreak => ("\n", style),
                _ => (null, style),
            };
            if (text is not null)
            {
                segments.Add(Segment(text, runStyle));
                offset += text.Length;
            }
        }

        if (segments.Count == 0)
        {
            segments.Add(Segment("", style)); // An empty paragraph still has a line height.
        }

        ParagraphLayout layout = ParagraphLayout.Create(segments, _geometry.ContentWidth, align, style.LineHeight ?? 1.2);
        foreach (TextDiagnostic diagnostic in layout.Diagnostics)
        {
            (int start, int inline) = starts.FindLast(s => s.Offset <= diagnostic.Index);
            _sink.Warning(
                diagnostic.Code,
                string.Create(CultureInfo.InvariantCulture, $"The word at index {diagnostic.Index - start} is wider than the page's {_geometry.ContentWidth:0.##} pt text width, so it was broken between characters."),
                $"{path}/inlines/{inline}");
        }

        var prepared = new PreparedText(role, layout.Lines, before, after, block is Heading || block.KeepWithNext);
        _prepared[index] = prepared;
        return prepared;

        TextSegment Segment(string text, TextStyle runStyle)
        {
            FaceKey face = Preflight.Face(runStyle, _styles, _defaultFamily)!.Value;
            _fonts.TryGet(face, out FontChain chain); // Preflight made sure every face loaded.
            return new TextSegment(text, chain, runStyle.Size?.Points ?? size, runStyle.Color);
        }
    }

    private async ValueTask NewPageAsync(CancellationToken cancellationToken)
    {
        await FinishPageAsync(cancellationToken).ConfigureAwait(false);
        StartPage();
    }

    private void StartPage()
    {
        if (_documentWriter.Pages.Count >= _options.Limits.MaxPages)
        {
            throw new PdfLimitException(string.Create(CultureInfo.InvariantCulture, $"The document needs more than {_options.Limits.MaxPages:N0} pages (RenderOptions.Limits.MaxPages). The output has the pages written so far and isn't a complete file."));
        }

        _documentWriter.AddPage();
        _page++;
        _tagger.StartPage();
        _content = new ContentStreamBuilder();
        _pageFonts.Clear();
        _y = _geometry.Top;
        _pageEmpty = true;
        _fill = Color.Black;
        (_stroke, _strokeWidth, _renderingMode) = (null, 1, 0);
    }

    /// <summary>Writes the page, flushes when enough has built up, and reports progress.</summary>
    private async ValueTask FinishPageAsync(CancellationToken cancellationToken)
    {
        var fonts = new List<(byte[] Name, PdfReference Font)>();
        foreach (EmbeddedFont font in _pageFonts)
        {
            fonts.Add((_fonts.Use(font, _writer), _fonts.Reference(font)));
        }

        fonts.Sort((a, b) => a.Font.Number.CompareTo(b.Font.Number)); // Deterministic order.
        _documentWriter.WritePage(_page, _content.WrittenSpan, fonts);
        await _writer.FlushIfNeededAsync(cancellationToken).ConfigureAwait(false);
        _options.Progress?.Report(new RenderProgress(_page + 1, _writer.Position));
    }

    /// <summary>A heading or paragraph laid out at the page's text width, ready to place.</summary>
    private sealed record PreparedText(StructureRole Role, IReadOnlyList<LayoutLine> Lines, double SpaceBefore, double SpaceAfter, bool Keep)
    {
        public double Height => SpaceBefore + Lines.Sum(l => l.Height);

        public double FirstLineHeight => SpaceBefore + (Lines.Count > 0 ? Lines[0].Height : 0);
    }
}
