using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Layout;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Spike;

/// <summary>A block in the flow: a paragraph or a table.</summary>
internal abstract record FlowBlock;

/// <summary>A paragraph. <paramref name="Role"/> is its structure type: P, H1, H2 ...</summary>
internal sealed record FlowParagraph(string Text, TextAlign Align = TextAlign.Left, double Size = 10, double SpaceAfter = 6, string Role = "P") : FlowBlock;

/// <summary>A table column: header text, share of the width, alignment.</summary>
internal sealed record FlowColumn(string Header, double Share, TextAlign Align = TextAlign.Left);

/// <summary>A table whose rows arrive asynchronously, so a large one never sits in memory.</summary>
internal sealed record FlowTable(IReadOnlyList<FlowColumn> Columns, IAsyncEnumerable<IReadOnlyList<string>> Rows, double Size = 9, double SpaceAfter = 8) : FlowBlock;

/// <summary>Document settings. The title is required: PDF/UA needs one, and viewers show it instead of the file name.</summary>
internal sealed record FlowOptions(string Title, string Language = "en");

/// <summary>The result of a render.</summary>
internal sealed record FlowResult(int Pages, IReadOnlyList<TextDiagnostic> Diagnostics);

/// <summary>
/// Spike slices S5 and S6: blocks flow down A4 pages and onto new ones; tables repeat their header row on each page;
/// every page has a "Page X of Y" footer; and the output is tagged for PDF/UA-1.
/// </summary>
/// <remarks>
/// <para>
/// Pages stream out as they're finished: each page's content and page object are written and flushed before the next
/// page is laid out. What can only be known at the end comes last: the total page count (drawn through a form XObject
/// every footer refers to), the font subset, the page tree and the structure tree.
/// </para>
/// <para>
/// Tagging: headings and paragraphs are H1/P, tables are Table/TR/TH/TD with column scope on the headers. Footers,
/// table rules and backgrounds, and header rows repeated on later pages are artifacts. Each page maps its marked
/// content back to structure elements through the parent tree.
/// </para>
/// </remarks>
internal sealed class FlowDocument : IDisposable
{
    private const double PageWidth = 595.2756;
    private const double PageHeight = 841.8898;
    private const double Margin = 56.6929; // 2 cm
    private const double FooterSize = 8;
    private const double CellPadding = 3;

    private readonly PdfObjectWriter _writer;
    private readonly EmbeddedFont _font;
    private readonly FlowOptions _options;
    private readonly List<PdfReference> _pages = [];
    private readonly List<List<PdfReference>> _parentTree = []; // Per page, the element that owns each MCID.
    private readonly List<TextDiagnostic> _diagnostics = [];
    private readonly PdfReference _catalog;
    private readonly PdfReference _pageTree;
    private readonly PdfReference _fontRef;
    private readonly PdfReference _totalPages;
    private readonly PdfReference _structTree;
    private readonly StructElement _root;

    private ContentStreamBuilder? _content;
    private double _y;

    private FlowDocument(Stream output, EmbeddedFont font, FlowOptions options)
    {
        _writer = new PdfObjectWriter(output, PdfVersion.Pdf17);
        _font = font;
        _options = options;
        _catalog = _writer.Allocate();
        _pageTree = _writer.Allocate();
        _fontRef = _writer.Allocate();
        _totalPages = _writer.Allocate();
        _structTree = _writer.Allocate();
        _root = new StructElement("Document", null, _writer.Allocate(), _structTree);
    }

    private const double ContentWidth = PageWidth - (2 * Margin);

    /// <summary>The lowest a line's baseline may go: above the footer.</summary>
    private const double Bottom = Margin + (FooterSize * 2.5);

    private int PageIndex => _pages.Count - 1;

