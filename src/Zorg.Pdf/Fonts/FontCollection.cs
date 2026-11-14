using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Pdf.Fonts;

/// <summary>
/// The fonts a render may use, by family name. There are no system fonts and no bundled font: a render with an empty
/// collection fails before writing anything, with diagnostic ZPDF-F001, so output never depends on the machine.
/// </summary>
/// <example>
/// <code>
/// static readonly FontCollection Fonts = FontCollection.Empty
///     .Add(new FontFamily("Inter") { Regular = FontSource.FromFile("fonts/Inter-Regular.ttf") });
///
/// var options = new RenderOptions { Fonts = Fonts };
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

    /// <summary>A collection whose default family is <paramref name="familyName"/>.</summary>
    /// <param name="familyName">A family in the collection.</param>
    /// <returns>The new collection.</returns>
    /// <exception cref="ArgumentException">There's no family of that name.</exception>
    public FontCollection WithDefault(string familyName) =>
        Families.ContainsKey(familyName)
            ? new FontCollection(Families, familyName)
            : throw new ArgumentException($"There's no font family named '{familyName}' in the collection.", nameof(familyName));
}

/// <summary>
/// A font family. 0.1 draws all text with <see cref="Regular"/>; the other faces are used once bold and italic text
/// is supported.
/// </summary>
/// <param name="Name">The name styles refer to, such as <c>"Inter"</c>. Case-insensitive.</param>
public sealed record FontFamily(string Name)
{
    /// <summary>The regular face. Required for a family to be used.</summary>
    public FontSource? Regular { get; init; }
}

/// <summary>Where a font file comes from: its bytes, or a file read when the render starts.</summary>
public abstract record FontSource
{
    /// <summary>A font from its file's bytes (TrueType outlines, <c>.ttf</c>).</summary>
    /// <param name="bytes">The file.</param>
    /// <returns>The source.</returns>
    public static FontSource FromBytes(ReadOnlyMemory<byte> bytes) => new BytesFontSource(bytes);

    /// <summary>A font file, read when a render that uses it starts.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The source.</returns>
    public static FontSource FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new FileFontSource(path);
    }

    /// <summary>A short description for messages, such as the file name.</summary>
    internal abstract string Describe();

    internal abstract ValueTask<ReadOnlyMemory<byte>> ReadAsync(bool synchronous, CancellationToken cancellationToken);
}

/// <summary>A font from bytes in memory.</summary>
/// <param name="Bytes">The font file.</param>
[SuppressMessage("Design", "CA1812", Justification = "Created by FontSource.FromBytes.")]
internal sealed record BytesFontSource(ReadOnlyMemory<byte> Bytes) : FontSource
{
    internal override string Describe() => $"{Bytes.Length:N0} bytes";

    internal override ValueTask<ReadOnlyMemory<byte>> ReadAsync(bool synchronous, CancellationToken cancellationToken) => ValueTask.FromResult(Bytes);
}

/// <summary>A font read from a file.</summary>
/// <param name="Path">The file path.</param>
internal sealed record FileFontSource(string Path) : FontSource
{
    internal override string Describe() => System.IO.Path.GetFileName(Path);

    internal override ValueTask<ReadOnlyMemory<byte>> ReadAsync(bool synchronous, CancellationToken cancellationToken) =>
        synchronous ? ValueTask.FromResult<ReadOnlyMemory<byte>>(ReadSync()) : ReadCoreAsync(cancellationToken);

    private byte[] ReadSync() => File.ReadAllBytes(Path);

    private async ValueTask<ReadOnlyMemory<byte>> ReadCoreAsync(CancellationToken cancellationToken) =>
        await File.ReadAllBytesAsync(Path, cancellationToken).ConfigureAwait(false);
}
