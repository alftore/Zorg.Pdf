using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>
/// A piece of text inside a heading, paragraph or table cell: a <see cref="TextRun"/>, a <see cref="LineBreak"/> or a
/// <see cref="Field"/>. A string converts to a text run, so collection expressions stay short.
/// </summary>
/// <remarks>
/// In JSON an inline is a bare string (a run of plain text), <c>{ "text": "...", "style": { ... } }</c>,
/// <c>{ "lineBreak": true }</c>, or <c>{ "field": "pageNumber" }</c>. Text is always literal: there's no template
/// syntax, so <c>{{</c> prints as typed.
/// </remarks>
/// <example>
/// <code>
/// var footer = new Paragraph(["Page ", Field.PageNumber, " of ", Field.PageCount]);
/// var total = new Paragraph([new TextRun("Total: 12 450,00 NOK") { Style = TextStyle.Bold }]);
/// </code>
/// </example>
[JsonConverter(typeof(InlineJsonConverter))]
public abstract record Inline
{
    /// <summary>A run of plain text.</summary>
    /// <param name="text">The text.</param>
    public static implicit operator Inline(string text) => new TextRun(text);

    /// <summary>A run of plain text, for languages that can't use the implicit conversion.</summary>
    /// <param name="text">The text.</param>
    /// <returns>A <see cref="TextRun"/>.</returns>
    public static Inline FromString(string text) => new TextRun(text);
}

/// <summary>Text in one style. Line feeds in the text break the line.</summary>
/// <param name="Text">The text, drawn as typed.</param>
public sealed record TextRun(string Text) : Inline
{
    /// <summary>Overrides for this run, on top of the block's style.</summary>
    public TextStyle? Style { get; init; }
}

/// <summary>Ends the line here, within the same paragraph.</summary>
public sealed record LineBreak : Inline
{
    /// <summary>The line break. All line breaks are the same.</summary>
    public static LineBreak Value { get; } = new();
}

/// <summary>A value filled in when the page is drawn, such as the page number.</summary>
/// <param name="Kind">Which value.</param>
public sealed record Field(FieldKind Kind) : Inline
{
    /// <summary>The number of the page it's drawn on, from 1.</summary>
    public static Field PageNumber { get; } = new(FieldKind.PageNumber);

    /// <summary>The number of pages in the document.</summary>
    public static Field PageCount { get; } = new(FieldKind.PageCount);
}

/// <summary>The values a <see cref="Field"/> can show. In JSON <c>"pageNumber"</c> or <c>"pageCount"</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<FieldKind>))]
public enum FieldKind
{
    /// <summary>The current page number, from 1.</summary>
    PageNumber,

    /// <summary>The total number of pages.</summary>
    PageCount,
}
