using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>Horizontal alignment of lines. In JSON <c>"left"</c>, <c>"center"</c>, <c>"right"</c> or <c>"justify"</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<TextAlign>))]
public enum TextAlign
{
    /// <summary>Lines start at the left edge.</summary>
    Left,

    /// <summary>Lines are centred.</summary>
    Center,

    /// <summary>Lines end at the right edge.</summary>
    Right,

    /// <summary>Spaces stretch so every line but the last of a paragraph fills the width.</summary>
    Justify,
}
