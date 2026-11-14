using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>
/// How text looks. Every property is optional: an unset property comes from the style underneath (the run's block,
/// then the block's named style, then <c>body</c> in the <see cref="StyleSheet"/>).
/// </summary>
/// <example>
/// <code>
/// var note = new TextStyle { Size = Length.Pt(8), Color = Color.Gray(90), FontStyle = FontStyle.Italic };
/// </code>
/// </example>
public sealed record TextStyle
{
    /// <summary>Bold, nothing else.</summary>
    public static TextStyle Bold { get; } = new() { Weight = FontWeight.Bold };

    /// <summary>Italic, nothing else.</summary>
    public static TextStyle Italic { get; } = new() { FontStyle = Zorg.Pdf.FontStyle.Italic };

    /// <summary>A font family name from the render's font collection.</summary>
    public string? FontFamily { get; init; }

    /// <summary>The font size.</summary>
    public Length? Size { get; init; }

    /// <summary>Normal or bold.</summary>
    public FontWeight? Weight { get; init; }

    /// <summary>Normal or italic.</summary>
    public FontStyle? FontStyle { get; init; }

    /// <summary>The text colour.</summary>
    public Color? Color { get; init; }

    /// <summary>Line height as a multiple of the font size, such as 1.2.</summary>
    public double? LineHeight { get; init; }

    /// <summary>
    /// The language of the text, as a BCP 47 tag such as <c>"de"</c> or <c>"nb-NO"</c>, when it differs from the
    /// document's <see cref="PdfDocument.Language"/>. Screen readers switch voice for it (PDF/UA requires the language
    /// of every passage), and hyphenation will choose its patterns by it.
    /// </summary>
    /// <example>
    /// <code>
    /// new Paragraph(["The German word ", new TextRun("Fernweh") { Style = new TextStyle { Language = "de" } }, " has no English equal."])
    /// </code>
    /// </example>
    public string? Language { get; init; }

    /// <summary>This style with every property <paramref name="over"/> sets taking its place.</summary>
    /// <param name="over">The style on top, or null.</param>
    /// <returns>The merged style.</returns>
    public TextStyle Merge(TextStyle? over) => over is null ? this : new TextStyle
    {
        FontFamily = over.FontFamily ?? FontFamily,
        Size = over.Size ?? Size,
        Weight = over.Weight ?? Weight,
        FontStyle = over.FontStyle ?? FontStyle,
        Color = over.Color ?? Color,
        LineHeight = over.LineHeight ?? LineHeight,
        Language = over.Language ?? Language,
    };
}

/// <summary>Font weight. In JSON <c>"normal"</c> or <c>"bold"</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<FontWeight>))]
public enum FontWeight
{
    /// <summary>The regular face.</summary>
    Normal,

    /// <summary>The bold face.</summary>
    Bold,
}

/// <summary>Font style. In JSON <c>"normal"</c> or <c>"italic"</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<FontStyle>))]
public enum FontStyle
{
    /// <summary>Upright.</summary>
    Normal,

    /// <summary>The italic face.</summary>
    Italic,
}

/// <summary>
/// Named text styles. Blocks refer to them by name through <see cref="Block.Style"/>. The built-in names are
/// <c>body</c>, <c>heading1</c> to <c>heading6</c>, <c>tableHeader</c>, <c>tableCell</c> and <c>footer</c>; a document's
/// style sheet adds to them and overrides them by name.
/// </summary>
/// <example>
/// <code>
/// var styles = new StyleSheet
/// {
///     Styles = StyleSheet.Default.Styles.SetItem("body", new TextStyle { Size = Length.Pt(11) }),
/// };
/// </code>
/// </example>
public sealed record StyleSheet
{
    /// <summary>An empty style sheet; the built-in styles still apply underneath it.</summary>
    public StyleSheet()
    {
    }

    /// <summary>For the JSON serializer only: a missing property keeps its default.</summary>
    [JsonConstructor]
    internal StyleSheet(ImmutableDictionary<string, TextStyle>? styles, string? defaultFontFamily = null) =>
        (Styles, DefaultFontFamily) = (styles ?? ImmutableDictionary<string, TextStyle>.Empty, defaultFontFamily);

    /// <summary>The built-in styles.</summary>
    public static StyleSheet Default { get; } = new()
    {
        Styles = ImmutableDictionary.CreateRange<string, TextStyle>(
            System.StringComparer.Ordinal,
            [
                new("body", new TextStyle { Size = Length.Pt(10), LineHeight = 1.2 }),
                new("heading1", new TextStyle { Size = Length.Pt(22), Weight = FontWeight.Bold }),
                new("heading2", new TextStyle { Size = Length.Pt(16), Weight = FontWeight.Bold }),
                new("heading3", new TextStyle { Size = Length.Pt(13), Weight = FontWeight.Bold }),
                new("heading4", new TextStyle { Size = Length.Pt(11), Weight = FontWeight.Bold }),
                new("heading5", new TextStyle { Size = Length.Pt(10), Weight = FontWeight.Bold }),
                new("heading6", new TextStyle { Size = Length.Pt(10), Weight = FontWeight.Bold, FontStyle = FontStyle.Italic }),
                new("tableHeader", new TextStyle { Size = Length.Pt(9), Weight = FontWeight.Bold }),
                new("tableCell", new TextStyle { Size = Length.Pt(9) }),
                new("header", new TextStyle { Size = Length.Pt(8) }),
                new("footer", new TextStyle { Size = Length.Pt(8) }),
                new("link", new TextStyle { Color = Color.Parse("#0B57D0") }),
                new("toc1", new TextStyle { Weight = FontWeight.Bold }),
                new("watermark", new TextStyle { Weight = FontWeight.Bold }),
            ]),
    };

    /// <summary>Styles by name.</summary>
    public ImmutableDictionary<string, TextStyle> Styles { get; init; } = ImmutableDictionary<string, TextStyle>.Empty;

    /// <summary>The font family used where no style names one. Null: the render's default family.</summary>
    public string? DefaultFontFamily { get; init; }
}
