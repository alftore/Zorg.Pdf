using System;
using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>How words in text may be hyphenated at line ends. In JSON <c>"manual"</c>, <c>"auto"</c> or <c>"none"</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<Hyphens>))]
public enum Hyphens
{
    /// <summary>Only at soft hyphens (U+00AD) in the text: the default.</summary>
    Manual,

    /// <summary>
    /// Wherever the text's language allows, by <see cref="RenderOptions.Hyphenator"/> (such as the one in
    /// <c>Zorg.Pdf.Hyphenation</c>), and at soft hyphens. Only a word that doesn't fit at the end of a line is
    /// hyphenated.
    /// </summary>
    Auto,

    /// <summary>Never: soft hyphens are ignored too.</summary>
    None,
}

/// <summary>
/// Finds where words may be hyphenated, for text whose style sets <see cref="Hyphens.Auto"/>. It's asked once per
/// word that overflows a line, so it should be fast and may cache.
/// </summary>
/// <example>
/// <code>
/// var options = new RenderOptions { Fonts = fonts, Hyphenator = PatternHyphenator.Default }; // Zorg.Pdf.Hyphenation
/// </code>
/// </example>
public interface IHyphenator
{
    /// <summary>Whether the hyphenator knows a language (a BCP 47 tag such as <c>"nb-NO"</c>).</summary>
    /// <param name="language">The language tag.</param>
    /// <returns>True when <see cref="TryHyphenate"/> can hyphenate words in it.</returns>
    public bool Supports(string language);

    /// <summary>Marks where a word may be hyphenated.</summary>
    /// <param name="language">The language tag.</param>
    /// <param name="word">The word: letters only, as written (any case).</param>
    /// <param name="breakAfter">Set to true at each index after which a hyphen may go; as long as the word, all false on entry.</param>
    /// <returns>False when the language isn't known.</returns>
    public bool TryHyphenate(string language, ReadOnlySpan<char> word, Span<bool> breakAfter);
}
