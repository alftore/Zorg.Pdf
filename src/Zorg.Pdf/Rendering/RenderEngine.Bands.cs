using System;
using System.Collections.Generic;
using System.Globalization;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Layout;
using Zorg.Pdf.Validation;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// Headers and footers. Each is measured once, before the first page, so the content area can leave room for it.
/// They're drawn last, into a second content stream per page, when the page count is known: the pages before them
/// have already streamed out, and only those small streams wait. A band without fields is one stream that every page
/// shares. Bands are pagination artifacts, outside the structure tree.
/// </summary>
internal sealed partial class RenderEngine
{
    /// <summary>Space between a band and the content, in points.</summary>
    internal const double BandGap = 12;

    /// <summary>The least height the content area may keep once the bands are placed.</summary>
    internal const double MinContentHeight = 36;

    private readonly List<EmbeddedFont> _bandFonts = [];
    private readonly List<ImageSet.Entry> _bandImages = [];

    /// <summary>Each section's header and footer and the content area they leave; set 0 is the document's.</summary>
    private readonly List<BandSet> _bandSets = [];

    /// <summary>Which band set each top-level section break starts, by its index in the content.</summary>
    private readonly Dictionary<int, int> _sectionBands = [];

    /// <summary>Pages with fields in their bands: each page's own stream and set, drawn when the page count is known.</summary>
    private readonly List<(PdfReference Stream, int Page, int Set)> _bandStreams = [];

    /// <summary>The band set of the pages being written now.</summary>
    private int _bandSet;

    /// <summary>
    /// Measures every section's header and footer before the first page, so a band too tall for the page is found
    /// before anything is written, and returns the first section's content area. A section break's null band
    /// inherits the one before; <see cref="PageBand.None"/> (no content) is no band.
    /// </summary>
    private PageGeometry MeasureBands()
    {
        PageBand? header = _document.Header, footer = _document.Footer;
        _bandSets.Add(MeasureBandSet(header, "/header", footer, "/footer"));
        string headerPath = "/header", footerPath = "/footer";
        for (int i = 0; i < _document.Content.Length; i++)
        {
            if (_document.Content[i] is SectionBreak { } section && (section.Header is not null || section.Footer is not null))
            {
                (header, headerPath) = section.Header is { } h ? (h, $"/content/{i}/header") : (header, headerPath);
                (footer, footerPath) = section.Footer is { } f ? (f, $"/content/{i}/footer") : (footer, footerPath);
                _sectionBands[i] = _bandSets.Count;
                _bandSets.Add(MeasureBandSet(header, headerPath, footer, footerPath));
            }
        }

        _bandFonts.Sort((a, b) => string.CompareOrdinal(a.Font.PostScriptName, b.Font.PostScriptName)); // Deterministic.
        return _bandSets[0].Content;
    }

    private BandSet MeasureBandSet(PageBand? headerModel, string headerPath, PageBand? footerModel, string footerPath)
    {
        Band? header = headerModel is { Content.IsDefaultOrEmpty: false } ? Band.From(headerModel, headerPath, "header", "Header"u8) : null;
        Band? footer = footerModel is { Content.IsDefaultOrEmpty: false } ? Band.From(footerModel, footerPath, "footer", "Footer"u8) : null;
        PageGeometry content = _pageGeometry;
        string labels = Preflight.LabelCharacters(_document);
        foreach (Band? band in (ReadOnlySpan<Band?>)[header, footer])
        {
            if (band is null)
            {
                continue;
            }

            // Three digits for the height; every label character once to find the fonts that may draw them.
            band.Height = Height(LayoutBand(band, _ => "000", report: true));
            foreach (BandItem item in LayoutBand(band, _ => labels, report: false))
            {
                if (item.Image is { } image && !_bandImages.Contains(image))
                {
                    _bandImages.Add(image);
                }

                foreach (LayoutLine line in item.Text?.Lines ?? [])
                {
                    foreach (LineRun run in line.Runs)
                    {
                        if (!_bandFonts.Contains(run.Font))
                        {
                            _bandFonts.Add(run.Font);
                        }
                    }
                }
            }

            double room = band.Height > 0 ? band.Height + BandGap : 0;
            content = band == header ? content with { Top = content.Top - room } : content with { Bottom = content.Bottom + room };
        }

        if ((header ?? footer) is not null && content.ContentHeight < MinContentHeight)
        {
            _sink.Error(
                DiagnosticCodes.BandsTooTall,
                string.Create(CultureInfo.InvariantCulture, $"The header and footer take {_pageGeometry.ContentHeight - content.ContentHeight:0.#} pt of the page's {_pageGeometry.ContentHeight:0.#} pt, leaving less than {MinContentHeight} pt for the content."),
                footer is not null ? footer.Path : header!.Path);
        }

        return new BandSet(header, footer, content);
    }

