using System;
using System.Buffers.Binary;
using System.Globalization;

namespace Zorg.Pdf.Images;

/// <summary>
/// Reads a JPEG's header to embed it unchanged (<c>/DCTDecode</c>): size, components and the Adobe CMYK flag. The
/// image data isn't decoded; PDF readers do that. Only what PDF's DCT filter supports gets through: 8-bit baseline,
/// extended or progressive Huffman JPEG with 1, 3 or 4 components.
/// </summary>
internal static class JpegReader
{
    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    public static PreparedImage Read(ReadOnlyMemory<byte> bytes, PdfLimits limits)
    {
        ReadOnlySpan<byte> data = bytes.Span;
        int at = 2;
        bool adobe = false;
        (int Width, int Height, int Components)? frame = null;
        while (true)
        {
            // Markers start with 0xFF, possibly padded with more 0xFF bytes.
            if (at >= data.Length || data[at] != 0xFF)
            {
                throw new PdfException("The JPEG is damaged: a marker is missing where one should be.");
            }

            while (at < data.Length && data[at] == 0xFF)
            {
                at++;
            }

            if (at >= data.Length)
            {
                throw new PdfException("The JPEG ends before its image data.");
            }

            byte marker = data[at++];
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                continue; // Standalone markers have no length.
            }

            if (marker == 0xD9)
            {
                throw new PdfException("The JPEG ends before its image data.");
            }

            if (at + 2 > data.Length)
            {
                throw new PdfException("The JPEG ends inside a segment header.");
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
            if (length < 2 || at + length > data.Length)
            {
                throw new PdfException("The JPEG is damaged: a segment runs past the end of the file.");
            }

            ReadOnlySpan<byte> segment = data.Slice(at + 2, length - 2);
            at += length;
            switch (marker)
            {
                case 0xC0 or 0xC1 or 0xC2:
                    frame = Frame(segment, limits);
                    break;
                case 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF:
                    throw new PdfException("The JPEG is lossless, hierarchical or arithmetic-coded, which PDF can't embed. Save it as a baseline or progressive JPEG.");
                case 0xEE when segment.Length >= 12 && segment[..5].SequenceEqual("Adobe"u8):
                    adobe = true;
                    break;
                case 0xDA:
                    if (frame is not { } f)
                    {
                        throw new PdfException("The JPEG has image data before its frame header.");
                    }

                    return new PreparedImage
                    {
                        Width = f.Width,
                        Height = f.Height,
                        ColorSpace = f.Components switch { 1 => ImageColorSpace.Gray, 3 => ImageColorSpace.Rgb, _ => ImageColorSpace.Cmyk },
                        BitsPerComponent = 8,
                        Dct = true,
                        InvertedCmyk = f.Components == 4 && adobe,
                        Data = bytes,
                    };
            }
        }
    }

    private static (int Width, int Height, int Components) Frame(ReadOnlySpan<byte> segment, PdfLimits limits)
    {
        if (segment.Length < 6)
        {
            throw new PdfException("The JPEG's frame header is too short.");
        }

        int precision = segment[0];
        int height = BinaryPrimitives.ReadUInt16BigEndian(segment[1..]);
        int width = BinaryPrimitives.ReadUInt16BigEndian(segment[3..]);
        int components = segment[5];
        if (precision != 8)
        {
            throw new PdfException(string.Create(CultureInfo.InvariantCulture, $"The JPEG has {precision}-bit samples; PDF embeds only 8-bit JPEG."));
        }

        if (width == 0 || height == 0)
        {
            throw new PdfException("The JPEG has no size in its frame header.");
        }

        if (components is not (1 or 3 or 4))
        {
            throw new PdfException(string.Create(CultureInfo.InvariantCulture, $"The JPEG has {components} colour components; only 1 (grey), 3 (colour) and 4 (CMYK) can be embedded."));
        }

        ImageLimits.CheckPixels(width, height, limits);
        return (width, height, components);
    }
}

/// <summary>Checks shared by the image readers.</summary>
internal static class ImageLimits
{
    public static void CheckPixels(long width, long height, PdfLimits limits)
    {
        if (width * height > limits.MaxImagePixels)
        {
            throw new PdfLimitException(string.Create(CultureInfo.InvariantCulture, $"The image is {width:N0} × {height:N0} pixels, more than the {limits.MaxImagePixels:N0} pixels allowed (RenderOptions.Limits.MaxImagePixels)."));
        }
    }
}
