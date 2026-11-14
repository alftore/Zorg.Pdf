using System;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Pdf;

/// <summary>
/// Prepares images for embedding: decodes formats Zorg.Pdf doesn't read itself, applies orientation, converts colour,
/// or shrinks photos to the size they're drawn at. Set it on <see cref="RenderOptions.ImageProcessor"/>; the
/// <c>Zorg.Pdf.Imaging</c> package has one built on Zorg.Imaging. It's called once per distinct image, before the
/// first page, and its result is checked like any other image.
/// </summary>
/// <example>
/// <code>
/// sealed class Grayscale : IImageProcessor
/// {
///     public ValueTask&lt;PdfImage?&gt; ProcessAsync(ImageRequest request, CancellationToken cancellationToken) =&gt;
///         ValueTask.FromResult&lt;PdfImage?&gt;(null); // Null: Zorg.Pdf reads the image itself.
/// }
/// </code>
/// </example>
public interface IImageProcessor
{
    /// <summary>Prepares one image.</summary>
    /// <param name="request">The image's bytes and how large it's drawn.</param>
    /// <param name="cancellationToken">Cancels the render.</param>
    /// <returns>
    /// The image to embed, or null to let Zorg.Pdf read the bytes itself (JPEG and PNG). Throw a
    /// <see cref="PdfException"/> for an image that can't be used; its message is reported as ZPDF-I001, or as
    /// ZPDF-I002 for a <see cref="PdfLimitException"/>.
    /// </returns>
    /// <remarks>
    /// When <see cref="ImageRequest.Synchronous"/> is set the render is synchronous and the returned task must
    /// already be complete; one that isn't is reported as ZPDF-R002.
    /// </remarks>
    public ValueTask<PdfImage?> ProcessAsync(ImageRequest request, CancellationToken cancellationToken);
}

/// <summary>An image to prepare: its bytes, where it's used, and the largest box it's drawn in.</summary>
public sealed class ImageRequest
{
    internal ImageRequest(ReadOnlyMemory<byte> bytes, string path, double maxWidth, double maxHeight, bool synchronous) =>
        (Bytes, Path, MaxWidth, MaxHeight, Synchronous) = (bytes, path, Length.Pt(maxWidth), Length.Pt(maxHeight), synchronous);

    /// <summary>The file's bytes, as given in the document or the data.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>The JSON pointer of the first block that uses the image, such as <c>/content/3</c>.</summary>
    public string Path { get; }

    /// <summary>
    /// The widest the image is drawn, over every block that uses it: its set width, or the width of the page or band
    /// it's in. Drawing never exceeds it, so an image shrunk to fit it at the output resolution loses nothing.
    /// </summary>
    public Length MaxWidth { get; }

    /// <summary>The tallest the image is drawn: its set height, or the content height of the page.</summary>
    public Length MaxHeight { get; }

    /// <summary>The render is synchronous (<see cref="PdfDocument.Render"/>): complete the work before returning.</summary>
    public bool Synchronous { get; }
}

/// <summary>The pixel layout of <see cref="PdfImage.FromPixels"/>: 8 bits per channel, rows top to bottom, no padding.</summary>
public enum PdfPixelFormat
{
    /// <summary>One byte per pixel, black to white.</summary>
    Gray8,

    /// <summary>Red, green, blue: three bytes per pixel, sRGB.</summary>
    Rgb24,

    /// <summary>Cyan, magenta, yellow, black: four bytes per pixel, 0 is no ink.</summary>
    Cmyk32,
}

/// <summary>An image ready to embed, from an <see cref="IImageProcessor"/>.</summary>
/// <example>
/// <code>
/// PdfImage photo = PdfImage.Jpeg(jpegBytes) with { NaturalWidth = 4000, NaturalHeight = 3000 };
/// PdfImage icon = PdfImage.FromPixels(32, 32, PdfPixelFormat.Rgb24, rgb, alpha);
/// </code>
/// </example>
public sealed record PdfImage
{
    private PdfImage(bool isJpeg, ReadOnlyMemory<byte> data, int width, int height, PdfPixelFormat format, ReadOnlyMemory<byte> alpha) =>
        (IsJpeg, Data, PixelWidth, PixelHeight, Format, Alpha) = (isJpeg, data, width, height, format, alpha);

    /// <summary>A baseline or progressive 8-bit JPEG, embedded as it is.</summary>
    /// <param name="jpeg">The JPEG file.</param>
    /// <returns>The image.</returns>
    public static PdfImage Jpeg(ReadOnlyMemory<byte> jpeg) => new(true, jpeg, 0, 0, default, default);

    /// <summary>Pixels, which Zorg.Pdf compresses losslessly.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="format">The layout of <paramref name="pixels"/>.</param>
    /// <param name="pixels">The pixels, rows top to bottom.</param>
    /// <param name="alpha">Optional opacity, one byte per pixel (255 is opaque); empty for an opaque image.</param>
    /// <returns>The image.</returns>
    public static PdfImage FromPixels(int width, int height, PdfPixelFormat format, ReadOnlyMemory<byte> pixels, ReadOnlyMemory<byte> alpha = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        int channels = format switch { PdfPixelFormat.Gray8 => 1, PdfPixelFormat.Rgb24 => 3, PdfPixelFormat.Cmyk32 => 4, _ => throw new ArgumentOutOfRangeException(nameof(format)) };
        if (pixels.Length != (long)width * height * channels)
        {
            throw new ArgumentException($"{format} pixels for {width} × {height} take {(long)width * height * channels} bytes; got {pixels.Length}.", nameof(pixels));
        }

        if (!alpha.IsEmpty && alpha.Length != (long)width * height)
        {
            throw new ArgumentException($"Alpha for {width} × {height} takes {(long)width * height} bytes; got {alpha.Length}.", nameof(alpha));
        }

        return new PdfImage(false, pixels, width, height, format, alpha);
    }

    internal bool IsJpeg { get; }

    internal ReadOnlyMemory<byte> Data { get; }

    internal int PixelWidth { get; }

    internal int PixelHeight { get; }

    internal PdfPixelFormat Format { get; }

    internal ReadOnlyMemory<byte> Alpha { get; }

    /// <summary>
    /// The width in pixels to size the image by when its block sets no size (at 96 dpi), when that isn't the width of
    /// the pixels embedded: set it to the original's width after shrinking a photo, so it's drawn at the same size.
    /// </summary>
    public int? NaturalWidth { get; init; }

    /// <summary>The height to size the image by; see <see cref="NaturalWidth"/>.</summary>
    public int? NaturalHeight { get; init; }
}