    /// <summary>A section break: its pages use its band set and content area from the page it starts.</summary>
    private void StartBandSet(int index)
    {
        if (_sectionBands.TryGetValue(index, out int set) && set != _bandSet)
        {
            _bandSet = set;
            _geometry = _bandSets[set].Content;
            _y = _geometry.Top; // The section's first page is still empty.
        }
    }

    /// <summary>The band stream for the page being finished, or null when its section has no bands.</summary>
    private PdfReference? BandStream()
    {
        BandSet set = _bandSets[_bandSet];
        if (set.Header is null && set.Footer is null)
        {
            return null;
        }

        if (!(set.Header?.HasFields ?? false) && !(set.Footer?.HasFields ?? false))
        {
            set.Shared ??= _writer.Allocate(); // The same on every page of the section: one stream.
            return set.Shared;
        }

        PdfReference stream = _writer.Allocate();
        _bandStreams.Add((stream, _page, _bandSet));
        return stream;
    }

    /// <summary>Draws and writes the band streams, now that the page count is known.</summary>
    private void WriteBands()
    {
        foreach (BandSet set in _bandSets)
        {
            if (set.Shared is { } shared)
            {
                WriteBandStream(shared, set, 0, _documentWriter.Pages.Count);
            }
        }

        foreach ((PdfReference stream, int page, int set) in _bandStreams)
        {
            WriteBandStream(stream, _bandSets[set], page, _documentWriter.Pages.Count);
        }
    }

    private void WriteBandStream(PdfReference reference, BandSet set, int page, int pages)
    {
        // This stream follows the page's own, so it starts from whatever state that one left: set the fill colour,
        // and let the first run set its font, stroke and rendering mode.
        _content = new ContentStreamBuilder();
        _content.SetFillGray(0);
        _fill = Color.Black;
        (_stroke, _strokeWidth, _renderingMode) = (null, 1, 0);
        string Field(FieldKind kind) => kind == FieldKind.PageCount ? pages.ToString(CultureInfo.InvariantCulture) : Label(page);
        if (set.Header is { } header)
        {
            DrawBand(header, LayoutBand(header, Field, report: false), _pageGeometry.Top);
        }

        if (set.Footer is { } footer)
        {
            DrawBand(footer, LayoutBand(footer, Field, report: false), _pageGeometry.Bottom + footer.Height);
        }

        _writer.BeginStreamObject(reference);
        _writer.EndStreamObject(_content.WrittenSpan, compress: true);
    }

    /// <summary>A section's header and footer, the content area they leave, and their shared stream when they have no fields.</summary>
    private sealed class BandSet(Band? header, Band? footer, PageGeometry content)
    {
        public Band? Header { get; } = header;

        public Band? Footer { get; } = footer;

        public PageGeometry Content { get; } = content;

        public PdfReference? Shared { get; set; }
    }

