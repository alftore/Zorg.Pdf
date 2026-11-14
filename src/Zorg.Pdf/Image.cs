using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>
/// A picture: JPEG or PNG. It needs <see cref="Alt"/> text for screen readers, unless it's <see cref="Decorative"/>.
/// </summary>
/// <example>
/// <code>
/// var logo = new Image(ImageSource.FromSlot("logo")) { Alt = "Zorg Consulting", Width = Length.Cm(4) };
/// </code>
/// </example>
public sealed record Image : Block
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal Image(ImageSource source, string? alt = null, bool decorative = false, Length? width = null, Length? height = null, TextAlign align = TextAlign.Left, string? style = null, bool keepWithNext = false) =>
        (Source, Alt, Decorative, Width, Height, Align, Style, KeepWithNext) = (source, alt, decorative, width, height, align, style, keepWithNext);

    /// <summary>An image from a source.</summary>
    /// <param name="source">The bytes, or a data slot.</param>
    [SetsRequiredMembers]
    public Image(ImageSource source) => Source = source;

    /// <summary>Where the image comes from.</summary>
    [JsonRequired]
    public required ImageSource Source { get; init; }

    /// <summary>What the image shows, read by screen readers. Required unless the image is decorative.</summary>
    public string? Alt { get; init; }

    /// <summary>The image adds nothing to the text, so screen readers skip it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Decorative { get; init; }

    /// <summary>The width. With only a width or a height set, the other keeps the aspect ratio.</summary>
    public Length? Width { get; init; }

    /// <summary>The height.</summary>
    public Length? Height { get; init; }

    /// <summary>Horizontal alignment.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TextAlign Align { get; init; }
}

/// <summary>
/// The bytes of an image, or a slot supplied when rendering. In JSON <c>{ "base64": "..." }</c> or
/// <c>{ "$data": "slot" }</c>. A document never names a file: reading files is the caller's decision, not the
/// document's.
/// </summary>
[JsonConverter(typeof(ImageSourceJsonConverter))]
public abstract record ImageSource
{
    /// <summary>An image from its file bytes.</summary>
    /// <param name="bytes">A JPEG or PNG file.</param>
    /// <returns>The source.</returns>
    public static ImageSource FromBytes(ReadOnlyMemory<byte> bytes) => new BytesImageSource(bytes);

    /// <summary>An image supplied when rendering, under a slot name.</summary>
    /// <param name="slot">The slot name.</param>
    /// <returns>The source.</returns>
    public static ImageSource FromSlot(string slot) => new SlotImageSource(slot);
}

/// <summary>An image from its file bytes.</summary>
/// <param name="Bytes">A JPEG or PNG file.</param>
public sealed record BytesImageSource(ReadOnlyMemory<byte> Bytes) : ImageSource;

/// <summary>An image supplied when rendering, under a slot name.</summary>
/// <param name="Slot">The slot name.</param>
public sealed record SlotImageSource(string Slot) : ImageSource;
