using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Zorg.Pdf;

/// <summary>
/// Something that flows down the page: a heading, paragraph, list, table, image, spacer or page break. In JSON every
/// block has a <c>"type"</c>: <c>"heading"</c>, <c>"paragraph"</c>, <c>"list"</c>, <c>"table"</c>, <c>"image"</c>,
/// <c>"spacer"</c> or <c>"pageBreak"</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(Heading), "heading")]
[JsonDerivedType(typeof(Paragraph), "paragraph")]
[JsonDerivedType(typeof(List), "list")]
[JsonDerivedType(typeof(Repeat), "repeat")]
[JsonDerivedType(typeof(Conditional), "when")]
[JsonDerivedType(typeof(Table), "table")]
[JsonDerivedType(typeof(Image), "image")]
[JsonDerivedType(typeof(Spacer), "spacer")]
[JsonDerivedType(typeof(PageBreak), "pageBreak")]
[JsonDerivedType(typeof(TextBox), "textBox")]
[JsonDerivedType(typeof(TableOfContents), "tableOfContents")]
public abstract record Block
{
    /// <summary>The name of a style in the document's <see cref="StyleSheet"/>, replacing the block's default style.</summary>
    public string? Style { get; init; }

    /// <summary>Keeps the block on the same page as the next one. Headings keep with their next block anyway.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool KeepWithNext { get; init; }

    /// <summary>
    /// A name other parts of the document, and other documents, can link to: <c>"#appendix-b"</c>. Letters, digits,
    /// <c>-</c>, <c>_</c>, <c>.</c> and <c>:</c>, unique in the document. Written as a named destination at the block's
    /// top, so a viewer opens <c>file.pdf#appendix-b</c> there.
    /// </summary>
    /// <example>
    /// <code>
    /// new Heading("Appendix B") { Id = "appendix-b" }
    /// </code>
    /// </example>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }
}

/// <summary>A heading, tagged H1 to H6 for screen readers and outlines.</summary>
/// <example>
/// <code>
/// var title = new Heading("Invoice 2026-0042");
/// var section = new Heading("Payment terms") { Level = 2 };
/// </code>
/// </example>
public sealed record Heading : Block
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal Heading(ImmutableArray<Inline> inlines, int level = 1, TextAlign align = TextAlign.Left, string? style = null, bool keepWithNext = false, string? id = null) =>
        (Inlines, Level, Align, Style, KeepWithNext, Id) = (inlines, level, align, style, keepWithNext, id);

    /// <summary>A heading of plain text.</summary>
    /// <param name="text">The text.</param>
    [SetsRequiredMembers]
    public Heading(string text) => Inlines = [new TextRun(text)];

    /// <summary>A heading of mixed inlines.</summary>
    /// <param name="inlines">The inlines.</param>
    [SetsRequiredMembers]
    public Heading(ImmutableArray<Inline> inlines) => Inlines = inlines;

    /// <summary>1 to 6. Level 1 is the document's main heading.</summary>
    public int Level { get; init; } = 1;

    /// <summary>The text.</summary>
    [JsonRequired]
    public required ImmutableArray<Inline> Inlines { get; init; }

    /// <summary>Horizontal alignment.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TextAlign Align { get; init; }
}

/// <summary>A paragraph of text that wraps to the width of the page.</summary>
/// <example>
/// <code>
/// var terms = new Paragraph("Payment is due within 30 days.") { Align = TextAlign.Justify };
/// var mixed = new Paragraph(["Due: ", new TextRun("14 December") { Style = TextStyle.Bold }]);
/// </code>
/// </example>
public sealed record Paragraph : Block
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal Paragraph(ImmutableArray<Inline> inlines, TextAlign align = TextAlign.Left, Length? spaceAfter = null, string? style = null, bool keepWithNext = false, string? id = null) =>
        (Inlines, Align, SpaceAfter, Style, KeepWithNext, Id) = (inlines, align, spaceAfter, style, keepWithNext, id);

    /// <summary>A paragraph of plain text. Line feeds break the line.</summary>
    /// <param name="text">The text.</param>
    [SetsRequiredMembers]
    public Paragraph(string text) => Inlines = [new TextRun(text)];

    /// <summary>A paragraph of mixed inlines.</summary>
    /// <param name="inlines">The inlines.</param>
    [SetsRequiredMembers]
    public Paragraph(ImmutableArray<Inline> inlines) => Inlines = inlines;

    /// <summary>The text.</summary>
    [JsonRequired]
    public required ImmutableArray<Inline> Inlines { get; init; }

    /// <summary>Horizontal alignment.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TextAlign Align { get; init; }

    /// <summary>Space after the paragraph. Null: the style's default.</summary>
    public Length? SpaceAfter { get; init; }
}

/// <summary>Empty vertical space.</summary>
/// <example>
/// <code>
/// var gap = new Spacer(Length.Mm(10));
/// </code>
/// </example>
public sealed record Spacer : Block
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal Spacer(Length height, string? style = null, bool keepWithNext = false, string? id = null) =>
        (Height, Style, KeepWithNext, Id) = (height, style, keepWithNext, id);

    /// <summary>A spacer of the given height.</summary>
    /// <param name="height">The height.</param>
    [SetsRequiredMembers]
    public Spacer(Length height) => Height = height;

    /// <summary>The height. A spacer at the bottom of a page doesn't carry over to the next.</summary>
    [JsonRequired]
    public required Length Height { get; init; }
}

/// <summary>Starts a new page.</summary>
public sealed record PageBreak : Block;

/// <summary>Content repeated on every page above or below the margin, such as a footer with the page number.</summary>
/// <example>
/// <code>
/// var footer = new PageBand([new Paragraph(["Page ", Field.PageNumber, " of ", Field.PageCount]) { Align = TextAlign.Right }]);
/// </code>
/// </example>
public sealed record PageBand
{
    /// <summary>A band with the given content.</summary>
    /// <param name="content">Paragraphs, images and spacers.</param>
    [JsonConstructor]
    [SetsRequiredMembers]
    public PageBand(ImmutableArray<Block> content) => Content = content;

    /// <summary>Paragraphs, images and spacers. It's an artifact for screen readers, not part of the text.</summary>
    [JsonRequired]
    public required ImmutableArray<Block> Content { get; init; }
}
