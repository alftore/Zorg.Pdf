using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>
/// Starts a new page and, from there on, a new page numbering, header and footer: roman numerals for front matter, the body from 1, an
/// appendix as A-1, A-2. The numbers are the viewer's page labels, what page number fields show, and what a table of
/// contents lists. In JSON <c>{ "type": "sectionBreak", "numbering": "number", "startAt": 1, "prefix": "A-" }</c>.
/// </summary>
/// <example>
/// <code>
/// Content = [new TableOfContents(), new SectionBreak { Numbering = PageNumberStyle.Number, StartAt = 1 }, new Heading("Introduction"), ...]
/// // with PdfDocument.Numbering = PageNumberStyle.LowerRoman for the pages before the break
/// </code>
/// </example>
public sealed record SectionBreak : Block
{
    /// <summary>A section that continues the numbering as it is.</summary>
    public SectionBreak()
    {
    }

    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal SectionBreak(PageNumberStyle? numbering = null, int? startAt = null, string? prefix = null, PageBand? header = null, PageBand? footer = null, string? style = null, bool keepWithNext = false, string? id = null) =>
        (Numbering, StartAt, Prefix, Header, Footer, Style, KeepWithNext, Id) = (numbering, startAt, prefix, header, footer, style, keepWithNext, id);

    /// <summary>How the section's pages are numbered. Null: as the section before, continuing its count unless <see cref="StartAt"/> is set.</summary>
    public PageNumberStyle? Numbering { get; init; }

    /// <summary>The number of the section's first page. Null: 1 when <see cref="Numbering"/> changes, else the count continues.</summary>
    public int? StartAt { get; init; }

    /// <summary>Text before every page number of the section, such as <c>"A-"</c>. Null: none.</summary>
    public string? Prefix { get; init; }

    /// <summary>The section's header. Null: the section before's; <see cref="PageBand.None"/>: none.</summary>
    public PageBand? Header { get; init; }

    /// <summary>The section's footer. Null: the section before's; <see cref="PageBand.None"/>: none.</summary>
    /// <example>
    /// <code>
    /// // A cover without a footer: the document has none, and the body's section sets it.
    /// var report = new PdfDocument("Report") { Content = [new Heading("Report"), new SectionBreak { Footer = footer }, ...] };
    /// </code>
    /// </example>
    public PageBand? Footer { get; init; }
}

/// <summary>How pages are numbered. In JSON <c>"number"</c>, <c>"lowerRoman"</c>, <c>"upperRoman"</c>, <c>"lowerLetter"</c> or <c>"upperLetter"</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<PageNumberStyle>))]
public enum PageNumberStyle
{
    /// <summary>1, 2, 3.</summary>
    Number,

    /// <summary>i, ii, iii.</summary>
    LowerRoman,

    /// <summary>I, II, III.</summary>
    UpperRoman,

    /// <summary>a to z, then aa to zz, as PDF viewers count.</summary>
    LowerLetter,

    /// <summary>A to Z, then AA to ZZ, as PDF viewers count.</summary>
    UpperLetter,
}
