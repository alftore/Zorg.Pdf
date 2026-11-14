using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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
/// The pipeline is model → <see cref="Preflight"/> → layout (<see cref="ParagraphLayout"/>) → pagination (this class)
/// → page content and tagging (<see cref="Tagger"/>) → <see cref="DocumentWriter"/> → <see cref="PdfObjectWriter"/>.
/// With <c>synchronous</c> set, every step completes without a pending task, so the sync API never blocks on one.
/// </remarks>
internal sealed class RenderEngine : IDisposable
{
    private readonly PdfDocument _document;
    private readonly RenderOptions _options;
    private readonly StyleResolver _styles;
    private readonly FontSet _fonts;
    private readonly DiagnosticSink _sink;
    private readonly PdfObjectWriter _writer;
    private readonly DocumentWriter _documentWriter;
    private readonly Tagger _tagger;
    private readonly PageGeometry _geometry;
    private readonly Dictionary<int, PreparedText> _prepared = [];

    private ContentStreamBuilder _content = new();
    private readonly HashSet<EmbeddedFont> _pageFonts = [];
    private int _page = -1;
    private double _y;
    private bool _pageEmpty = true;

    private RenderEngine(PdfDocument document, RenderOptions options, StyleResolver styles, FontSet fonts, DiagnosticSink sink, Stream output, bool synchronous)
    {
        _document = document;
        _options = options;
        _styles = styles;
        _fonts = fonts;
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
        if (options.Fonts.Families.IsEmpty)
        {
            sink.Error(DiagnosticCodes.NoFonts, "The render has no fonts, and there are no system fonts by default. Add one: new RenderOptions { Fonts = FontCollection.Empty.Add(new FontFamily(\"Inter\") { Regular = FontSource.FromFile(\"Inter-Regular.ttf\") }) }.", "");
        }

        FontSet fonts = await FontSet.LoadAsync(options.Fonts, Preflight.NeededFamilies(document, styles, defaultFamily), defaultFamily, sink, synchronous, cancellationToken).ConfigureAwait(false);
        Preflight.Check(document, styles, fonts, sink);
        if (sink.HasErrors)
        {
            throw new PdfDocumentException(sink.ToImmutable());
        }

        using var engine = new RenderEngine(document, options, styles, fonts, sink, output, synchronous);
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
        foreach (LayoutLine line in text.Lines)
        {
            if (_y - text.Leading < _geometry.Bottom && !_pageEmpty)
            {
                if (open)
                {
                    _content.EndText().EndMarkedContent();
                    open = false;
                }

                await NewPageAsync(cancellationToken).ConfigureAwait(false);
            }

            if (line.Glyphs.Length > 0)
            {
                if (!open)
                {
                    element ??= _tagger.Begin(text.Role);
                    _content.BeginMarkedContent(Tagger.Name(text.Role), _tagger.Mark(element, _page)).BeginText().SetFont(_fonts.Use(text.Font, _writer), text.Size);
                    if (text.Color is { } color && color != Color.Black)
                    {
                        _content.SetFillRgb(color.R / 255.0, color.G / 255.0, color.B / 255.0);
                    }

                    _pageFonts.Add(text.Font);
                    open = true;
                }

                _content.SetTextPosition(_geometry.Left + line.Offset, _y - text.Ascent).ShowGlyphs(line.Glyphs, line.ExtraAfter);
            }

            _y -= text.Leading;
            _pageEmpty = false;
        }

        if (open)
        {
            _content.EndText().EndMarkedContent();
        }

        if (element is not null)
        {
            _tagger.End(element, _documentWriter.Pages);
        }

        _y -= text.SpaceAfter;
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
        string family = style.FontFamily ?? _styles.DefaultFontFamily ?? _fonts.DefaultFamily!;
        _fonts.TryGet(family, out EmbeddedFont font); // Preflight made sure it loaded.
        double size = style.Size?.Points ?? 10;
        double leading = size * (style.LineHeight ?? 1.2);

        (System.Collections.Immutable.ImmutableArray<Inline> inlines, TextAlign align, StructureRole role, double before, double after) = block switch
        {
            Heading heading => (heading.Inlines, heading.Align, Tagger.Heading(heading.Level), size * 0.8, size * 0.4),
            Paragraph paragraph => (paragraph.Inlines, paragraph.Align, StructureRole.P, 0, paragraph.SpaceAfter?.Points ?? size * 0.6),
            _ => throw new InvalidOperationException($"Block {block.GetType().Name} isn't text."),
        };

        // Runs are joined; their own styles aren't applied yet (Preflight said so). Offsets map layout diagnostics back.
        var text = new StringBuilder();
        var starts = new List<(int Offset, int Inline)>();
        for (int j = 0; j < inlines.Length; j++)
        {
            starts.Add((text.Length, j));
            switch (inlines[j])
            {
                case TextRun run:
                    text.Append(run.Text);
                    break;
                case LineBreak:
                    text.Append('\n');
                    break;
            }
        }

        ParagraphLayout layout = ParagraphLayout.Create(text.ToString(), font, size, _geometry.ContentWidth, align);
        foreach (TextDiagnostic diagnostic in layout.Diagnostics)
        {
            (int offset, int inline) = starts.FindLast(s => s.Offset <= diagnostic.Index);
            _sink.Warning(
                diagnostic.Code,
                string.Create(CultureInfo.InvariantCulture, $"The word at index {diagnostic.Index - offset} is wider than the page's {_geometry.ContentWidth:0.##} pt text width at {size:0.##} pt, so it was broken between characters."),
                $"{path}/inlines/{inline}");
        }

        var prepared = new PreparedText(
            role,
            font,
            size,
            leading,
            font.Font.LayoutAscender * size / font.Font.UnitsPerEm,
            style.Color,
            layout.Lines,
            before,
            after,
            block is Heading || block.KeepWithNext);
        _prepared[index] = prepared;
        return prepared;
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
    private sealed record PreparedText(
        StructureRole Role,
        EmbeddedFont Font,
        double Size,
        double Leading,
        double Ascent,
        Color? Color,
        IReadOnlyList<LayoutLine> Lines,
        double SpaceBefore,
        double SpaceAfter,
        bool Keep)
    {
        public double Height => SpaceBefore + (Lines.Count * Leading);

        public double FirstLineHeight => SpaceBefore + Leading;
    }
}
