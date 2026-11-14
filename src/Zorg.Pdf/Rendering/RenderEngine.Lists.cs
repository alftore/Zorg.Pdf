using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Layout;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// Lists: items indented by the list's indent, each marker drawn right-aligned in that space on the baseline of the
/// item's first line, and the structure PDF/UA asks for: L, then LI per item with an Lbl (the marker) and an LBody
/// (the item's paragraphs and nested lists). Items flow over pages like paragraphs; a marker always stays with its
/// first line.
/// </summary>
internal sealed partial class RenderEngine
{
    /// <summary>Default indent of list items, in points.</summary>
    internal const double DefaultListIndent = 18;

    /// <summary>Space between a marker and its item's text, in points.</summary>
    private const double MarkerGap = 4;

    /// <summary>Space after a paragraph inside a list, as a share of its font size: tighter than between paragraphs.</summary>
    private const double ListItemSpacing = 0.25;

    private static readonly string[] Bullets = ["•", "–", "◦"];

    private async ValueTask PlaceListAsync(int index, List list, CancellationToken cancellationToken)
    {
        await PlaceListAsync(list, $"/content/{index}", list.Style, _geometry.Left, _geometry.ContentWidth, 0, null, null, cancellationToken).ConfigureAwait(false);
        _y -= (_styles.ForListParagraph(new Paragraph(""), list.Style).Size?.Points ?? 10) * 0.6;
    }

    /// <summary>
    /// Places a list at <paramref name="left"/> within <paramref name="width"/>. <paramref name="pendingMarker"/> is an
    /// outer item's marker still to draw, when that item starts with this list: it goes on this list's first line.
    /// </summary>
    private async ValueTask PlaceListAsync(List list, string path, string? styleName, double left, double width, int depth, Tagger.Element? parent, Action<double>? pendingMarker, CancellationToken cancellationToken)
    {
        Tagger.Element element = _tagger.Begin(StructureRole.L, parent);
        element.ListNumbering = Numbering(list.Marker, depth);
        double indent = list.Indent?.Points ?? DefaultListIndent;
        for (int i = 0; i < list.Items.Length; i++)
        {
            string itemPath = $"{path}/items/{i}";
            Tagger.Element item = _tagger.Begin(StructureRole.LI, element);
            Tagger.Element? label = list.Marker == ListMarker.None ? null : _tagger.Begin(StructureRole.Lbl, item);
            Tagger.Element body = _tagger.Begin(StructureRole.LBody, item);
            Action<double>? marker = null;
            System.Collections.Immutable.ImmutableArray<Block> content = list.Items[i].Content;
            for (int b = 0; b < content.Length; b++)
            {
                string blockPath = $"{itemPath}/content/{b}";
                if (b == 0)
                {
                    TextStyle firstStyle = content[0] is Paragraph p ? _styles.ForListParagraph(p, styleName) : _styles.ForListParagraph(new Paragraph(""), styleName);
                    int number = list.Start + i;
                    Action<double>? own = label is null ? null : baseline => DrawMarker(label, MarkerText(list.Marker, number, depth, firstStyle), firstStyle, left, indent, baseline);
                    marker = pendingMarker + own;
                    pendingMarker = null;
                }

                switch (content[b])
                {
                    case Paragraph paragraph:
                        TextStyle style = _styles.ForListParagraph(paragraph, styleName);
                        PreparedText text = LayoutText(paragraph, style, blockPath, null, report: true, width: Math.Max(1, width - indent));
                        await PlaceLinesAsync(text, left + indent, body, marker, cancellationToken).ConfigureAwait(false);
                        marker = null;
                        _y -= paragraph.SpaceAfter?.Points ?? ((style.Size?.Points ?? 10) * ListItemSpacing);
                        break;
                    case List nested:
                        await PlaceListAsync(nested, blockPath, nested.Style ?? styleName, left + indent, width - indent, depth + 1, body, marker, cancellationToken).ConfigureAwait(false);
                        marker = null;
                        break;
                }
            }

            _tagger.End(body, _documentWriter.Pages);
            if (label is not null)
            {
                _tagger.End(label, _documentWriter.Pages);
            }

            _tagger.End(item, _documentWriter.Pages);
        }

        _tagger.End(element, _documentWriter.Pages);
    }

