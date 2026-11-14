using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Pdf.Fonts;

/// <summary>
/// The fonts a render may use, by family name. There are no system fonts and no bundled font: a render with an empty
/// collection and no <see cref="RenderOptions.FontResolver"/> fails before writing anything, with diagnostic
/// ZPDF-F001, so output never depends on the machine unless you ask for that.
/// </summary>
/// <example>
/// <code>
/// static readonly FontCollection Fonts = FontCollection.Empty
///     .Add(new FontFamily("Inter")
///     {
///         Regular = FontSource.FromFile("fonts/Inter-Regular.ttf"),
///         Bold = FontSource.FromFile("fonts/Inter-Bold.ttf"),
///         Fallbacks = ["Noto Sans Symbols"],
///     });
/// </code>
/// </example>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "A set of fonts by family name; it names what it holds, as System.Drawing.Text.FontCollection does.")]
public sealed class FontCollection
{
    private FontCollection(ImmutableDictionary<string, FontFamily> families, string? defaultFamily) =>
        (Families, DefaultFamily) = (families, defaultFamily);

    /// <summary>No fonts.</summary>
    public static FontCollection Empty { get; } = new(ImmutableDictionary<string, FontFamily>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase), null);

    /// <summary>The families, by name (case-insensitive).</summary>
    public ImmutableDictionary<string, FontFamily> Families { get; }

    /// <summary>The family used where no style names one: the one set by <see cref="WithDefault"/>, else the first added.</summary>
    public string? DefaultFamily { get; }

    /// <summary>A collection with <paramref name="family"/> added, replacing one of the same name.</summary>
    /// <param name="family">The family.</param>
    /// <returns>The new collection; this one is unchanged.</returns>
    public FontCollection Add(FontFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return new FontCollection(Families.SetItem(family.Name, family), DefaultFamily ?? family.Name);
    }

    /// <summary>
    /// A collection of the TrueType files given, grouped into families by the names inside them. Where a family has
    /// several files for one face (Light, Regular and Medium are all "regular"), the weight nearest 400, or 700 for
    /// bold, wins. Files that aren't TrueType fonts this library can embed (CFF outlines, collections) are skipped.
    /// </summary>
    /// <param name="paths">Font files, read now for their names and again when a render uses them.</param>
    /// <returns>The collection. Its default family is the first by name, in ordinal order; change it with <see cref="WithDefault"/>.</returns>
    /// <example>
    /// <code>
    /// FontCollection fonts = FontCollection.FromFiles(["fonts/Inter-Regular.ttf", "fonts/Inter-Bold.ttf"]);
    /// </code>
    /// </example>
    public static FontCollection FromFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var faces = new List<(string Family, bool Bold, bool Italic, int Weight, string Path, int Face)>();
        foreach (string path in paths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(paths));
            foreach ((string family, bool bold, bool italic, int weight, int face) in SystemFontResolver.DescribeFaces(path))
            {
                faces.Add((family, bold, italic, weight, Path.GetFullPath(path), face));
            }
        }

        FontCollection collection = Empty;
        foreach (IGrouping<string, (string Family, bool Bold, bool Italic, int Weight, string Path, int Face)> family in faces
            .GroupBy(f => f.Family, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            FontSource? Pick(bool bold, bool italic) => family
                .Where(f => f.Bold == bold && f.Italic == italic)
                .OrderBy(f => Math.Abs(f.Weight - (bold ? 700 : 400)))
                .ThenBy(f => f.Path, StringComparer.Ordinal)
                .ThenBy(f => f.Face)
                .Select(f => FontSource.FromFile(f.Path, f.Face))
                .FirstOrDefault();

            FontSource? regular = Pick(false, false);
            if (regular is null)
            {
                continue; // A family is only usable with a regular face.
            }

            collection = collection.Add(new FontFamily(family.Key)
            {
                Regular = regular,
                Bold = Pick(true, false),
                Italic = Pick(false, true),
                BoldItalic = Pick(true, true),
            });
        }

        return collection;
    }

    /// <summary>A collection of the TrueType files in a folder and its subfolders; see <see cref="FromFiles"/>.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns>The collection, empty when the folder has no usable fonts.</returns>
    /// <example>
    /// <code>
    /// var options = new RenderOptions { Fonts = FontCollection.FromFolder("fonts").WithDefault("Inter") };
    /// </code>
    /// </example>
    public static FontCollection FromFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return FromFiles(Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal));
    }

    /// <summary>A collection with every family of <paramref name="other"/> added, replacing ones of the same name.</summary>
    /// <param name="other">The families to add.</param>
    /// <returns>The new collection; its default family is this one's, else the other's.</returns>
    /// <example>
    /// <code>
    /// FontCollection fonts = FontCollection.FromFolder("fonts").AddRange(FontCollection.FromFolder("brand-fonts"));
    /// </code>
    /// </example>
    public FontCollection AddRange(FontCollection other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new FontCollection(Families.SetItems(other.Families), DefaultFamily ?? other.DefaultFamily);
    }

    /// <summary>A collection whose default family is <paramref name="familyName"/>.</summary>
    /// <param name="familyName">A family name. It needn't be in the collection when a font resolver will find it.</param>
    /// <returns>The new collection.</returns>
    public FontCollection WithDefault(string familyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyName);
        return new FontCollection(Families, familyName);
    }
}