    public static async ValueTask<FlowResult> RenderAsync(Stream output, ReadOnlyMemory<byte> fontFile, FlowOptions options, IEnumerable<FlowBlock> blocks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(blocks);
        if (string.IsNullOrWhiteSpace(options.Title))
        {
            throw new PdfException("The document needs a title: PDF/UA requires one, and viewers show it in place of the file name.");
        }

        using var document = new FlowDocument(output, new EmbeddedFont(TrueTypeFont.Parse(fontFile)), options);
        {
            await document.StartPageAsync(cancellationToken).ConfigureAwait(false);
            foreach (FlowBlock block in blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (block)
                {
                    case FlowParagraph paragraph:
                        await document.ParagraphAsync(paragraph, cancellationToken).ConfigureAwait(false);
                        break;
                    case FlowTable table:
                        await document.TableAsync(table, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new ArgumentException($"Unknown block type {block?.GetType().Name ?? "null"}.", nameof(blocks));
                }
            }

            await document.FinishPageAsync(cancellationToken).ConfigureAwait(false);
            await document.FinishAsync(cancellationToken).ConfigureAwait(false);
            return new FlowResult(document._pages.Count, document._diagnostics);
        }
    }

    public void Dispose() => _writer.Dispose();

    private async ValueTask ParagraphAsync(FlowParagraph paragraph, CancellationToken cancellationToken)
    {
        ParagraphLayout layout = ParagraphLayout.Create(paragraph.Text, _font, paragraph.Size, ContentWidth, paragraph.Align);
        _diagnostics.AddRange(layout.Diagnostics);
        var element = new StructElement(paragraph.Role, _root, _writer.Allocate(), _root.Reference);
        double leading = _font.LineHeight * paragraph.Size;
        double ascent = Ascent(paragraph.Size);

        bool open = false;
        foreach (LayoutLine line in layout.Lines)
        {
            if (_y - leading < Bottom)
            {
                if (open)
                {
                    Content.EndText().EndMarkedContent();
                    open = false;
                }

                await NewPageAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!open && line.Glyphs.Length > 0)
            {
                Content.BeginMarkedContent(Encoding.ASCII.GetBytes(paragraph.Role), Mark(element)).BeginText().SetFont("F1"u8, paragraph.Size);
                open = true;
            }

            if (line.Glyphs.Length > 0)
            {
                Content.SetTextPosition(Margin + line.Offset, _y - ascent).ShowGlyphs(line.Glyphs, line.ExtraAfter);
            }

            _y -= leading;
        }

        if (open)
        {
            Content.EndText().EndMarkedContent();
        }

        WriteElement(element);
        _y -= paragraph.SpaceAfter;
    }

    private async ValueTask TableAsync(FlowTable table, CancellationToken cancellationToken)
    {
        double total = 0;
        foreach (FlowColumn column in table.Columns)
        {
            total += column.Share;
        }

        double[] widths = new double[table.Columns.Count];
        for (int c = 0; c < widths.Length; c++)
        {
            widths[c] = ContentWidth * table.Columns[c].Share / total;
        }

        var element = new StructElement("Table", _root, _writer.Allocate(), _root.Reference);
        string[] headers = new string[table.Columns.Count];
        for (int c = 0; c < headers.Length; c++)
        {
            headers[c] = table.Columns[c].Header;
        }

        await RowAsync(table, widths, headers, element, header: true, repeated: false, cancellationToken).ConfigureAwait(false);
        int index = 0;
        await foreach (IReadOnlyList<string> row in table.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (row.Count != widths.Length)
            {
                throw new PdfException(string.Create(CultureInfo.InvariantCulture, $"Table row {index} has {row.Count} cells, but the table has {widths.Length} columns."));
            }

            await RowAsync(table, widths, row, element, header: false, repeated: false, cancellationToken).ConfigureAwait(false);
            await _writer.FlushIfNeededAsync(cancellationToken).ConfigureAwait(false);
            index++;
        }

        WriteElement(element);
        _y -= table.SpaceAfter;
    }

    private async ValueTask RowAsync(FlowTable table, double[] widths, IReadOnlyList<string> cells, StructElement tableElement, bool header, bool repeated, CancellationToken cancellationToken)
    {
        // Lay out every cell first: the row is as tall as its tallest cell, and never splits across pages.
        var layouts = new ParagraphLayout[cells.Count];
        double height = 0;
        double leading = _font.LineHeight * table.Size;
        for (int c = 0; c < cells.Count; c++)
        {
            layouts[c] = ParagraphLayout.Create(cells[c], _font, table.Size, widths[c] - (2 * CellPadding), table.Columns[c].Align);
            _diagnostics.AddRange(layouts[c].Diagnostics);
            height = Math.Max(height, (layouts[c].Lines.Count * leading) + (2 * CellPadding));
        }

        if (height > PageHeight - Margin - Bottom)
        {
            throw new PdfException("A table row is taller than a whole page. Rows don't split across pages yet.");
        }

        if (_y - height < Bottom)
        {
            await NewPageAsync(cancellationToken).ConfigureAwait(false);
            if (!header)
            {
                // Repeat the header row. It's an artifact: the header cells are already in the structure once.
                string[] headers = new string[table.Columns.Count];
                for (int c = 0; c < headers.Length; c++)
                {
                    headers[c] = table.Columns[c].Header;
                }

                await RowAsync(table, widths, headers, tableElement, header: true, repeated: true, cancellationToken).ConfigureAwait(false);
            }
        }

        // Background and rule: always artifacts.
        double top = _y;
        Content.BeginArtifact("Layout"u8, []).SaveState();
        if (header)
        {
            Content.SetFillGray(0.9).FillRectangle(Margin, top - height, ContentWidth, height);
        }

        Content.SetLineWidth(header ? 0.8 : 0.3).SetStrokeGray(0.4).StrokeLine(Margin, top - height, Margin + ContentWidth, top - height)
            .RestoreState().EndMarkedContent();

        StructElement? row = repeated ? null : new StructElement("TR", tableElement, _writer.Allocate(), tableElement.Reference);
        double x = Margin;
        double ascent = Ascent(table.Size);
        var written = new List<StructElement>(cells.Count);
        for (int c = 0; c < cells.Count; c++)
        {
            if (row is null)
            {
                Content.BeginArtifact("Pagination"u8, []);
            }
            else
            {
                var cell = new StructElement(header ? "TH" : "TD", row, _writer.Allocate(), row.Reference) { ColumnHeader = header };
                Content.BeginMarkedContent(header ? "TH"u8 : "TD"u8, Mark(cell));
                written.Add(cell);
            }

            Content.BeginText().SetFont("F1"u8, table.Size);
            double lineY = top - CellPadding - ascent;
            foreach (LayoutLine line in layouts[c].Lines)
            {
                if (line.Glyphs.Length > 0)
                {
                    Content.SetTextPosition(x + CellPadding + line.Offset, lineY).ShowGlyphs(line.Glyphs, line.ExtraAfter);
                }

                lineY -= leading;
            }

            Content.EndText().EndMarkedContent();
            x += widths[c];
        }

        // The row is complete: write its elements now, so a long table never holds them in memory.
        foreach (StructElement cell in written)
        {
            WriteElement(cell);
        }

        if (row is not null)
        {
            WriteElement(row);
        }

        _y -= height;
    }

    private ContentStreamBuilder Content => _content ?? throw new InvalidOperationException("No page is open.");

    private double Ascent(double size) => _font.Font.LayoutAscender * size / _font.Font.UnitsPerEm;

    /// <summary>Gives the next MCID on the current page to <paramref name="element"/>.</summary>
    private int Mark(StructElement element)
    {
        List<PdfReference> page = _parentTree[PageIndex];
        element.Kids.Add(new MarkedContent(PageIndex, page.Count));
        page.Add(element.Reference);
        return page.Count - 1;
    }

    private async ValueTask NewPageAsync(CancellationToken cancellationToken)
    {
        await FinishPageAsync(cancellationToken).ConfigureAwait(false);
        await StartPageAsync(cancellationToken).ConfigureAwait(false);
    }

    private ValueTask StartPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pages.Add(_writer.Allocate());
        _parentTree.Add([]);
        _content = new ContentStreamBuilder();
        _y = PageHeight - Margin;
        return ValueTask.CompletedTask;
    }

