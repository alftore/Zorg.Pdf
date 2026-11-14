using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>
/// A paper size, portrait. In JSON a name (<c>"A4"</c>, <c>"A3"</c>, <c>"A5"</c>, <c>"Letter"</c>, <c>"Legal"</c>) or an
/// object with <c>width</c> and <c>height</c>.
/// </summary>
/// <param name="Width">The width in portrait.</param>
/// <param name="Height">The height in portrait.</param>
[JsonConverter(typeof(PageSizeJsonConverter))]
public readonly record struct PageSize(Length Width, Length Height)
{
    /// <summary>ISO A3, 297 × 420 mm.</summary>
    public static PageSize A3 { get; } = new(Length.Mm(297), Length.Mm(420));

    /// <summary>ISO A4, 210 × 297 mm.</summary>
    public static PageSize A4 { get; } = new(Length.Mm(210), Length.Mm(297));

    /// <summary>ISO A5, 148 × 210 mm.</summary>
    public static PageSize A5 { get; } = new(Length.Mm(148), Length.Mm(210));

    /// <summary>US Letter, 8.5 × 11 in.</summary>
    public static PageSize Letter { get; } = new(Length.In(8.5), Length.In(11));

    /// <summary>US Legal, 8.5 × 14 in.</summary>
    public static PageSize Legal { get; } = new(Length.In(8.5), Length.In(14));
}

/// <summary>Which way up the page is.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<PageOrientation>))]
public enum PageOrientation
{
    /// <summary>Taller than wide.</summary>
    Portrait,

    /// <summary>Wider than tall: the page size's width and height swap.</summary>
    Landscape,
}

/// <summary>Size, orientation and margins of every page.</summary>
/// <example>
/// <code>
/// PageSettings page = PageSettings.A4 with { Orientation = PageOrientation.Landscape, Margin = Thickness.All(Length.Cm(1.5)) };
/// </code>
/// </example>
[JsonConverter(typeof(PageSettingsJsonConverter))]
public sealed record PageSettings
{
    /// <summary>A4 portrait with 2 cm margins; change it with an object initialiser.</summary>
    public PageSettings()
    {
    }

    /// <summary>A4 portrait with 2 cm margins.</summary>
    public static PageSettings A4 { get; } = new();

    /// <summary>US Letter portrait with 2 cm margins.</summary>
    public static PageSettings Letter { get; } = new() { Size = PageSize.Letter };

    /// <summary>The paper size. A4 by default.</summary>
    public PageSize Size { get; init; } = PageSize.A4;

    /// <summary>Portrait or landscape.</summary>
    public PageOrientation Orientation { get; init; }

    /// <summary>The margins. 2 cm on every side by default.</summary>
    public Thickness Margin { get; init; } = Thickness.All(Length.Cm(2));
}
