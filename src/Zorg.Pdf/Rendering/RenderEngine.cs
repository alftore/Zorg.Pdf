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
internal sealed partial class RenderEngine : IDisposable
{
    /// <summary>Stroke width of a synthetic bold face, as a share of the font size: about the weight step from regular to bold.</summary>
    private const double SyntheticBoldStroke = 0.03;

    /// <summary>Slant of a synthetic italic face: tan 12°, the usual oblique angle.</summary>
    private const double SyntheticItalicShear = 0.2126;

    private readonly PdfDocument _document;
    private readonly RenderOptions _options;
    private readonly StyleResolver _styles;
    private readonly FontSet _fonts;
    private readonly ImageSet _images;
    private readonly HashSet<ImageSet.Entry> _pageImages = [];
    private readonly string? _defaultFamily;
    private readonly DiagnosticSink _sink;
    private readonly PdfObjectWriter _writer;
    private readonly DocumentWriter _documentWriter;
    private readonly Tagger _tagger;
    private PageGeometry _geometry; // The content area: the page inside its margins, less the header and footer.
    private readonly PageGeometry _pageGeometry; // The page inside its margins.
    private readonly Dictionary<int, PreparedText> _prepared = [];

    /// <summary>Headings for the outline, in order: title, level, page and top.</summary>
    private readonly List<OutlineEntry> _outline = [];

    /// <summary>Named destinations: each block Id, at its page and top.</summary>
    private readonly Dictionary<string, (int Page, double Top)> _destinations = new(StringComparer.Ordinal);
    private readonly HashSet<EmbeddedFont> _pageFonts = [];
    private readonly HashSet<int> _missingInData = []; // Characters already reported for rows from data slots.

    private ContentStreamBuilder _content = new();
    private int _page = -1;
    private double _y;
    private bool _pageEmpty = true;
    private Color _fill = Color.Black; // The page's fill colour: graphics state outlives BT/ET.
    private Color? _stroke;
    private double _strokeWidth = 1;
    private int _renderingMode; // Text state outlives BT/ET too.
    private EmbeddedFont? _textFont;
    private double _textSize;

    private RenderEngine(PdfDocument document, RenderOptions options, StyleResolver styles, FontSet fonts, ImageSet images, string? defaultFamily, DiagnosticSink sink, Stream output, bool synchronous)
    {
        _images = images;
        _document = document;
        _options = options;
        _styles = styles;
        _fonts = fonts;
        _defaultFamily = defaultFamily;
        _sink = sink;
        _writer = new PdfObjectWriter(output, PdfVersion.Pdf17, synchronous, objectStreams: true);
        _pageGeometry = PageGeometry.From(document.Page);
        _geometry = _pageGeometry;
        _documentWriter = new DocumentWriter(_writer, _pageGeometry);
        _tagger = new Tagger(_writer);
        _geometry = MeasureBands();
        PrepareWatermark();
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
        foreach (PdfDiagnostic diagnostic in document.Validate().Diagnostics)
        {
            // Under PDF/UA and PDF/A-2a a skipped heading level fails conformance, so it stops the render.
            sink.AddRange([diagnostic.Code == DiagnosticCodes.HeadingSkipsLevel && (options.Conformance == PdfConformance.PdfUA1 || options.Archive == PdfArchive.PdfA2a) ? diagnostic with { Severity = DiagnosticSeverity.Error } : diagnostic]);
        }
        if (!sink.HasErrors && TemplateBinder.IsTemplate(document))
        {
            // A template: bound to the model first, so everything after sees a plain document.
            document = TemplateBinder.Bind(document, options.Data.Model, sink);
        }

        var styles = new StyleResolver(document.Styles);
        string? defaultFamily = styles.DefaultFontFamily ?? options.Fonts.DefaultFamily;
        if (options.Fonts.Families.IsEmpty && options.FontResolver is null)
        {
            sink.Error(DiagnosticCodes.NoFonts, "The render has no fonts, and there are no system fonts by default. Add one: new RenderOptions { Fonts = FontCollection.Empty.Add(new FontFamily(\"Inter\") { Regular = FontSource.FromFile(\"Inter-Regular.ttf\") }) }, or opt in to installed fonts with FontResolver = FontResolvers.System().", "");
        }

        Dictionary<FaceKey, string> needed = sink.HasErrors ? [] : Preflight.NeededFaces(document, styles, defaultFamily, sink);
        FontSet fonts = await FontSet.LoadAsync(options, needed, sink, synchronous, cancellationToken).ConfigureAwait(false);
        ImageSet images = await ImageSet.LoadAsync(document, options, sink, synchronous, cancellationToken).ConfigureAwait(false);
        Preflight.Check(document, styles, fonts, defaultFamily, options, synchronous, sink);
        if (sink.HasErrors)
        {
            throw new PdfDocumentException(sink.ToImmutable());
        }

        using var engine = new RenderEngine(document, options, styles, fonts, images, defaultFamily, sink, output, synchronous);
        if (sink.HasErrors)
        {
            throw new PdfDocumentException(sink.ToImmutable()); // The bands leave no room; nothing has been written yet.
        }

        await engine.RunAsync(cancellationToken).ConfigureAwait(false);
        return new RenderResult(engine._documentWriter.Pages.Count, engine._writer.Position, sink.ToImmutable());
    }

