using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>
/// Paragraphs at a fixed place on the page, such as the address of a letter for a window envelope. The box is drawn
/// on the page the content has reached, measured from the paper's top-left corner, and takes no room in the flow.
/// Text too long for the box is made smaller (<see cref="TextOverflow.ShrinkToFit"/>, the default) or cut.
/// In JSON <c>{ "type": "textBox", "preset": "din5008B", "content": [paragraphs] }</c>, or with <c>x</c>, <c>y</c>,
/// <c>width</c> and <c>height</c>.
/// </summary>
/// <example>
/// <code>
/// var address = new TextBox([new Paragraph([new Bind("customer.name")]), new Paragraph([new Bind("customer.street")])])
/// {
///     Preset = TextBoxPreset.Din5008B,          // 85 × 45 mm, 20 mm from the left, 45 mm from the top
/// };
/// var reference = new TextBox([new Paragraph("Our ref: 2026-0042")])
/// {
///     X = Length.Mm(125), Y = Length.Mm(50), Width = Length.Mm(65), Height = Length.Mm(20), Overflow = TextOverflow.Clip,
/// };
/// </code>
/// </example>
public sealed record TextBox : Block
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal TextBox(
        ImmutableArray<Block> content,
        TextBoxPreset preset = TextBoxPreset.None,
        Length? x = null,
        Length? y = null,
        Length? width = null,
        Length? height = null,
        Thickness? padding = null,
        Color? borderColor = null,
        Length? borderWidth = null,
        TextOverflow overflow = TextOverflow.ShrinkToFit,
        Length? minSize = null,
        string? style = null,
        bool keepWithNext = false) =>
        (Content, Preset, X, Y, Width, Height, Padding, BorderColor, BorderWidth, Overflow, MinSize, Style, KeepWithNext) =
        (content, preset, x, y, width, height, padding, borderColor, borderWidth, overflow, minSize, style, keepWithNext);

    /// <summary>A box of paragraphs. Set its place with <see cref="Preset"/>, or <see cref="X"/>, <see cref="Y"/>, <see cref="Width"/> and <see cref="Height"/>.</summary>
    /// <param name="content">Paragraphs, or template parts that make paragraphs.</param>
    [SetsRequiredMembers]
    public TextBox(ImmutableArray<Block> content) => Content = content;

    /// <summary>The paragraphs, in reading order. <see cref="Repeat"/> and <see cref="Conditional"/> may make them.</summary>
    [JsonRequired]
    public required ImmutableArray<Block> Content { get; init; }

    /// <summary>A standard place and size. Any of <see cref="X"/>, <see cref="Y"/>, <see cref="Width"/>, <see cref="Height"/> and <see cref="Padding"/> set as well override it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TextBoxPreset Preset { get; init; }

    /// <summary>The left edge, from the paper's left edge.</summary>
    public Length? X { get; init; }

    /// <summary>The top edge, from the paper's top edge.</summary>
    public Length? Y { get; init; }

    /// <summary>The width, padding included.</summary>
    public Length? Width { get; init; }

    /// <summary>The height, padding included. The text must fit inside it; see <see cref="Overflow"/>.</summary>
    public Length? Height { get; init; }

    /// <summary>Space between the box's edges and its text. Null: none, or the preset's.</summary>
    public Thickness? Padding { get; init; }

    /// <summary>The colour of a border around the box. Null: no border.</summary>
    public Color? BorderColor { get; init; }

    /// <summary>The border's width. Null: 0.5 pt when there's a border colour.</summary>
    public Length? BorderWidth { get; init; }

    /// <summary>What happens to text that doesn't fit: made smaller (the default), cut, or drawn past the box.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TextOverflow Overflow { get; init; }

    /// <summary>The smallest body text size <see cref="TextOverflow.ShrinkToFit"/> goes down to. Null: 6 pt.</summary>
    public Length? MinSize { get; init; }

    /// <summary>The box's place and padding from the preset and the properties set, or null when something is missing.</summary>
    internal (double X, double Y, double Width, double Height, Thickness Padding)? Resolve()
    {
        (Length? x, Length? y, Length? width, Length? height, Thickness padding) = Preset switch
        {
            // DIN 5008: the address field is 85 × 45 mm, 20 mm from the left edge; its text starts at the 25 mm margin.
            TextBoxPreset.Din5008A => (Length.Mm(20), Length.Mm(27), Length.Mm(85), Length.Mm(45), new Thickness(Length.Zero, Length.Zero, Length.Zero, Length.Mm(5))),
            TextBoxPreset.Din5008B => (Length.Mm(20), Length.Mm(45), Length.Mm(85), Length.Mm(45), new Thickness(Length.Zero, Length.Zero, Length.Zero, Length.Mm(5))),
            _ => ((Length?)null, (Length?)null, (Length?)null, (Length?)null, default(Thickness)),
        };
        x = X ?? x;
        y = Y ?? y;
        width = Width ?? width;
        height = Height ?? height;
        return x is { } left && y is { } top && width is { } w && height is { } h
            ? (left.Points, top.Points, w.Points, h.Points, Padding ?? padding)
            : null;
    }
}

/// <summary>Standard places for a <see cref="TextBox"/>. In JSON <c>"din5008A"</c> or <c>"din5008B"</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<TextBoxPreset>))]
public enum TextBoxPreset
{
    /// <summary>No preset: set the place yourself.</summary>
    None,

    /// <summary>
    /// The DIN 5008 address field for form A (a short letterhead): 85 × 45 mm, 20 mm from the left and 27 mm from the
    /// top, text from 25 mm. Check it against your envelope's window.
    /// </summary>
    Din5008A,

    /// <summary>
    /// The DIN 5008 address field for form B (the common one): 85 × 45 mm, 20 mm from the left and 45 mm from the
    /// top, text from 25 mm. Check it against your envelope's window.
    /// </summary>
    Din5008B,
}

/// <summary>What a <see cref="TextBox"/> does with text that doesn't fit. In JSON <c>"shrinkToFit"</c>, <c>"clip"</c> or <c>"visible"</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<TextOverflow>))]
public enum TextOverflow
{
    /// <summary>
    /// Makes all the text smaller, keeping its proportions, until it fits or reaches the box's minimum size; then
    /// cuts what's left as <see cref="Clip"/> does. Information ZPDF-L005 says how far it shrank.
    /// </summary>
    ShrinkToFit,

    /// <summary>Draws the whole lines that fit and leaves the rest out, with warning ZPDF-L006.</summary>
    Clip,

    /// <summary>Draws everything, past the bottom of the box if need be, with warning ZPDF-L006.</summary>
    Visible,
}
