using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>
/// A bulleted or numbered list, tagged for screen readers (L, LI, Lbl, LBody). Items hold paragraphs and nested lists.
/// In JSON <c>{ "type": "list", "marker": "number", "items": ["First", "Second"] }</c>.
/// </summary>
/// <example>
/// <code>
/// var steps = new List(["Open the invoice", "Check the amounts", new ListItem([new Paragraph("Pay"), new List(["by card", "by transfer"])])])
/// {
///     Marker = ListMarker.Number,
/// };
/// </code>
/// </example>
public sealed record List : Block
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal List(ImmutableArray<ListItem> items, ListMarker marker = ListMarker.Bullet, int start = 1, Length? indent = null, string? style = null, bool keepWithNext = false, string? id = null) =>
        (Items, Marker, Start, Indent, Style, KeepWithNext, Id) = (items, marker, start, indent, style, keepWithNext, id);

    /// <summary>A list of items. Strings convert to items of one paragraph.</summary>
    /// <param name="items">The items.</param>
    [SetsRequiredMembers]
    public List(ImmutableArray<ListItem> items) => Items = items;

    /// <summary>The items, in order.</summary>
    [JsonRequired]
    public required ImmutableArray<ListItem> Items { get; init; }

    /// <summary>What marks each item: a bullet (the default), a number or letter, or nothing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ListMarker Marker { get; init; }

    /// <summary>The number of the first item, for numbered lists: 1 by default.</summary>
    public int Start { get; init; } = 1;

    /// <summary>How far items are indented from the list's left edge; the marker sits in this space. Null: 18 pt.</summary>
    public Length? Indent { get; init; }
}

/// <summary>
/// One item of a <see cref="List"/>: paragraphs and nested lists. A string converts to an item of one paragraph. In
/// JSON an item is a string, or <c>{ "content": [blocks] }</c>.
/// </summary>
[JsonConverter(typeof(ListItemJsonConverter))]
public sealed record ListItem
{
    /// <summary>An item of blocks: paragraphs and nested lists.</summary>
    /// <param name="content">The blocks.</param>
    public ListItem(ImmutableArray<Block> content) => Content = content;

    /// <summary>An item of one plain paragraph.</summary>
    /// <param name="text">The text.</param>
    public ListItem(string text) => Content = [new Paragraph(text)];

    /// <summary>Paragraphs and nested lists.</summary>
    public ImmutableArray<Block> Content { get; init; }

    /// <summary>An item of one plain paragraph.</summary>
    /// <param name="text">The text.</param>
    public static implicit operator ListItem(string text) => new(text);

    /// <summary>An item of one plain paragraph, for languages that can't use the implicit conversion.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The item.</returns>
    public static ListItem FromString(string text) => new(text);
}

/// <summary>What marks the items of a <see cref="List"/>. In JSON camelCase: <c>"bullet"</c>, <c>"number"</c> and so on.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ListMarker>))]
public enum ListMarker
{
    /// <summary>A bullet, •; nested lists use – and then ◦ where the font has them.</summary>
    Bullet,

    /// <summary>1. 2. 3.</summary>
    Number,

    /// <summary>a. b. c.</summary>
    LowerAlpha,

    /// <summary>A. B. C.</summary>
    UpperAlpha,

    /// <summary>i. ii. iii.</summary>
    LowerRoman,

    /// <summary>I. II. III.</summary>
    UpperRoman,

    /// <summary>No marker; the items are still indented and tagged as a list.</summary>
    None,
}