/// <summary>
/// A font family: up to four faces and the families to try for characters it doesn't have. Text in a face the
/// family lacks uses the regular face, with warning ZPDF-F003.
/// </summary>
/// <param name="Name">The name styles refer to, such as <c>"Inter"</c>. Case-insensitive.</param>
/// <example>
/// <code>
/// var inter = new FontFamily("Inter")
/// {
///     Regular = FontSource.FromFile("Inter-Regular.ttf"),
///     Bold = FontSource.FromFile("Inter-Bold.ttf"),
///     Italic = FontSource.FromFile("Inter-Italic.ttf"),
/// };
/// </code>
/// </example>
public sealed record FontFamily(string Name)
{
    /// <summary>The regular face. Required for a family to be used.</summary>
    public FontSource? Regular { get; init; }

    /// <summary>The bold face.</summary>
    public FontSource? Bold { get; init; }

    /// <summary>The italic face.</summary>
    public FontSource? Italic { get; init; }

    /// <summary>The bold italic face. Without it, bold italic text uses the bold face, then the italic face.</summary>
    public FontSource? BoldItalic { get; init; }

    /// <summary>Family names tried in order for characters this family has no glyph for.</summary>
    public ImmutableArray<string> Fallbacks { get; init; } = [];

    /// <summary>The source for a face, falling back through the faces this family has; null when it has no regular face.</summary>
    internal (FontSource? Source, bool Exact) Face(FontWeight weight, FontStyle style) => (weight, style) switch
    {
        (FontWeight.Bold, FontStyle.Italic) when BoldItalic is not null => (BoldItalic, true),
        (FontWeight.Bold, FontStyle.Italic) => (Bold ?? Italic ?? Regular, false),
        (FontWeight.Bold, _) => (Bold ?? Regular, Bold is not null),
        (_, FontStyle.Italic) => (Italic ?? Regular, Italic is not null),
        _ => (Regular, true),
    };
}

/// <summary>Where a font file comes from: its bytes, or a file read when the render starts.</summary>
public abstract record FontSource
{
    /// <summary>A font from its file's bytes (TrueType outlines, <c>.ttf</c>).</summary>
    /// <param name="bytes">The file.</param>
    /// <returns>The source.</returns>
    public static FontSource FromBytes(ReadOnlyMemory<byte> bytes) => new BytesFontSource(bytes, 0);

