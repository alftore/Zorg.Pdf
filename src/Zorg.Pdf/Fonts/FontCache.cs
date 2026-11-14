using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Zorg.Pdf.Fonts;

/// <summary>
/// Parsed fonts shared by every render in the process. A <see cref="TrueTypeFont"/> is immutable once parsed, so
/// concurrent renders can share it; what a render changes (the glyphs it uses, a kerning cache) lives in its own
/// <see cref="EmbeddedFont"/>.
/// </summary>
/// <remarks>
/// Three ways in, cheapest first: the same byte array as before (by identity, no hashing); the same file, unchanged
/// (by path, last write time and length, no reading); anything else by a SHA-256 of the bytes.
/// </remarks>
internal static class FontCache
{
    /// <summary>The most fonts kept by content and by file. Past it a cache starts over: simple, and rare in practice.</summary>
    internal const int Capacity = 64;

    private static readonly ConcurrentDictionary<string, TrueTypeFont> ByContent = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string Path, DateTime Written, long Length), TrueTypeFont> ByFile = new();
    private static readonly ConditionalWeakTable<byte[], ArrayEntry> ByArray = [];

    internal static int Count => ByContent.Count;

    /// <summary>The key a file is cached under: full path, last write time and length, so a changed file is read again.</summary>
    public static (string Path, DateTime Written, long Length) FileKey(string path, int face = 0)
    {
        var info = new FileInfo(path);
        string full = Path.GetFullPath(path);
        return (face == 0 ? full : $"{full}#{face}", info.LastWriteTimeUtc, info.Exists ? info.Length : -1);
    }

    /// <summary>The parsed font for an unchanged file, without reading it.</summary>
    public static bool TryGetFile((string Path, DateTime Written, long Length) key, out TrueTypeFont font) => ByFile.TryGetValue(key, out font!);

    /// <summary>Parses a file's bytes (or finds them by content) and remembers the result under the file's key.</summary>
    public static TrueTypeFont AddFile((string Path, DateTime Written, long Length) key, ReadOnlyMemory<byte> bytes, int face = 0)
    {
        TrueTypeFont font = ByHash(bytes, face);
        if (ByFile.Count >= Capacity)
        {
            ByFile.Clear();
        }

        return ByFile.GetOrAdd(key, font);
    }

    /// <summary>The parsed font for some bytes: by array identity when the bytes are a whole array, else by content.</summary>
    public static TrueTypeFont GetOrParse(ReadOnlyMemory<byte> file, int face = 0)
    {
        if (MemoryMarshal.TryGetArray(file, out ArraySegment<byte> segment) && segment.Array is { } array && segment.Offset == 0 && segment.Count == array.Length)
        {
            ArrayEntry entry = ByArray.GetValue(array, _ => new ArrayEntry());
            lock (entry.Faces)
            {
                if (entry.Faces.TryGetValue(face, out TrueTypeFont? known))
                {
                    return known;
                }
            }

            TrueTypeFont parsed = ByHash(file, face);
            lock (entry.Faces)
            {
                entry.Faces[face] = parsed;
            }

            return parsed;
        }

        return ByHash(file, face);
    }

    private static TrueTypeFont ByHash(ReadOnlyMemory<byte> file, int face)
    {
        string key = Convert.ToHexString(SHA256.HashData(file.Span)) + (face == 0 ? "" : $"#{face}");
        if (ByContent.TryGetValue(key, out TrueTypeFont? font))
        {
            return font;
        }

        font = TrueTypeFont.Parse(file.ToArray(), face); // A copy: the caller's buffer may change after the render.
        if (ByContent.Count >= Capacity)
        {
            ByContent.Clear();
        }

        return ByContent.GetOrAdd(key, font);
    }

    /// <summary>Parsed faces kept alive as long as the array they came from.</summary>
    private sealed class ArrayEntry
    {
        public Dictionary<int, TrueTypeFont> Faces { get; } = [];
    }
}
