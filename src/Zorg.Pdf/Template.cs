using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Zorg.Pdf;

/// <summary>
/// A value from the render's model (<see cref="PdfData.WithModel(System.Text.Json.JsonElement)"/>), as text. The path names it with dots and
/// indexes, such as <c>customer.name</c> or <c>lines.0.amount</c>; inside a <see cref="Repeat"/> it looks in the
/// current item first, then outwards. <c>@index</c> and <c>@number</c> give the item's position from 0 and from 1.
/// In JSON <c>{ "bind": "customer.name", "format": "N2" }</c>.
/// </summary>
/// <param name="Path">The value's path in the model.</param>
/// <example>
/// <code>
/// var greeting = new Paragraph(["Dear ", new Bind("customer.name"), ","]);
/// var total = new Paragraph(["Total: ", new Bind("total") { Format = "N2", Style = TextStyle.Bold }]);
/// </code>
/// </example>
public sealed record Bind(string Path) : Inline
{
    /// <summary>
    /// A .NET format string for numbers and dates, such as <c>N2</c> or <c>yyyy-MM-dd</c>, applied in the culture of
    /// the document's <see cref="PdfDocument.Language"/>. Null: the value as it is.
    /// </summary>
    public string? Format { get; init; }

    /// <summary>Overrides for the bound text, on top of the block's style.</summary>
    public TextStyle? Style { get; init; }
}

/// <summary>
/// Content repeated for each item of an array in the model. Inside it, bindings look in the item first. A list item
/// whose only content is a repeat becomes one list item per array element. In JSON
/// <c>{ "type": "repeat", "items": "notes", "content": [blocks] }</c>.
/// </summary>
/// <example>
/// <code>
/// var notes = new Repeat("notes", [new Paragraph([new Bind("@number"), ". ", new Bind("text")])]);
/// </code>
/// </example>
public sealed record Repeat : Block
{
    /// <summary>For the JSON serializer only.</summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal Repeat(string items, ImmutableArray<Block> content, string? style = null, bool keepWithNext = false) =>
        (Items, Content, Style, KeepWithNext) = (items, content, style, keepWithNext);

    /// <summary>Content repeated for each item of the array at <paramref name="items"/>.</summary>
    /// <param name="items">The array's path in the model.</param>
    /// <param name="content">The blocks to repeat.</param>
    [SetsRequiredMembers]
    public Repeat(string items, ImmutableArray<Block> content) => (Items, Content) = (items, content);

    /// <summary>The array's path in the model.</summary>
    [JsonRequired]
    public required string Items { get; init; }

    /// <summary>The blocks repeated for each item.</summary>
    [JsonRequired]
    public required ImmutableArray<Block> Content { get; init; }
}

/// <summary>
/// Content shown only when a model value is set: not missing, null, false, 0, empty text or an empty array (or the
/// other way round with <see cref="Not"/>). In JSON <c>{ "type": "when", "test": "overdue", "content": [blocks] }</c>.
/// </summary>
/// <example>
/// <code>
/// var reminder = new Conditional("overdue", [new Paragraph("Payment is overdue.")]);
/// var thanks = new Conditional("overdue", [new Paragraph("Thank you for paying on time.")]) { Not = true };
/// </code>
/// </example>
public sealed record Conditional : Block
{
    /// <summary>For the JSON serializer only.</summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal Conditional(string test, ImmutableArray<Block> content, bool not = false, string? style = null, bool keepWithNext = false) =>
        (Test, Content, Not, Style, KeepWithNext) = (test, content, not, style, keepWithNext);

    /// <summary>Content shown when the value at <paramref name="test"/> is set.</summary>
    /// <param name="test">The value's path in the model.</param>
    /// <param name="content">The blocks to show.</param>
    [SetsRequiredMembers]
    public Conditional(string test, ImmutableArray<Block> content) => (Test, Content) = (test, content);

    /// <summary>The value's path in the model.</summary>
    [JsonRequired]
    public required string Test { get; init; }

    /// <summary>The blocks shown.</summary>
    [JsonRequired]
    public required ImmutableArray<Block> Content { get; init; }

    /// <summary>Shows the content when the value is not set instead.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Not { get; init; }
}