    /// <summary>One face of a TrueType collection (<c>.ttc</c>) from its bytes.</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="face">The face, from 0. A single font has only face 0.</param>
    /// <returns>The source.</returns>
    /// <example>
    /// <code>
    /// FontSource cambria = FontSource.FromBytes(File.ReadAllBytes("cambria.ttc"), face: 0);
    /// </code>
    /// </example>
    public static FontSource FromBytes(ReadOnlyMemory<byte> bytes, int face)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(face);
        return new BytesFontSource(bytes, face);
    }

    /// <summary>A font file, read when a render that uses it starts.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The source.</returns>
    public static FontSource FromFile(string path) => FromFile(path, 0);

    /// <summary>One face of a TrueType collection file (<c>.ttc</c>), read when a render that uses it starts.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="face">The face, from 0. A single font has only face 0.</param>
    /// <returns>The source.</returns>
    /// <example>
    /// <code>
    /// FontSource yuGothic = FontSource.FromFile(@"C:\Windows\Fonts\YuGothR.ttc", face: 1);
    /// </code>
    /// </example>
    public static FontSource FromFile(string path, int face)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(face);
        return new FileFontSource(path, face);
    }

    /// <summary>A short description for messages, such as the file name.</summary>
    internal abstract string Describe();

    /// <summary>The parsed font, through the process-wide <see cref="FontCache"/>.</summary>
    internal abstract ValueTask<TrueTypeFont> LoadAsync(bool synchronous, CancellationToken cancellationToken);
}

/// <summary>A font from bytes in memory.</summary>
/// <param name="Bytes">The font file.</param>
/// <param name="Face">The face of a collection, or 0.</param>
internal sealed record BytesFontSource(ReadOnlyMemory<byte> Bytes, int Face) : FontSource
{
    internal override string Describe() => Face == 0 ? $"{Bytes.Length:N0} bytes" : $"{Bytes.Length:N0} bytes, face {Face}";

    internal override ValueTask<TrueTypeFont> LoadAsync(bool synchronous, CancellationToken cancellationToken) => ValueTask.FromResult(FontCache.GetOrParse(Bytes, Face));
}

/// <summary>A font read from a file.</summary>
/// <param name="Path">The file path.</param>
/// <param name="Face">The face of a collection, or 0.</param>
internal sealed record FileFontSource(string Path, int Face) : FontSource
{
    internal override string Describe() => Face == 0 ? System.IO.Path.GetFileName(Path) : $"{System.IO.Path.GetFileName(Path)}, face {Face}";

    internal override ValueTask<TrueTypeFont> LoadAsync(bool synchronous, CancellationToken cancellationToken)
    {
        // An unchanged file is neither read nor hashed again.
        (string, DateTime, long) key = FontCache.FileKey(Path, Face);
        if (FontCache.TryGetFile(key, out TrueTypeFont font))
        {
            return ValueTask.FromResult(font);
        }

        return synchronous ? ValueTask.FromResult(FontCache.AddFile(key, ReadSync(), Face)) : ReadCoreAsync(key, cancellationToken);
    }

    private byte[] ReadSync() => File.ReadAllBytes(Path);

    private async ValueTask<TrueTypeFont> ReadCoreAsync((string, DateTime, long) key, CancellationToken cancellationToken) =>
        FontCache.AddFile(key, await File.ReadAllBytesAsync(Path, cancellationToken).ConfigureAwait(false), Face);
}

/// <summary>A request for one face of a family.</summary>
/// <param name="Family">The family name, as a style names it.</param>
/// <param name="Weight">Normal or bold.</param>
/// <param name="Style">Normal or italic.</param>
public readonly record struct FontRequest(string Family, FontWeight Weight, FontStyle Style);

/// <summary>
/// Finds fonts the <see cref="FontCollection"/> doesn't have, such as a company's font server or the system's fonts.
/// It's asked once per face a render needs, before anything is written.
/// </summary>
/// <example>
/// <code>
/// var options = new RenderOptions { FontResolver = FontResolvers.System() };   // opt-in: output then depends on the machine
/// </code>
/// </example>
public interface IFontResolver
{
    /// <summary>The source for a face, or null when the resolver doesn't have the family.</summary>
    /// <param name="request">The family and face.</param>
    /// <param name="cancellationToken">Stops the lookup.</param>
    /// <returns>A source, or null. Return the nearest face of the family when the exact one is missing.</returns>
    public ValueTask<FontSource?> ResolveAsync(FontRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Ready-made font resolvers.</summary>
public static class FontResolvers
{
    /// <summary>
    /// The fonts installed on this machine (TrueType files in the usual Windows, macOS and Linux font folders), found
    /// by family name. An opt-in: the same document then renders differently on machines with different fonts. The
    /// folders are scanned once per process, reading only each file's name and style tables.
    /// </summary>
    /// <returns>The resolver.</returns>
    public static IFontResolver System() => SystemFontResolver.Instance;
}
