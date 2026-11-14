using System;
using System.Globalization;
using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>
/// A distance on the page, stored in PDF points (1/72 inch). In JSON a length is a number of points, or a string with
/// a unit: <c>"12pt"</c>, <c>"5mm"</c>, <c>"2cm"</c>, <c>"0.5in"</c>.
/// </summary>
/// <example>
/// <code>
/// Length margin = Length.Cm(2);          // 56.69 pt
/// Length gap = Length.Parse("6pt");
/// </code>
/// </example>
[JsonConverter(typeof(LengthJsonConverter))]
public readonly record struct Length(double Points)
{
    /// <summary>Zero.</summary>
    public static Length Zero { get; }

    /// <summary>A length in points (1/72 inch).</summary>
    /// <param name="value">Points.</param>
    /// <returns>The length.</returns>
    public static Length Pt(double value) => new(value);

    /// <summary>A length in millimetres.</summary>
    /// <param name="value">Millimetres.</param>
    /// <returns>The length.</returns>
    public static Length Mm(double value) => new(value * 72 / 25.4);

    /// <summary>A length in centimetres.</summary>
    /// <param name="value">Centimetres.</param>
    /// <returns>The length.</returns>
    public static Length Cm(double value) => new(value * 72 / 2.54);

    /// <summary>A length in inches.</summary>
    /// <param name="value">Inches.</param>
    /// <returns>The length.</returns>
    public static Length In(double value) => new(value * 72);

    /// <summary>Parses <c>"12pt"</c>, <c>"5mm"</c>, <c>"2cm"</c>, <c>"0.5in"</c> or a plain number of points.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The length.</returns>
    /// <exception cref="FormatException">The text isn't a number with one of the units.</exception>
    public static Length Parse(string text) =>
        TryParse(text, out Length length) ? length : throw new FormatException($"'{text}' isn't a length. Use a number of points or a number with pt, mm, cm or in, such as \"2cm\".");

    /// <summary>Parses like <see cref="Parse"/> without throwing.</summary>
    /// <param name="text">The text.</param>
    /// <param name="length">The length, when the text is valid.</param>
    /// <returns>True when the text is a length.</returns>
    public static bool TryParse(string? text, out Length length)
    {
        length = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        double scale = span.EndsWith("mm", StringComparison.OrdinalIgnoreCase) ? 72 / 25.4
            : span.EndsWith("cm", StringComparison.OrdinalIgnoreCase) ? 72 / 2.54
            : span.EndsWith("in", StringComparison.OrdinalIgnoreCase) ? 72
            : 1;
        if (scale != 1 || span.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            span = span[..^2];
        }

        if (!double.TryParse(span.TrimEnd(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
        {
            return false;
        }

        length = new Length(value * scale);
        return true;
    }

    /// <summary>The length in points, such as <c>"56.6929pt"</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Math.Round(Points, 4).ToString(CultureInfo.InvariantCulture) + "pt";
}
