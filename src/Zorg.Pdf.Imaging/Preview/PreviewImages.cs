using System;
using Zorg.Imaging;
using Bitmap = Zorg.Imaging.Image;

namespace Zorg.Pdf.Imaging.Preview;

/// <summary>
/// Image XObjects as Zorg.Pdf writes them, decoded to RGBA pixels: JPEG through Zorg.Imaging, and Flate samples
/// in DeviceGray, DeviceRGB, DeviceCMYK or Indexed at 1 to 16 bits, with an optional grey soft mask as alpha.
/// </summary>
internal static class PreviewImages
{
    public static Bitmap Load(PreviewFile file, PdfStream stream, PdfPreviewLimits limits)
    {
        PdfDictionary d = stream.Dictionary;
        int width = file.Resolve(d["Width"]).Integer, height = file.Resolve(d["Height"]).Integer;
        if (width < 1 || height < 1 || (long)width * height > limits.MaxImagePixels)
        {
            throw new PdfPreviewException($"An image is {width} × {height} pixels, outside 1 to {limits.MaxImagePixels:N0} pixels (PdfPreviewLimits.MaxImagePixels).");
        }

        Bitmap image;
        if (file.Resolve(d["Filter"]) is PdfName { Value: "DCTDecode" })
        {
            try
            {
                image = Bitmap.Decode(stream.Raw, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
            }
            catch (Exception ex) when (ex is ImageFormatException or ImageLimitException or NotSupportedException)
            {
                throw PdfPreviewException.Malformed($"An embedded JPEG can't be decoded: {ex.Message}");
            }

            if (!image.ColorSpace.IsSrgb)
            {
                Bitmap srgb = image.ToSrgb();
                image.Dispose();
                image = srgb;
            }
        }
        else
        {
            image = Samples(file, stream, width, height);
        }

        if (file.Stream(d["SMask"]) is { } mask)
        {
            ApplyMask(file, mask, image);
        }

        return image;
    }

    private static Bitmap Samples(PreviewFile file, PdfStream stream, int width, int height)
    {
        PdfDictionary d = stream.Dictionary;
        int bits = file.Resolve(d["BitsPerComponent"]).Integer;
        if (bits is not (1 or 2 or 4 or 8 or 16))
        {
            throw PdfPreviewException.Malformed($"An image has {bits} bits per component.");
        }

        PdfValue space = file.Resolve(d["ColorSpace"]);
        (int components, byte[]? palette) = space switch
        {
            PdfName { Value: "DeviceGray" } => (1, null),
            PdfName { Value: "DeviceRGB" } => (3, null),
            PdfName { Value: "DeviceCMYK" } => (4, null),
            PdfArray { Items.Count: 4 } a when file.Resolve(a.Items[0]) is PdfName { Value: "Indexed" } && file.Resolve(a.Items[3]) is PdfString lookup => (1, lookup.Value),
            _ => throw new PdfPreviewException("An image uses a colour space Zorg.Pdf never writes."),
        };

        byte[] data = file.Decode(stream);
        int stride = ((width * components * bits) + 7) / 8;
        if ((long)stride * height > data.Length)
        {
            throw PdfPreviewException.Malformed("An image has fewer bytes than its size needs.");
        }

        bool invert = file.Resolve(d["Decode"]) is PdfArray { Items.Count: > 0 } decode && file.Resolve(decode.Items[0]).Number == 1;
        int max = (1 << bits) - 1;
        var image = new Bitmap(width, height, PixelFormat.Rgba32);
        for (int y = 0; y < height; y++)
        {
            byte[] row = data.AsSpan(y * stride, stride).ToArray();
            Span<Rgba32> pixels = image.GetRow<Rgba32>(y);
            for (int x = 0; x < width; x++)
            {
                int Sample(int c) => Read(row, (x * components) + c, bits, invert);

                byte Scale(int value) => bits == 16 ? (byte)(value >> 8) : (byte)(value * 255 / max);

                if (palette is not null)
                {
                    int entry = Sample(0) * 3;
                    pixels[x] = entry + 2 < palette.Length ? new Rgba32(palette[entry], palette[entry + 1], palette[entry + 2]) : new Rgba32(0, 0, 0);
                }
                else if (components == 1)
                {
                    byte g = Scale(Sample(0));
                    pixels[x] = new Rgba32(g, g, g);
                }
                else if (components == 3)
                {
                    pixels[x] = new Rgba32(Scale(Sample(0)), Scale(Sample(1)), Scale(Sample(2)));
                }
                else
                {
                    // A plain conversion, as viewers without a colour profile draw DeviceCMYK.
                    double c = Scale(Sample(0)) / 255.0, m = Scale(Sample(1)) / 255.0, ye = Scale(Sample(2)) / 255.0, k = Scale(Sample(3)) / 255.0;
                    pixels[x] = new Rgba32((byte)Math.Round(255 * (1 - c) * (1 - k)), (byte)Math.Round(255 * (1 - m) * (1 - k)), (byte)Math.Round(255 * (1 - ye) * (1 - k)));
                }
            }
        }

        return image;
    }

    /// <summary>One sample of a row, as an integer from 0 to 2^bits − 1, inverted when the Decode array asks.</summary>
    private static int Read(byte[] row, int index, int bits, bool invert)
    {
        int max = (1 << bits) - 1;
        int value = bits switch
        {
            16 => (row[index * 2] << 8) | row[(index * 2) + 1],
            8 => row[index],
            _ => (row[index * bits / 8] >> (8 - bits - (index * bits % 8))) & max,
        };
        return invert ? max - value : value;
    }

    private static void ApplyMask(PreviewFile file, PdfStream mask, Bitmap image)
    {
        int width = file.Resolve(mask.Dictionary["Width"]).Integer, height = file.Resolve(mask.Dictionary["Height"]).Integer;
        int bits = file.Resolve(mask.Dictionary["BitsPerComponent"]).Integer;
        if (width != image.Width || height != image.Height || bits is not (8 or 16))
        {
            throw new PdfPreviewException("An image's soft mask isn't the same size at 8 or 16 bits, as Zorg.Pdf writes it.");
        }

        byte[] alpha = file.Decode(mask);
        int stride = width * bits / 8;
        if ((long)stride * height > alpha.Length)
        {
            throw PdfPreviewException.Malformed("A soft mask has fewer bytes than its size needs.");
        }

        for (int y = 0; y < height; y++)
        {
            Span<Rgba32> pixels = image.GetRow<Rgba32>(y);
            for (int x = 0; x < width; x++)
            {
                pixels[x].A = alpha[(y * stride) + (x * bits / 8)];
            }
        }
    }
}
