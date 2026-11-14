using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Zorg.Pdf.Fonts;

/// <summary>
/// Parsed fonts shared by every render in the process, keyed by a SHA-256 of the file. Parsing a large font takes
/// milliseconds; hashing it takes a fraction of that. A <see cref="TrueTypeFont"/> is immutable once parsed, so
/// concurrent renders can share it; what a render changes (the glyphs it uses, a kerning cache) lives in its own
/// <see cref="EmbeddedFont"/>.
/// </summary>
internal static class FontCache
{
    /// <summary>The most fonts kept. Past it the cache starts over: simple, and rare in practice.</summary>
    internal const int Capacity = 64;

    private static readonly ConcurrentDictionary<string, TrueTypeFont> Fonts = new(StringComparer.Ordinal);

    public static TrueTypeFont GetOrParse(ReadOnlyMemory<byte> file)
    {
        string key = Convert.ToHexString(SHA256.HashData(file.Span));
        if (Fonts.TryGetValue(key, out TrueTypeFont? font))
        {
            return font;
        }

        font = TrueTypeFont.Parse(file.ToArray()); // A copy: the caller's buffer may change after the render.
        if (Fonts.Count >= Capacity)
        {
            Fonts.Clear();
        }

        return Fonts.GetOrAdd(key, font);
    }

    internal static int Count => Fonts.Count;
}