    /// <summary>Draws the footer, writes the page's content and page object, and flushes them to the output.</summary>
    private async ValueTask FinishPageAsync(CancellationToken cancellationToken)
    {
        ContentStreamBuilder content = Content;
        string label = string.Create(CultureInfo.InvariantCulture, $"Page {_pages.Count} of ");
        LayoutLine footer = ParagraphLayout.Create(label, _font, FooterSize, ContentWidth, TextAlign.Left).Lines[0];
        double baseline = Margin;
        content.BeginArtifact("Pagination"u8, "Footer"u8)
            .BeginText().SetFont("F1"u8, FooterSize).SetTextPosition(Margin, baseline).ShowGlyphs(footer.Glyphs, footer.ExtraAfter).EndText()
            .SaveState();

        // The total is a form XObject written at the end, placed just after "of " (the trailing space hangs, so add it back).
        double after = footer.Width + (_font.Width(_font.Font.GlyphFor(' ')) * FooterSize / 1000);
        content.Translate(Margin + after, baseline).DrawXObject("TP"u8).RestoreState().EndMarkedContent();

        // Title on the right of the footer, also an artifact.
        LayoutLine title = ParagraphLayout.Create(_options.Title, _font, FooterSize, ContentWidth, TextAlign.Right).Lines[0];
        content.BeginArtifact("Pagination"u8, "Footer"u8)
            .BeginText().SetFont("F1"u8, FooterSize).SetTextPosition(Margin + title.Offset, baseline).ShowGlyphs(title.Glyphs, title.ExtraAfter).EndText()
            .EndMarkedContent();

        PdfReference page = _pages[PageIndex];
        PdfReference stream = _writer.Allocate();
        _writer.BeginStreamObject(stream);
        _writer.EndStreamObject(content.WrittenSpan, compress: true);

        _writer.BeginObject(page);
        _writer.BeginDictionary();
        _writer.Name("Type"u8);
        _writer.Name("Page"u8);
        _writer.Name("Parent"u8);
        _writer.Reference(_pageTree);
        _writer.Name("MediaBox"u8);
        _writer.BeginArray();
        _writer.Integer(0);
        _writer.Integer(0);
        _writer.Real(PageWidth);
        _writer.Real(PageHeight);
        _writer.EndArray();
        _writer.Name("Resources"u8);
        _writer.BeginDictionary();
        _writer.Name("Font"u8);
        _writer.BeginDictionary();
        _writer.Name("F1"u8);
        _writer.Reference(_fontRef);
        _writer.EndDictionary();
        _writer.Name("XObject"u8);
        _writer.BeginDictionary();
        _writer.Name("TP"u8);
        _writer.Reference(_totalPages);
        _writer.EndDictionary();
        _writer.EndDictionary();
        _writer.Name("Contents"u8);
        _writer.Reference(stream);
        _writer.Name("StructParents"u8);
        _writer.Integer(PageIndex);
        _writer.Name("Tabs"u8);
        _writer.Name("S"u8);
        _writer.EndDictionary();
        _writer.EndObject();

        _content = null;
        await _writer.FlushIfNeededAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        WriteTotalPages();
        _font.Write(_writer, _fontRef);

        _writer.BeginObject(_pageTree);
        _writer.BeginDictionary();
        _writer.Name("Type"u8);
        _writer.Name("Pages"u8);
        _writer.Name("Kids"u8);
        _writer.BeginArray();
        foreach (PdfReference page in _pages)
        {
            _writer.Reference(page);
        }

        _writer.EndArray();
        _writer.Name("Count"u8);
        _writer.Integer(_pages.Count);
        _writer.EndDictionary();
        _writer.EndObject();

        PdfReference parentTree = _writer.Allocate();
        WriteStructure(_structTree, parentTree);
        PdfReference metadata = WriteMetadata();

        _writer.BeginObject(_catalog);
        _writer.BeginDictionary();
        _writer.Name("Type"u8);
        _writer.Name("Catalog"u8);
        _writer.Name("Pages"u8);
        _writer.Reference(_pageTree);
        _writer.Name("StructTreeRoot"u8);
        _writer.Reference(_structTree);
        _writer.Name("MarkInfo"u8);
        _writer.BeginDictionary();
        _writer.Name("Marked"u8);
        _writer.Boolean(true);
        _writer.EndDictionary();
        _writer.Name("Lang"u8);
        _writer.TextString(_options.Language);
        _writer.Name("ViewerPreferences"u8);
        _writer.BeginDictionary();
        _writer.Name("DisplayDocTitle"u8);
        _writer.Boolean(true);
        _writer.EndDictionary();
        _writer.Name("Metadata"u8);
        _writer.Reference(metadata);
        _writer.EndDictionary();
        _writer.EndObject();

        await _writer.FinishAsync(_catalog, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The form XObject every footer draws: the total page count, known only now.</summary>
    private void WriteTotalPages()
    {
        LayoutLine line = ParagraphLayout.Create(_pages.Count.ToString(CultureInfo.InvariantCulture), _font, FooterSize, ContentWidth, TextAlign.Left).Lines[0];
        ContentStreamBuilder content = new ContentStreamBuilder().BeginText().SetFont("F1"u8, FooterSize).SetTextPosition(0, 0).ShowGlyphs(line.Glyphs, line.ExtraAfter).EndText();
        double descent = _font.Font.LayoutDescender * FooterSize / _font.Font.UnitsPerEm;

        _writer.BeginStreamObject(_totalPages);
        _writer.Name("Type"u8);
        _writer.Name("XObject"u8);
        _writer.Name("Subtype"u8);
        _writer.Name("Form"u8);
        _writer.Name("BBox"u8);
        _writer.BeginArray();
        _writer.Integer(0);
        _writer.Real(descent);
        _writer.Real(line.Width + 1);
        _writer.Real(FooterSize * 1.2);
        _writer.EndArray();
        _writer.Name("Resources"u8);
        _writer.BeginDictionary();
        _writer.Name("Font"u8);
        _writer.BeginDictionary();
        _writer.Name("F1"u8);
        _writer.Reference(_fontRef);
        _writer.EndDictionary();
        _writer.EndDictionary();
        _writer.EndStreamObject(content.WrittenSpan, compress: true);
    }

    private void WriteStructure(PdfReference structTree, PdfReference parentTree)
    {
        _writer.BeginObject(structTree);
        _writer.BeginDictionary();
        _writer.Name("Type"u8);
        _writer.Name("StructTreeRoot"u8);
        _writer.Name("K"u8);
        _writer.Reference(_root.Reference);
        _writer.Name("ParentTree"u8);
        _writer.Reference(parentTree);
        _writer.Name("ParentTreeNextKey"u8);
        _writer.Integer(_pages.Count);
        _writer.EndDictionary();
        _writer.EndObject();

        WriteElement(_root);

        // Number tree: page StructParents index -> array of the element owning each MCID on that page.
        _writer.BeginObject(parentTree);
        _writer.BeginDictionary();
        _writer.Name("Nums"u8);
        _writer.BeginArray();
        for (int page = 0; page < _parentTree.Count; page++)
        {
            _writer.Integer(page);
            _writer.BeginArray();
            foreach (PdfReference owner in _parentTree[page])
            {
                _writer.Reference(owner);
            }

            _writer.EndArray();
        }

        _writer.EndArray();
        _writer.EndDictionary();
        _writer.EndObject();
    }

    /// <summary>Writes a finished element. Its children were written before it; only their references are kept.</summary>
    private void WriteElement(StructElement element)
    {
        _writer.BeginObject(element.Reference);
        _writer.BeginDictionary();
        _writer.Name("Type"u8);
        _writer.Name("StructElem"u8);
        _writer.Name("S"u8);
        _writer.Name(Encoding.ASCII.GetBytes(element.Role));
        _writer.Name("P"u8);
        _writer.Reference(element.Parent);
        if (element.ColumnHeader)
        {
            _writer.Name("A"u8);
            _writer.BeginDictionary();
            _writer.Name("O"u8);
            _writer.Name("Table"u8);
            _writer.Name("Scope"u8);
            _writer.Name("Column"u8);
            _writer.EndDictionary();
        }

        // Marked content on one page can name the page once (/Pg); content on several pages needs MCR dictionaries.
        int firstPage = -1;
        bool onePage = true;
        foreach (object kid in element.Kids)
        {
            if (kid is MarkedContent mark)
            {
                onePage &= firstPage < 0 || firstPage == mark.Page;
                firstPage = firstPage < 0 ? mark.Page : firstPage;
            }
        }

        if (firstPage >= 0 && onePage)
        {
            _writer.Name("Pg"u8);
            _writer.Reference(_pages[firstPage]);
        }

        _writer.Name("K"u8);
        _writer.BeginArray();
        foreach (object kid in element.Kids)
        {
            switch (kid)
            {
                case PdfReference child:
                    _writer.Reference(child);
                    break;
                case MarkedContent mark when onePage:
                    _writer.Integer(mark.Mcid);
                    break;
                case MarkedContent mark:
                    _writer.BeginDictionary();
                    _writer.Name("Type"u8);
                    _writer.Name("MCR"u8);
                    _writer.Name("Pg"u8);
                    _writer.Reference(_pages[mark.Page]);
                    _writer.Name("MCID"u8);
                    _writer.Integer(mark.Mcid);
                    _writer.EndDictionary();
                    break;
            }
        }

        _writer.EndArray();
        _writer.EndDictionary();
        _writer.EndObject();
        element.Kids.Clear();
    }

    /// <summary>XMP with the title (PDF/UA 7.1) and the PDF/UA identification schema. Not compressed, so any tool can read it.</summary>
    private PdfReference WriteMetadata()
    {
        string title = SecurityElement.Escape(_options.Title);
        string xmp =
            "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n" +
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\">\n" +
            $"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{title}</rdf:li></rdf:Alt></dc:title>\n" +
            "<pdfuaid:part>1</pdfuaid:part>\n" +
            "</rdf:Description>\n</rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>";

        PdfReference metadata = _writer.Allocate();
        _writer.BeginStreamObject(metadata);
        _writer.Name("Type"u8);
        _writer.Name("Metadata"u8);
        _writer.Name("Subtype"u8);
        _writer.Name("XML"u8);
        _writer.EndStreamObject(Encoding.UTF8.GetBytes(xmp), compress: false);
        return metadata;
    }

    private readonly record struct MarkedContent(int Page, int Mcid);

    /// <summary>A structure element. It's written as soon as all its content is laid out, then only its reference is kept.</summary>
    private sealed class StructElement
    {
        public StructElement(string role, StructElement? parent, PdfReference reference, PdfReference parentReference)
        {
            Role = role;
            Reference = reference;
            Parent = parentReference;
            parent?.Kids.Add(reference);
        }

        public string Role { get; }

        public PdfReference Reference { get; }

        /// <summary>The parent element, or the structure tree root for the document element.</summary>
        public PdfReference Parent { get; }

        public bool ColumnHeader { get; init; }

        /// <summary>Child element references and marked content, in reading order. Cleared once written.</summary>
        public List<object> Kids { get; } = [];
    }
}