    private void DrawBand(Band band, List<BandItem> items, double y)
    {
        for (int i = 0; i < items.Count; i++)
        {
            (PreparedText? text, double space, ImageSet.Entry? image, double x, double width) = items[i];
            if (image is not null)
            {
                _content.BeginArtifact("Pagination"u8, band.Subtype.Span);
                DrawImage(image, x, y - space, width, space);
                _content.EndMarkedContent();
            }

            if (text is null)
            {
                y -= space;
                continue;
            }

            y -= text.SpaceBefore;
            _content.BeginArtifact("Pagination"u8, band.Subtype.Span);
            BeginText();
            foreach (LayoutLine line in text.Lines)
            {
                if (line.Runs.Count > 0)
                {
                    DrawRuns(line, _pageGeometry.Left + line.Offset, y - line.Ascent);
                }

                y -= line.Height;
            }

            EndText();
            y -= text.SpaceAfter;
        }
    }

    /// <summary>The band's paragraphs laid out with the given field text, its images sized to the text width, and its spacers as space.</summary>
    private List<BandItem> LayoutBand(Band band, Func<FieldKind, string> fields, bool report)
    {
        var items = new List<BandItem>();
        for (int i = 0; i < band.Model.Content.Length; i++)
        {
            switch (band.Model.Content[i])
            {
                case Paragraph paragraph:
                    items.Add(new BandItem(LayoutText(paragraph, _styles.ForBand(paragraph, band.StyleName), $"{band.Path}/content/{i}", fields, report), 0, null, 0, 0));
                    break;
                case Spacer spacer:
                    items.Add(new BandItem(null, spacer.Height.Points, null, 0, 0));
                    break;
                case Image { Source: { } source } image when _images.TryGet(source, out ImageSet.Entry entry):
                    (double width, double height) = ImageSize(image, entry, _pageGeometry.ContentWidth, double.MaxValue);
                    double x = image.Align switch
                    {
                        TextAlign.Center => _pageGeometry.Left + ((_pageGeometry.ContentWidth - width) / 2),
                        TextAlign.Right => _pageGeometry.Right - width,
                        _ => _pageGeometry.Left,
                    };
                    items.Add(new BandItem(null, height, entry, x, width));
                    break;
            }
        }

        return items;
    }

    /// <summary>A band's height: its blocks and the space between them, but not the space after the last.</summary>
    private static double Height(List<BandItem> items)
    {
        double height = 0;
        for (int i = 0; i < items.Count; i++)
        {
            height += items[i].Text is { } text ? text.Height + (i < items.Count - 1 ? text.SpaceAfter : 0) : items[i].Space;
        }

        return height;
    }

    /// <summary>One block of a band: laid-out text, or a height of space, with an image drawn in it at x and width.</summary>
    private readonly record struct BandItem(PreparedText? Text, double Space, ImageSet.Entry? Image, double X, double Width);

    /// <summary>A header or footer and what's known about it before the first page.</summary>
    private sealed class Band(PageBand model, string path, string styleName, ReadOnlyMemory<byte> subtype)
    {
        public PageBand Model { get; } = model;

        public string Path { get; } = path;

        /// <summary>The built-in style its paragraphs start from: <c>header</c> or <c>footer</c>.</summary>
        public string StyleName { get; } = styleName;

        /// <summary>The artifact subtype: <c>Header</c> or <c>Footer</c>.</summary>
        public ReadOnlyMemory<byte> Subtype { get; } = subtype;

        /// <summary>Whether it shows a page number or count, so it differs from page to page.</summary>
        public bool HasFields { get; } = HasField(model);

        public double Height { get; set; }

        public static Band? From(PageBand? band, string path, string styleName, ReadOnlySpan<byte> subtype) =>
            band is null ? null : new Band(band, path, styleName, subtype.ToArray());

        private static bool HasField(PageBand band)
        {
            foreach (Block block in band.Content)
            {
                if (block is Paragraph paragraph && !paragraph.Inlines.IsDefault)
                {
                    foreach (Inline inline in paragraph.Inlines)
                    {
                        if (inline is Field)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}
