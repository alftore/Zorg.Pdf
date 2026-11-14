using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging;
using ZorgImage = Zorg.Imaging.Image;

namespace Zorg.Pdf.Imaging;

/// <summary>
/// An <see cref="IImageProcessor"/> built on Zorg.Imaging. It reads every format Zorg.Imaging decodes (JPEG, PNG, WebP,
/// GIF, BMP, TIFF), turns photos the right way up from their EXIF orientation, converts other colour spaces (CMYK,
/// ICC profiles) to sRGB, and shrinks images larger than they're printed at. JPEG and PNG that need none of that go
/// in untouched, as Zorg.Pdf reads them itself.
/// </summary>
/// <example>
/// <code>
/// var options = new RenderOptions
/// {
///     Fonts = fonts,
///     ImageProcessor = new ImagingProcessor { Resolution = 200 },
/// };
/// </code>
/// </example>
public sealed class ImagingProcessor : IImageProcessor
{
    /// <summary>Points per inch.</summary>
    private const double PointsPerInch = 72;

    /// <summary>
    /// The most pixels per inch an image keeps at the largest size it's drawn: 300, print quality. Larger images are
    /// shrunk to it; smaller ones are never enlarged.
    /// </summary>
    public int Resolution { get; init; } = 300;

    /// <summary>JPEG quality, 1 to 100, for photos this processor has to encode again: 85.</summary>
    public int JpegQuality { get; init; } = 85;

    /// <summary>Limits on the images read, before anything is allocated. Zorg.Pdf's own limits apply too.</summary>
    public ImageLimits Limits { get; init; } = ImageLimits.Default;

    /// <inheritdoc/>
    /// <remarks>The work is synchronous, so this processor also serves synchronous renders.</remarks>
    public ValueTask<PdfImage?> ProcessAsync(ImageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ValueTask.FromResult(Process(request));
        }
        catch (ImageLimitException ex)
        {
            throw new PdfLimitException(ex.Message, ex);
        }
        catch (ImageException ex)
        {
            throw new PdfException(ex.Message, ex);
        }
    }

    private PdfImage? Process(ImageRequest request)
    {
        ReadOnlyMemory<byte> bytes = request.Bytes; // Read in place: no copy.
        IdentifyResult identified = ZorgImage.Identify(bytes, new IdentifyOptions { Limits = Limits });
        if (!identified.Succeeded || identified.Info is not { } info)
        {
            throw new PdfException(identified.Message ?? "The image's format isn't recognised.");
        }

        // The pixels it needs at the largest size it's drawn, for the oriented image.
        int targetWidth = Pixels(request.MaxWidth), targetHeight = Pixels(request.MaxHeight);
        bool tooLarge = info.DisplayWidth > targetWidth || info.DisplayHeight > targetHeight;
        bool turned = info.Orientation > 1;
        bool plainColour = !info.HasIccProfile && info.ColorSpace is null or { IsSrgb: true };
        bool readByCore = info.Format == ImageFormat.Jpeg || info.Format == ImageFormat.Png;
        if (readByCore && !tooLarge && !turned && plainColour)
        {
            return null; // Zorg.Pdf embeds it as it is.
        }

        // A JPEG larger than it's drawn decodes at 1/2, 1/4 or 1/8 size straight away (still at least twice the target).
        // Grey, RGB or RGBA go into a PDF image directly; anything else (16-bit, BGRA) is converted to RGBA.
        ZorgImage native = ZorgImage.Decode(bytes, new DecodeOptions { Limits = Limits, ApplyOrientation = true, ShrinkTo = tooLarge ? (targetWidth, targetHeight) : null });
        using ZorgImage decoded = native.PixelFormat is PixelFormat.L8 or PixelFormat.Rgb24 or PixelFormat.Rgba32
            ? native
            : Replace(native, native.Convert(PixelFormat.Rgba32));
        ZorgImage image = decoded;
        try
        {
            if (!image.ColorSpace.IsSrgb)
            {
                image = image.ToSrgb();
            }

            if (image.Width > targetWidth || image.Height > targetHeight)
            {
                ZorgImage fitted = image.Fit(targetWidth, targetHeight);
                if (!ReferenceEquals(image, decoded))
                {
                    image.Dispose();
                }

                image = fitted;
            }

            // An alpha channel every pixel leaves opaque (common in exported PNG and WebP) needs no soft mask.
            bool transparent = image.PixelFormat == PixelFormat.Rgba32 && !image.IsOpaque();
            PdfImage result = info.Format == ImageFormat.Jpeg && !transparent ? Jpeg(image) : Pixels(image, transparent);

            // Drawn at the size of the original, the right way up, when the block doesn't set one.
            return result with { NaturalWidth = info.DisplayWidth, NaturalHeight = info.DisplayHeight };
        }
        finally
        {
            if (!ReferenceEquals(image, decoded))
            {
                image.Dispose();
            }
        }
    }

    private static ZorgImage Replace(ZorgImage old, ZorgImage replacement)
    {
        old.Dispose();
        return replacement;
    }

    private int Pixels(Length length) => Math.Max(1, (int)Math.Ceiling(length.Points / PointsPerInch * Resolution));

    /// <summary>A photo that had to be decoded: encoded as JPEG again, which keeps it small.</summary>
    private PdfImage Jpeg(ZorgImage image)
    {
        using var output = new MemoryStream();
        image.SaveAsJpeg(output, new JpegEncoderOptions { Quality = JpegQuality });
        return PdfImage.Jpeg(output.ToArray());
    }

    /// <summary>Anything else: lossless RGB pixels, with alpha when the image has it.</summary>
    private static PdfImage Pixels(ZorgImage image, bool hasAlpha)
    {
        int width = image.Width, height = image.Height;
        if (image.PixelFormat == PixelFormat.L8)
        {
            byte[] grey = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                image.GetRowBytes(y)[..width].CopyTo(grey.AsSpan(y * width));
            }

            return PdfImage.FromPixels(width, height, PdfPixelFormat.Gray8, grey);
        }

        int channels = image.PixelFormat switch
        {
            PixelFormat.Rgb24 => 3,
            PixelFormat.Rgba32 => 4,
            _ => throw new PdfException($"Zorg.Imaging gave {image.PixelFormat} pixels where RGB was asked for."),
        };
        byte[] rgb = new byte[width * height * 3];
        byte[]? alpha = hasAlpha && channels == 4 ? new byte[width * height] : null;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = image.GetRowBytes(y);
            for (int x = 0; x < width; x++)
            {
                int from = x * channels, to = ((y * width) + x) * 3;
                (rgb[to], rgb[to + 1], rgb[to + 2]) = (row[from], row[from + 1], row[from + 2]);
                if (alpha is not null)
                {
                    alpha[(y * width) + x] = row[from + 3];
                }
            }
        }

        return PdfImage.FromPixels(width, height, PdfPixelFormat.Rgb24, rgb, alpha);
    }
}