    /// <summary>Draws a marker right-aligned in the indent, on the baseline of its item's first line, tagged as the Lbl.</summary>
    private void DrawMarker(Tagger.Element label, string text, TextStyle style, double left, double indent, double baseline)
    {
        List<TextSegment> segments = Segments([new TextRun(text)], style, out _);
        ParagraphLayout layout = ParagraphLayout.Create(segments, Math.Max(1, indent - MarkerGap), TextAlign.Right, style.LineHeight ?? 1.2);
        if (layout.Lines is not [{ Runs.Count: > 0 } line, ..])
        {
            return;
        }

        _content.BeginMarkedContent(Tagger.Name(StructureRole.Lbl), _tagger.Mark(label, _page));
        BeginText();
        DrawRuns(line, left + line.Offset, baseline);
        EndText();
    }

    /// <summary>The marker for the item numbered <paramref name="number"/>; a bullet the font lacks becomes a hyphen.</summary>
    private string MarkerText(ListMarker marker, int number, int depth, TextStyle style)
    {
        switch (marker)
        {
            case ListMarker.Bullet:
                string bullet = Bullets[depth % Bullets.Length];
                return Preflight.Face(style, _styles, _defaultFamily) is { } face && _fonts.TryGet(face, out FontChain chain) && chain.Covers(bullet[0]) ? bullet : "-";
            case ListMarker.Number:
                return number.ToString(CultureInfo.InvariantCulture) + ".";
            case ListMarker.LowerAlpha:
                return AsciiLower(Alpha(number)) + ".";
            case ListMarker.UpperAlpha:
                return Alpha(number) + ".";
            case ListMarker.LowerRoman:
                return AsciiLower(Roman(number)) + ".";
            case ListMarker.UpperRoman:
                return Roman(number) + ".";
            default:
                return "";
        }
    }

    /// <summary>A, B ... Z, AA, AB ...: bijective base 26. Numbers below 1 stay digits.</summary>
    internal static string Alpha(int number)
    {
        if (number < 1)
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        var letters = new StringBuilder();
        for (int n = number; n > 0; n = (n - 1) / 26)
        {
            letters.Insert(0, (char)('A' + ((n - 1) % 26)));
        }

        return letters.ToString();
    }

    /// <summary>Roman numerals from 1 to 3999; anything else stays digits.</summary>
    internal static string Roman(int number)
    {
        if (number is < 1 or > 3999)
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        ReadOnlySpan<int> values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] numerals = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
        var roman = new StringBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            for (; number >= values[i]; number -= values[i])
            {
                roman.Append(numerals[i]);
            }
        }

        return roman.ToString();
    }

    /// <summary>Letters and numerals in lower case: ASCII only, so no culture can change them.</summary>
    internal static string AsciiLower(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (int i = 0; i < source.Length; i++)
        {
            span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
        }
    });

    /// <summary>The ListNumbering attribute for a list's markers.</summary>
    private static string Numbering(ListMarker marker, int depth) => marker switch
    {
        ListMarker.Bullet => depth % Bullets.Length == 2 ? "Circle" : "Disc",
        ListMarker.Number => "Decimal",
        ListMarker.LowerAlpha => "LowerAlpha",
        ListMarker.UpperAlpha => "UpperAlpha",
        ListMarker.LowerRoman => "LowerRoman",
        ListMarker.UpperRoman => "UpperRoman",
        _ => "None",
    };

    /// <summary>The height of a list's first line, for keeping a heading with it.</summary>
    private double? FirstListLineHeight(List list, string path, string? styleName = null, double? width = null)
    {
        styleName = list.Style ?? styleName;
        double available = (width ?? _geometry.ContentWidth) - (list.Indent?.Points ?? DefaultListIndent);
        return list.Items is [{ Content: [var first, ..] }, ..] ? first switch
        {
            Paragraph paragraph => LayoutText(paragraph, _styles.ForListParagraph(paragraph, styleName), path, null, report: false, width: Math.Max(1, available)).FirstLineHeight,
            List nested => FirstListLineHeight(nested, path, styleName, available),
            _ => null,
        } : null;
    }
}
