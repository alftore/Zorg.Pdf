using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// Sections and page labels: each <see cref="SectionBreak"/> starts a page with a new numbering. A page's label (iii,
/// 12, A-2) is what page number fields show, what a table of contents lists, and what the viewer's page box shows
/// through <c>/PageLabels</c>.
/// </summary>
internal sealed partial class RenderEngine
{
    /// <summary>The sections in page order; the first starts on page 0 with the document's numbering.</summary>
    private readonly List<PageSection> _sections = [];

    private void StartSections() => _sections.Add(new PageSection(0, _document.Numbering, 1, null));

    private async ValueTask PlaceSectionBreakAsync(SectionBreak section, CancellationToken cancellationToken)
    {
        if (!_pageEmpty)
        {
            await NewPageAsync(cancellationToken).ConfigureAwait(false);
        }

        PageSection current = _sections[^1];
        PageNumberStyle style = section.Numbering ?? current.Style;
        int start = section.StartAt ?? (style != current.Style ? 1 : current.Start + (_page - current.FirstPage));
        var next = new PageSection(_page, style, start, section.Prefix);
        if (style == current.Style && start == current.Start + (_page - current.FirstPage) && section.Prefix == current.Prefix)
        {
            return; // The numbering just continues: no new label range.
        }

        if (current.FirstPage == _page)
        {
            _sections[^1] = next; // Nothing was drawn under the old numbering here: the new one replaces it.
        }
        else
        {
            _sections.Add(next);
        }
    }

    /// <summary>The label of a page (from 0): its section's prefix and number in the section's style.</summary>
    private string Label(int page)
    {
        PageSection section = _sections[0];
        foreach (PageSection candidate in _sections)
        {
            if (candidate.FirstPage <= page)
            {
                section = candidate;
            }
        }

        return (section.Prefix ?? "") + Number(section.Start + (page - section.FirstPage), section.Style);
    }

    /// <summary>A number in a page numbering style; letters count as PDF viewers do (a to z, then aa to zz).</summary>
    internal static string Number(int number, PageNumberStyle style) => style switch
    {
        PageNumberStyle.LowerRoman => AsciiLower(Roman(number)),
        PageNumberStyle.UpperRoman => Roman(number),
        PageNumberStyle.LowerLetter => AsciiLower(Letters(number)),
        PageNumberStyle.UpperLetter => Letters(number),
        _ => number.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>A to Z, then AA to ZZ, AAA...: the letter repeated, as ISO 32000-1 12.4.2 counts.</summary>
    private static string Letters(int number)
    {
        if (number < 1)
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        return new StringBuilder().Append((char)('A' + ((number - 1) % 26)), ((number - 1) / 26) + 1).ToString();
    }

    /// <summary>The sections for <c>/PageLabels</c>, or none when every page is numbered 1, 2, 3 without a prefix.</summary>
    private List<PageSection> PageLabels() =>
        _sections is [{ Style: PageNumberStyle.Number, Start: 1, Prefix: null }] ? [] : _sections;
}

/// <summary>A run of pages with one numbering, from its first page (from 0).</summary>
internal readonly record struct PageSection(int FirstPage, PageNumberStyle Style, int Start, string? Prefix);
