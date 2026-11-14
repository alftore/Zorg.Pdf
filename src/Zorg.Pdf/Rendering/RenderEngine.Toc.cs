using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Layout;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// The table of contents. Its entries (the headings' text and levels) are known once templates are bound, so it's laid
/// out where it stands; only the page numbers depend on what comes later. Each number's place and MCID are reserved
/// when its entry is drawn, and the numbers are drawn into a second content stream of the page when the document
/// finishes, as page X of Y is. Tagged TOC, TOCI and Link (title, number and the entry's annotation); leaders are
/// artifacts.
/// </summary>
internal sealed partial class RenderEngine
{
    private const double TocIndent = 12;
    private const double TocGap = 6;
    private const double TocEntrySpace = 2;

    /// <summary>The destination name of each heading the table of contents lists: its Id, or a generated one.</summary>
    private readonly Dictionary<Block, string> _tocNames = new(ReferenceEqualityComparer.Instance);

    /// <summary>The page each listed heading landed on.</summary>
    private readonly Dictionary<Block, int> _headingPages = new(ReferenceEqualityComparer.Instance);

    /// <summary>Numbers waiting to be drawn, and the second stream of each page that has some.</summary>
    private readonly List<TocNumber> _tocNumbers = [];
    private readonly Dictionary<int, PdfReference> _tocStreams = [];

    /// <summary>Names the headings the table of contents lists, so they get destinations as they're placed.</summary>
    private void PrepareTableOfContents()
    {
        if (_document.Content.OfType<TableOfContents>().FirstOrDefault() is not { } toc)
        {
            return;
        }

        foreach ((int index, Heading heading, _, _) in Preflight.TocEntries(_document, toc))
        {
            _tocNames[heading] = heading.Id ?? string.Create(CultureInfo.InvariantCulture, $"_h{index}"); // Ids can't start with "_".
        }
    }

    private async ValueTask PlaceTableOfContentsAsync(int index, TableOfContents toc, CancellationToken cancellationToken)
    {
        if (toc.Title is { } title)
        {
            var heading = new Heading(title);
            PreparedText laid = LayoutText(heading, _styles.For(heading), $"/content/{index}/title", fields: null, report: true);
            if (!_pageEmpty)
            {
                _y -= laid.SpaceBefore;
            }

            await PlaceLinesAsync(laid, _geometry.Left, parent: null, firstLine: null, cancellationToken).ConfigureAwait(false);
            _y -= laid.SpaceAfter;
        }

        Tagger.Element tocElement = _tagger.Begin(StructureRole.TOC);
        foreach ((_, Heading heading, int level, string text) in Preflight.TocEntries(_document, toc))
        {
            TextStyle style = _styles.Named(string.Create(CultureInfo.InvariantCulture, $"toc{level}"));
            double numberColumn = NumberColumn(style);
            double left = _geometry.Left + ((level - 1) * TocIndent);
            double columnStart = _geometry.Right - numberColumn;
            PreparedText entry = LayoutText(new Paragraph(text), style, $"/content/{index}", fields: null, report: false, width: columnStart - TocGap - left);
            var link = new Link([text]) { Target = "#" + _tocNames[heading], Description = text };
            Tagger.Element item = _tagger.Begin(StructureRole.TOCI, tocElement);
            Tagger.Element linkElement = _tagger.Begin(StructureRole.Link, item);
            for (int l = 0; l < entry.Lines.Count; l++)
            {
                LayoutLine line = entry.Lines[l];
                if (_y - line.Height < _geometry.Bottom && !_pageEmpty)
                {
                    await NewPageAsync(cancellationToken).ConfigureAwait(false);
                }

                double baseline = _y - line.Ascent;
                if (line.Runs.Count > 0)
                {
                    _content.BeginMarkedContent(Tagger.Name(StructureRole.Link), _tagger.Mark(linkElement, _page));
                    BeginText();
                    DrawRuns(line, left + line.Offset, baseline);
                    EndText();
                }

                LinkArea(link, linkElement, left + line.Offset, _y - line.Height, line.Width, line.Height);
                if (l == entry.Lines.Count - 1)
                {
                    if (toc.Leaders)
                    {
                        DrawLeaders(style, left + line.Offset + line.Width + TocGap, columnStart - TocGap, baseline);
                    }

                    // The number's place and MCID now; its digits when the heading's page is known.
                    _tocNumbers.Add(new TocNumber(_page, _tagger.Mark(linkElement, _page), _geometry.Right, baseline, style, heading));
                    LinkArea(link, linkElement, columnStart, _y - line.Height, numberColumn, line.Height);
                }

                _y -= line.Height;
                _pageEmpty = false;
            }

            _y -= TocEntrySpace;
            _tagger.End(linkElement, _documentWriter.Pages);
            _tagger.End(item, _documentWriter.Pages);
        }

        _tagger.End(tocElement, _documentWriter.Pages);
    }

