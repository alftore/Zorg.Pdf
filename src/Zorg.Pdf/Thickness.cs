using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>
/// Space on the four sides of something, such as page margins. In JSON either one length for all sides
/// (<c>"2cm"</c>) or an object with <c>top</c>, <c>right</c>, <c>bottom</c> and <c>left</c>.
/// </summary>
/// <param name="Top">The top.</param>
/// <param name="Right">The right side.</param>
/// <param name="Bottom">The bottom.</param>
/// <param name="Left">The left side.</param>
/// <example>
/// <code>
/// Thickness margins = Thickness.All(Length.Cm(2)) with { Top = Length.Cm(3) };
/// </code>
/// </example>
[JsonConverter(typeof(ThicknessJsonConverter))]
public readonly record struct Thickness(Length Top, Length Right, Length Bottom, Length Left)
{
    /// <summary>The same length on every side.</summary>
    /// <param name="length">The length.</param>
    /// <returns>The thickness.</returns>
    public static Thickness All(Length length) => new(length, length, length, length);
}
