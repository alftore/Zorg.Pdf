using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>
/// Text or an image across every page, such as DRAFT, COPY or CONFIDENTIAL, turned and see-through. It's an
/// artifact, so screen readers skip it. Text uses the built-in <c>watermark</c> style; without a size it's scaled to
/// span most of the page diagonally.
/// In JSON <c>"watermark": { "text": "DRAFT", "angle": 45, "opacity": 0.15 }</c>.
/// </summary>
/// <example>
/// <code>
/// var draft = document with { Watermark = new Watermark("DRAFT") };
/// var logo = document with { Watermark = new Watermark(ImageSource.FromSlot("logo")) { Size = Length.Cm(12), Angle = 0, Layer = WatermarkLayer.Behind } };
/// </code>
/// </example>
public sealed record Watermark
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    internal Watermark(string? text = null, ImageSource? image = null, double angle = 45, double opacity = 0.15, Length? size = null, Color? color = null, WatermarkLayer layer = WatermarkLayer.Over) =>
        (Text, Image, Angle, Opacity, Size, Color, Layer) = (text, image, angle, opacity, size, color, layer);

    /// <summary>A text watermark.</summary>
    /// <param name="text">The text, one line.</param>
    public Watermark(string text) => Text = text;

    /// <summary>An image watermark.</summary>
    /// <param name="image">The image.</param>
    public Watermark(ImageSource image) => Image = image;

    /// <summary>The text. Set this or <see cref="Image"/>.</summary>
    public string? Text { get; init; }

    /// <summary>The image. Set this or <see cref="Text"/>.</summary>
    public ImageSource? Image { get; init; }

    /// <summary>Counterclockwise turn in degrees: 45 by default, rising from bottom left to top right.</summary>
    public double Angle { get; init; } = 45;

    /// <summary>0 (invisible) to 1 (solid): 0.15 by default, light enough to read through.</summary>
    public double Opacity { get; init; } = 0.15;

    /// <summary>The font size of text, or the width of an image. Null: text spans most of the page diagonally, an image half the page width.</summary>
    public Length? Size { get; init; }

    /// <summary>The colour of text. Null: mid grey.</summary>
    public Color? Color { get; init; }

    /// <summary>Over the content (the default, visible on filled table rows too) or behind it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public WatermarkLayer Layer { get; init; }
}

/// <summary>Where a <see cref="Watermark"/> is drawn. In JSON <c>"over"</c> or <c>"behind"</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<WatermarkLayer>))]
public enum WatermarkLayer
{
    /// <summary>Over the content.</summary>
    Over,

    /// <summary>Behind the content: hidden by filled areas such as table headers.</summary>
    Behind,
}