    /// <summary>Room for the widest page number this document can have, in the entry's style.</summary>
    private double NumberColumn(TextStyle style)
    {
        string widest = new('0', _options.Limits.MaxPages.ToString(CultureInfo.InvariantCulture).Length);
        PreparedText digits = LayoutText(new Paragraph(widest), style, "/", fields: null, report: false, width: 1e6);
        return digits.Lines is [{ } line, ..] ? line.Width : 0;
    }

    /// <summary>Spaced dots from <paramref name="from"/> to <paramref name="to"/>, ending at <paramref name="to"/>: a layout artifact.</summary>
    private void DrawLeaders(TextStyle style, double from, double to, double baseline)
    {
        PreparedText unit = LayoutText(new Paragraph(" ."), style, "/", fields: null, report: false, width: 1e6);
        double width = unit.Lines is [{ } one, ..] ? one.Width : 0;
        int count = width > 0 ? (int)Math.Floor((to - from) / width) : 0;
        if (count < 1)
        {
            return;
        }

        PreparedText dots = LayoutText(new Paragraph(string.Concat(Enumerable.Repeat(" .", count))), style, "/", fields: null, report: false, width: 1e6);
        if (dots.Lines is not [{ } line, ..])
        {
            return;
        }

        _content.BeginArtifact("Layout"u8, []);
        BeginText();
        DrawRuns(line, to - line.Width, baseline);
        EndText();
    }

    /// <summary>The page's second stream for its table of contents numbers, allocated when the page is written.</summary>
    private PdfReference? TocStream()
    {
        if (!_tocNumbers.Exists(n => n.Page == _page))
        {
            return null;
        }

        PdfReference stream = _writer.Allocate();
        _tocStreams[_page] = stream;
        return stream;
    }

    /// <summary>Draws every page number, right-aligned in its column, now that every heading has been placed.</summary>
    private void WriteTocNumbers()
    {
        foreach ((int page, PdfReference stream) in _tocStreams.OrderBy(s => s.Key))
        {
            // This stream follows the page's own: set the fill colour, and let the first run set the rest.
            _content = new ContentStreamBuilder();
            _content.SetFillGray(0);
            _fill = Color.Black;
            (_stroke, _strokeWidth, _renderingMode) = (null, 1, 0);
            foreach (TocNumber number in _tocNumbers.Where(n => n.Page == page))
            {
                string text = _headingPages.TryGetValue(number.Heading, out int target) ? Label(target) : "";
                PreparedText digits = LayoutText(new Paragraph(text), number.Style, "/", fields: null, report: false, width: 1e6);
                if (digits.Lines is not [{ } line, ..])
                {
                    continue;
                }

                _content.BeginMarkedContent(Tagger.Name(StructureRole.Link), number.Mcid);
                BeginText();
                DrawRuns(line, number.Right - line.Width, number.Baseline);
                EndText();
            }

            _writer.BeginStreamObject(stream);
            _writer.EndStreamObject(_content.WrittenSpan, compress: true);
        }
    }

    /// <summary>A page number to draw: where, in which style, for which heading, under which MCID.</summary>
    private sealed record TocNumber(int Page, int Mcid, double Right, double Baseline, TextStyle Style, Heading Heading);
}
