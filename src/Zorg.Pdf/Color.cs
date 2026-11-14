using System;
using System.Globalization;
using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>An sRGB colour. In JSON a string <c>"#RRGGBB"</c> or <c>"#RGB"</c>.</summary>
/// <param name="R">Red, 0 to 255.</param>
/// <param name="G">Green, 0 to 255.</param>
/// <param name="B">Blue, 0 to 255.</param>
/// <example>
/// <code>
/// Color brand = Color.Parse("#1F6FEB");
/// </code>
/// </example>
[JsonConverter(typeof(ColorJsonConverter))]
public readonly record struct Color(byte R, byte G, byte B)
{
    /// <summary>Black.</summary>
    public static Color Black { get; } = new(0, 0, 0);

    /// <summary>White.</summary>
    public static Color White { get; } = new(255, 255, 255);

    /// <summary>A grey from 0 (black) to 255 (white).</summary>
    /// <param name="level">The level.</param>
    /// <returns>The colour.</returns>
    public static Color Gray(byte level) => new(level, level, level);

    /// <summary>Parses <c>"#RRGGBB"</c> or <c>"#RGB"</c>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The colour.</returns>
    /// <exception cref="FormatException">The text isn't a hex colour.</exception>
    public static Color Parse(string text) =>
        TryParse(text, out Color color) ? color : throw new FormatException($"'{text}' isn't a colour. Use \"#RRGGBB\", such as \"#1F6FEB\".");

    /// <summary>Parses like <see cref="Parse"/> without throwing.</summary>
    /// <param name="text">The text.</param>
    /// <param name="color">The colour, when the text is valid.</param>
    /// <returns>True when the text is a colour.</returns>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (text is null || text.Length is not (4 or 7) || text[0] != '#')
        {
            return false;
        }

        if (!uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
        {
            return false;
        }

        color = text.Length == 7
            ? new Color((byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : new Color((byte)(((value >> 8) & 0xF) * 17), (byte)(((value >> 4) & 0xF) * 17), (byte)((value & 0xF) * 17));
        return true;
    }

    /// <summary>The colour as <c>"#RRGGBB"</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");
}