    public void Dispose() => _writer.Dispose();

    private async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        PrepareTableOfContents();
        StartSections();
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
                case Table table:
                    Arrived(table); // Its top: a table that moves to the next page whole is found a page early.
                    await PlaceTableAsync(i, table, cancellationToken).ConfigureAwait(false);
                    break;
                case Image image:
                    Arrived(image);
                    await PlaceImageAsync(i, image, cancellationToken).ConfigureAwait(false);
                    break;
                case List list:
                    Arrived(list);
                    await PlaceListAsync(i, list, cancellationToken).ConfigureAwait(false);
                    break;
                case SectionBreak section:
                    await PlaceSectionBreakAsync(section, cancellationToken).ConfigureAwait(false);
                    StartBandSet(i);
                    Arrived(section);
                    break;
                case TableOfContents toc:
                    Arrived(toc);
                    await PlaceTableOfContentsAsync(i, toc, cancellationToken).ConfigureAwait(false);
                    break;
                case TextBox box:
                    if (box.Id is { } boxId && box.Resolve() is { } place)
                    {
                        _destinations[boxId] = (_page, _pageGeometry.Height - place.Y);
                    }

                    PlaceTextBox(i, box);
                    break;
            }

            _prepared.Remove(i);
        }

        await FinishPageAsync(cancellationToken).ConfigureAwait(false);
        WriteTocNumbers(); // Before the fonts are written: the digits join their subsets.
        WriteBands();
        WriteWatermark();
        foreach ((EmbeddedFont font, _, PdfReference reference) in _fonts.Used)
        {
            font.Write(_writer, reference);
        }

        _tagger.Finish(_documentWriter.Pages);
        PdfReference info = _documentWriter.Finish(_document, _options, _tagger.StructTreeRoot, _outline, _destinations, PageLabels());
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
        if (text.Keep && !_pageEmpty && NextFirstHeight(index) is { } next)
        {
            double needed = text.Height + text.SpaceAfter + next;
            if (_y - needed < _geometry.Bottom && needed <= _geometry.ContentHeight)
            {
                await NewPageAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (!_pageEmpty)
        {
            _y -= text.SpaceBefore;
        }

        Block block = _document.Content[index];
        await PlaceLinesAsync(text, _geometry.Left, parent: null, firstLine: _ => Arrived(block), cancellationToken).ConfigureAwait(false);
        _y -= text.SpaceAfter;
    }

    /// <summary>
    /// Places laid-out lines from the current position at <paramref name="left"/>, breaking pages as needed, tagged as
    /// one element under <paramref name="parent"/>. <paramref name="firstLine"/> is called with the first line's
    /// baseline, once that line's page is known and before it's drawn (a list item draws its marker there).
    /// </summary>
    private async ValueTask PlaceLinesAsync(PreparedText text, double left, Tagger.Element? parent, Action<double>? firstLine, CancellationToken cancellationToken)
    {
        Tagger.Element? element = null;
        Dictionary<int, Tagger.Element>? linkElements = null;
        bool open = false;
        int current = -1; // The link the open marked content belongs to, or -1 for the block's own.
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

            if (firstLine is not null)
            {
                firstLine(_y - line.Ascent);
                firstLine = null;
            }

            for (int r = 0; r < line.Runs.Count; r++)
            {
                LineRun run = line.Runs[r];
                if (!open || run.Link != current)
                {
                    // Link text is marked content of its own, under a Link element inside the block's (PDF/UA 7.18.5).
                    if (open)
                    {
                        EndText();
                    }

                    element ??= _tagger.Begin(text.Role, parent, text.Language);
                    Tagger.Element owner = element;
                    if (run.Link >= 0)
                    {
                        linkElements ??= [];
                        if (!linkElements.TryGetValue(run.Link, out Tagger.Element? linkElement))
                        {
                            linkElements[run.Link] = linkElement = _tagger.Begin(StructureRole.Link, element);
                        }

                        owner = linkElement;
                    }

                    _content.BeginMarkedContent(Tagger.Name(owner.Role), _tagger.Mark(owner, _page));
                    BeginText();
                    (open, current) = (true, run.Link);
                }

                double x = left + line.Offset;
                DrawRun(run, x, _y - line.Ascent);
                if (run.Link >= 0)
                {
                    double width = (r + 1 < line.Runs.Count ? line.Runs[r + 1].X : line.Width) - run.X;
                    LinkArea(text.Links[run.Link], linkElements![run.Link], x + run.X, _y - line.Height, width, line.Height);
                }
            }

            _y -= line.Height;
            _pageEmpty = false;
        }

        if (open)
        {
            EndText();
        }

        if (linkElements is not null)
        {
            foreach (Tagger.Element linkElement in linkElements.Values)
            {
                _tagger.End(linkElement, _documentWriter.Pages);
            }
        }

        if (element is not null)
        {
            _tagger.End(element, _documentWriter.Pages);
        }
    }

    /// <summary>
    /// Records where a top-level block starts, at the current position: a named destination for its Id, and an outline
    /// entry for a heading within <see cref="PdfDocument.OutlineLevels"/>.
    /// </summary>
    private void Arrived(Block block)
    {
        if (_tocNames.TryGetValue(block, out string? tocName))
        {
            _destinations[tocName] = (_page, _y);
            _headingPages[block] = _page;
        }

        if (block.Id is { } id)
        {
            _destinations[id] = (_page, _y);
        }

        if (block is Heading heading && heading.Level <= _document.OutlineLevels)
        {
            _outline.Add(new OutlineEntry(OutlineTitle(heading.Inlines), heading.Level, _page, _y));
        }
    }

    /// <summary>A heading's text for its bookmark: its runs, with line breaks as spaces and fields left out.</summary>
    private static string OutlineTitle(ImmutableArray<Inline> inlines) => Preflight.PlainText(inlines);

    /// <summary>Starts a text object; the font is set again by the first run in it.</summary>
    private void BeginText()
    {
        _content.BeginText();
        (_textFont, _textSize) = (null, 0);
    }

    /// <summary>
    /// Draws a line's runs in an open text object, starting at <paramref name="x"/> on <paramref name="baseline"/>:
    /// font and size, fill colour, and synthetic bold (fill and stroke) and italic (a slanted text matrix).
    /// </summary>
    private void DrawRuns(LayoutLine line, double x, double baseline)
    {
        foreach (LineRun run in line.Runs)
        {
            DrawRun(run, x, baseline);
        }
    }

    /// <summary>Draws one run in an open text object; see <see cref="DrawRuns"/>.</summary>
    private void DrawRun(LineRun run, double x, double baseline)
    {
        if (run.Language is { } language)
        {
            _content.BeginSpan(language); // A passage in another language, inside the element's marked content.
        }

        if (!ReferenceEquals(run.Font, _textFont) || run.Size != _textSize)
        {
            _content.SetFont(_fonts.Use(run.Font, _writer), run.Size);
            (_textFont, _textSize) = (run.Font, run.Size);
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

        if (run.SyntheticItalic)
        {
            _content.SetTextMatrix(SyntheticItalicShear, x + run.X, baseline);
        }
        else
        {
            _content.SetTextPosition(x + run.X, baseline);
        }

        _content.ShowGlyphs(run.Glyphs, run.ExtraAfter);
        if (run.Language is not null)
        {
            _content.EndMarkedContent();
        }
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

    /// <summary>
    /// The height of the next block's first line (or of the next image), if nothing but spacers comes before it;
    /// null for anything else. What a heading or a <see cref="Block.KeepWithNext"/> block keeps with.
    /// </summary>
    private double? NextFirstHeight(int index)
    {
        for (int i = index + 1; i < _document.Content.Length; i++)
        {
            switch (_document.Content[i])
            {
                case Heading or Paragraph:
                    return Prepare(i).FirstLineHeight;
                case Image { Source: { } source } image when _images.TryGet(source, out ImageSet.Entry entry):
                    return ImageSize(image, entry, _geometry.ContentWidth, _geometry.ContentHeight).Height;
                case List list:
                    return FirstListLineHeight(list, $"/content/{i}");
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
        PreparedText prepared = LayoutText(block, _styles.For(block), $"/content/{index}", fields: null, report: true);
        _prepared[index] = prepared;
        return prepared;
    }

    /// <summary>
    /// Lays out a heading or paragraph at the text width. <paramref name="fields"/> gives the text of page number
    /// fields (in headers and footers); without it they're left out. Layout warnings are reported when
    /// <paramref name="report"/> is set, so a band laid out on every page reports them once.
    /// </summary>
    private PreparedText LayoutText(Block block, TextStyle style, string path, Func<FieldKind, string>? fields, bool report, double? width = null)
    {
        double size = style.Size?.Points ?? 10;
        ImmutableArray<Inline> inlines = Preflight.Inlines(block)!.Value;
        (TextAlign align, StructureRole role, double before, double after) = block switch
        {
            Heading heading => (heading.Align, Tagger.Heading(heading.Level), size * 0.8, size * 0.4),
            Paragraph paragraph => (paragraph.Align, StructureRole.P, 0.0, paragraph.SpaceAfter?.Points ?? size * 0.6),
            _ => throw new InvalidOperationException($"Block {block.GetType().Name} isn't text."),
        };

        var links = new List<Link>();
        List<TextSegment> segments = Segments(inlines, style, out List<(int Offset, int Inline)> starts, fields, links);
        double textWidth = width ?? _geometry.ContentWidth;
        ParagraphLayout layout = ParagraphLayout.Create(segments, textWidth, align, style.LineHeight ?? 1.2);
        foreach (TextDiagnostic diagnostic in report ? layout.Diagnostics : [])
        {
            (int start, int inline) = starts.FindLast(s => s.Offset <= diagnostic.Index);
            _sink.Warning(
                diagnostic.Code,
                string.Create(CultureInfo.InvariantCulture, $"The word at index {diagnostic.Index - start} is wider than the {textWidth:0.##} pt text width, so it was broken between characters."),
                $"{path}/inlines/{inline}");
        }

        return new PreparedText(role, layout.Lines, before, after, block is Heading || block.KeepWithNext) { Language = BlockLanguage(style), Links = links };
    }

    /// <summary>The style's language when it differs from the document's, for /Lang on the block's element.</summary>
    private string? BlockLanguage(TextStyle style) =>
        style.Language is { } language && !string.Equals(language, _document.Language, StringComparison.OrdinalIgnoreCase) ? language : null;

    /// <summary>
    /// One segment per run, in its own face, size and colour; line breaks become line feeds. <paramref name="starts"/>
    /// maps text offsets back to inlines for diagnostics. A run whose face wasn't loaded (styled text in bound rows
    /// isn't known before the render) uses the block's face. A field takes its text from <paramref name="fields"/>
    /// in the block's style, or is left out without it. A run in another language than its block carries it.
    /// </summary>
    private List<TextSegment> Segments(ImmutableArray<Inline> inlines, TextStyle style, out List<(int Offset, int Inline)> starts, Func<FieldKind, string>? fields = null, List<Link>? links = null)
    {
        double size = style.Size?.Points ?? 10;
        var segments = new List<TextSegment>();
        List<(int Offset, int Inline)> found = [];
        int offset = 0;
        for (int j = 0; j < inlines.Length; j++)
        {
            found.Add((offset, j));
            Add(inlines[j], style, -1);
        }

        starts = found;
        void Add(Inline inline, TextStyle outer, int link)
        {
            if (inline is Link { Inlines.IsDefault: false } linked && link < 0)
            {
                // A link's text in the link style, marked with its index so layout keeps it in runs of its own.
                int index = links?.Count ?? -1;
                links?.Add(linked);
                TextStyle linkStyle = outer.Merge(_styles.LinkStyle);
                foreach (Inline inner in linked.Inlines)
                {
                    Add(inner, linkStyle, index);
                }

                return;
            }

            (string? text, TextStyle runStyle) = inline switch
            {
                TextRun run => (run.Text, Preflight.RunStyle(outer, run)),
                LineBreak => ("\n", outer),
                Field field when fields is not null => (fields(field.Kind), outer),
                _ => (null, outer),
            };
            if (text is not null)
            {
                segments.Add(Segment(text, runStyle) with { Link = link });
                offset += text.Length;
            }
        }

        if (segments.Count == 0)
        {
            segments.Add(Segment("", style)); // An empty paragraph or cell still has a line height.
        }

        return segments;

        TextSegment Segment(string text, TextStyle runStyle)
        {
            if (!(Preflight.Face(runStyle, _styles, _defaultFamily) is { } face && _fonts.TryGet(face, out FontChain chain)))
            {
                _fonts.TryGet(Preflight.Face(style, _styles, _defaultFamily)!.Value, out chain);
            }

            string? language = runStyle.Language is { } own && !string.Equals(own, style.Language ?? _document.Language, StringComparison.OrdinalIgnoreCase) ? own : null;
            return new TextSegment(text, chain, (runStyle.Size?.Points ?? size) * _textScale, runStyle.Color) { Language = language };
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
        _pageImages.Clear();
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

        foreach (EmbeddedFont font in _bandFonts)
        {
            if (!_pageFonts.Contains(font))
            {
                fonts.Add((_fonts.Use(font, _writer), _fonts.Reference(font)));
            }
        }

        fonts.Sort((a, b) => a.Font.Number.CompareTo(b.Font.Number)); // Deterministic order.
        var images = new List<(byte[] Name, PdfReference Image)>();
        foreach (ImageSet.Entry image in _pageImages)
        {
            images.Add(_images.Use(image, _writer));
        }

        foreach (ImageSet.Entry image in _bandImages)
        {
            if (!_pageImages.Contains(image))
            {
                images.Add(_images.Use(image, _writer));
            }
        }

        images.Sort((a, b) => a.Image.Number.CompareTo(b.Image.Number));
        PdfReference? toc = TocStream();
        PdfReference? band = BandStream();
        PdfReference[] annotations = WriteLinkAnnotations();
        _documentWriter.WritePage(_page, _tagger.PageKey(_page), _content.WrittenSpan, fonts, images, WatermarkUnder(), [.. toc is { } t ? [t] : Array.Empty<PdfReference>(), .. band is { } b ? [b] : Array.Empty<PdfReference>(), .. WatermarkOver()], WatermarkState(), annotations);
        await _writer.FlushIfNeededAsync(cancellationToken).ConfigureAwait(false);
        _options.Progress?.Report(new RenderProgress(_page + 1, _writer.Position));
    }

    /// <summary>A heading or paragraph laid out at the page's text width, ready to place.</summary>
    private sealed record PreparedText(StructureRole Role, IReadOnlyList<LayoutLine> Lines, double SpaceBefore, double SpaceAfter, bool Keep)
    {
        /// <summary>The block's language when its style sets one other than the document's: /Lang on its element.</summary>
        public string? Language { get; init; }

        /// <summary>The links in the text, by the index on their runs.</summary>
        public List<Link> Links { get; init; } = [];

        public double Height => SpaceBefore + Lines.Sum(l => l.Height);

        public double FirstLineHeight => SpaceBefore + (Lines.Count > 0 ? Lines[0].Height : 0);
    }
}
