using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace Zorg.Pdf.Images;

/// <summary>
/// Reads a PNG for embedding. The compressed data is always inflated and checked row by row (chunk checksums,
/// filter types, palette indices, exact length), so a damaged file is an error here and never a broken PDF. An
/// opaque, non-interlaced PNG then goes in as it came: its Flate data with PDF's PNG predictor. Anything with
/// transparency or interlacing is decoded to 8-bit grey or RGB, with the alpha channel as a soft mask.
/// </summary>
/// <remarks>
/// Memory: rows are inflated one at a time; a decoded image holds one byte per channel per pixel, bounded by
/// <see cref="PdfLimits.MaxImagePixels"/>. Gamma, colour profiles and text chunks are ignored.
/// </remarks>
internal static class PngReader
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Adam7 passes: start column, start row, column step, row step.</summary>
    private static readonly (int X, int Y, int Dx, int Dy)[] Adam7 = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];

    public static bool IsPng(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    public static PreparedImage Read(ReadOnlyMemory<byte> bytes, PdfLimits limits)
    {
        ReadOnlySpan<byte> data = bytes.Span;
        Header? header = null;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var compressed = new MemoryStream();
        bool ended = false;
        int at = Signature.Length;
        while (!ended)
        {
            if (at + 12 > data.Length)
            {
                throw new PdfException("The PNG ends before its IEND chunk.");
            }

            uint length = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            if (length > int.MaxValue || at + 12 + (long)length > data.Length)
            {
                throw new PdfException("The PNG is damaged: a chunk runs past the end of the file.");
            }

            ReadOnlySpan<byte> type = data.Slice(at + 4, 4);
            ReadOnlySpan<byte> body = data.Slice(at + 8, (int)length);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(data[(at + 8 + (int)length)..]);
            bool critical = (type[0] & 0x20) == 0;
            if ((critical || type.SequenceEqual("tRNS"u8)) && Crc32.Compute(data.Slice(at + 4, 4 + (int)length)) != crc)
            {
                throw new PdfException(string.Create(CultureInfo.InvariantCulture, $"The PNG is damaged: the {System.Text.Encoding.ASCII.GetString(type)} chunk's checksum doesn't match."));
            }

            at += 12 + (int)length;
            if (header is null && !type.SequenceEqual("IHDR"u8))
            {
                throw new PdfException("The PNG doesn't start with its IHDR chunk.");
            }

            switch (System.Text.Encoding.ASCII.GetString(type))
            {
                case "IHDR":
                    header = Header.Read(body);
                    ImageLimits.CheckPixels(header.Width, header.Height, limits);
                    break;
                case "PLTE":
                    if (body.Length == 0 || body.Length % 3 != 0 || body.Length > 768)
                    {
                        throw new PdfException("The PNG's palette has the wrong length.");
                    }

                    palette = body.ToArray();
                    break;
                case "tRNS":
                    transparency = body.ToArray();
                    break;
                case "IDAT":
                    compressed.Write(body);
                    break;
                case "IEND":
                    ended = true;
                    break;
                default:
                    if (critical)
                    {
                        throw new PdfException(string.Create(CultureInfo.InvariantCulture, $"The PNG has a critical chunk this reader doesn't know: {System.Text.Encoding.ASCII.GetString(type)}."));
                    }

                    break;
            }
        }

        Header h = header!;
        if (h.ColorType == 3 && palette is null)
        {
            throw new PdfException("The PNG uses a palette but has no PLTE chunk.");
        }

        if (transparency is not null && !TransparencyFits(h, transparency, palette))
        {
            throw new PdfException("The PNG's tRNS chunk doesn't match its colour type.");
        }

        if (compressed.Length == 0)
        {
            throw new PdfException("The PNG has no image data.");
        }

        byte[] zlib = compressed.ToArray();
        bool passThrough = !h.Interlaced && transparency is null && h.ColorType is 0 or 2 or 3;
        return passThrough ? PassThrough(h, zlib, palette) : Decode(h, zlib, palette, transparency);
    }

    /// <summary>Checks every row, then keeps the PNG's own Flate data for the PDF predictor to undo.</summary>
    private static PreparedImage PassThrough(Header h, byte[] zlib, byte[]? palette)
    {
        int colors = h.ColorType == 2 ? 3 : 1;
        int maxIndex = palette is null ? int.MaxValue : (palette.Length / 3) - 1;
        Rows(h, zlib, (row, pass, y, width) =>
        {
            if (h.ColorType == 3)
            {
                for (int x = 0; x < width; x++)
                {
                    if (Sample(row, x, h.BitDepth) > maxIndex)
                    {
                        throw new PdfException("The PNG has a pixel whose palette index is past the end of its palette.");
                    }
                }
            }
        });

        return new PreparedImage
        {
            Width = h.Width,
            Height = h.Height,
            ColorSpace = h.ColorType switch { 0 => ImageColorSpace.Gray, 2 => ImageColorSpace.Rgb, _ => ImageColorSpace.Indexed },
            BitsPerComponent = h.BitDepth,
            PngPredictorColors = colors,
            Palette = palette,
            Data = zlib,
        };
    }

    /// <summary>Decodes to 8-bit grey or RGB plus alpha, and compresses both again.</summary>
    private static PreparedImage Decode(Header h, byte[] zlib, byte[]? palette, byte[]? transparency)
    {
        int outChannels = h.ColorType is 0 or 4 ? 1 : 3;
        long pixels = (long)h.Width * h.Height;
        byte[] color = new byte[pixels * outChannels];
        byte[] alpha = new byte[pixels];
        int channels = h.Channels;
        int depth = h.BitDepth;
        int maxIndex = palette is null ? -1 : (palette.Length / 3) - 1;
        Rows(h, zlib, (row, pass, y, width) =>
        {
            (int sx, int sy, int dx, int dy) = h.Interlaced ? Adam7[pass] : (0, 0, 1, 1);
            long line = ((long)y * dy + sy) * h.Width;
            for (int x = 0; x < width; x++)
            {
                long p = line + ((long)x * dx) + sx;
                int first = Sample(row, x * channels, depth);
                byte a = 255;
                switch (h.ColorType)
                {
                    case 0:
                        color[p] = To8(first, depth);
                        if (transparency is not null && first == BinaryPrimitives.ReadUInt16BigEndian(transparency))
                        {
                            a = 0;
                        }

                        break;
                    case 2:
                        int g = Sample(row, (x * channels) + 1, depth), b = Sample(row, (x * channels) + 2, depth);
                        (color[p * 3], color[(p * 3) + 1], color[(p * 3) + 2]) = (To8(first, depth), To8(g, depth), To8(b, depth));
                        if (transparency is not null && first == BinaryPrimitives.ReadUInt16BigEndian(transparency) && g == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2)) && b == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4)))
                        {
                            a = 0;
                        }

                        break;
                    case 3:
                        if (first > maxIndex)
                        {
                            throw new PdfException("The PNG has a pixel whose palette index is past the end of its palette.");
                        }

                        (color[p * 3], color[(p * 3) + 1], color[(p * 3) + 2]) = (palette![first * 3], palette[(first * 3) + 1], palette[(first * 3) + 2]);
                        a = transparency is not null && first < transparency.Length ? transparency[first] : (byte)255;
                        break;
                    case 4:
                        color[p] = To8(first, depth);
                        a = To8(Sample(row, (x * channels) + 1, depth), depth);
                        break;
                    default:
                        (color[p * 3], color[(p * 3) + 1], color[(p * 3) + 2]) = (To8(first, depth), To8(Sample(row, (x * channels) + 1, depth), depth), To8(Sample(row, (x * channels) + 2, depth), depth));
                        a = To8(Sample(row, (x * channels) + 3, depth), depth);
                        break;
                }

                alpha[p] = a;
            }
        });

        bool opaque = alpha.AsSpan().IndexOfAnyExcept((byte)255) < 0;
        return new PreparedImage
        {
            Width = h.Width,
            Height = h.Height,
            ColorSpace = outChannels == 1 ? ImageColorSpace.Gray : ImageColorSpace.Rgb,
            BitsPerComponent = 8,
            Data = Deflate(color),
            Mask = opaque ? null : new PreparedImage { Width = h.Width, Height = h.Height, ColorSpace = ImageColorSpace.Gray, BitsPerComponent = 8, Data = Deflate(alpha) },
        };
    }

    /// <summary>Inflates and unfilters every row of every pass, handing each to <paramref name="row"/> (without its filter byte).</summary>
    private static void Rows(Header h, byte[] zlib, Action<byte[], int, int, int> row)
    {
        int bpp = Math.Max(1, h.Channels * h.BitDepth / 8);
        long widest = h.RowBytes(h.Width);
        byte[] current = new byte[widest + 1];
        byte[] previous = new byte[widest + 1];
        byte[] unfiltered = new byte[widest];
        try
        {
            using var stream = new ZLibStream(new MemoryStream(zlib, writable: false), CompressionMode.Decompress);
            int passes = h.Interlaced ? Adam7.Length : 1;
            for (int pass = 0; pass < passes; pass++)
            {
                (int sx, int sy, int dx, int dy) = h.Interlaced ? Adam7[pass] : (0, 0, 1, 1);
                int width = (h.Width - sx + dx - 1) / dx;
                int height = (h.Height - sy + dy - 1) / dy;
                if (width <= 0 || height <= 0)
                {
                    continue;
                }

                int rowBytes = (int)h.RowBytes(width);
                Array.Clear(previous);
                for (int y = 0; y < height; y++)
                {
                    stream.ReadExactly(current.AsSpan(0, rowBytes + 1));
                    Unfilter(current[0], current.AsSpan(1, rowBytes), previous.AsSpan(1, rowBytes), bpp);
                    current.AsSpan(1, rowBytes).CopyTo(unfiltered);
                    row(unfiltered, pass, y, width);
                    (current, previous) = (previous, current);
                }
            }
        }
        catch (EndOfStreamException)
        {
            throw new PdfException("The PNG's image data is shorter than its size says.");
        }
        catch (InvalidDataException)
        {
            throw new PdfException("The PNG's image data isn't valid zlib data.");
        }
    }

    private static void Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> above, int bpp)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (int i = bpp; i < row.Length; i++)
                {
                    row[i] += row[i - bpp];
                }

                break;
            case 2:
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] += above[i];
                }

                break;
            case 3:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    row[i] += (byte)((left + above[i]) >> 1);
                }

                break;
            case 4:
                for (int i = 0; i < row.Length; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0, b = above[i], c = i >= bpp ? above[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }

                break;
            default:
                throw new PdfException(string.Create(CultureInfo.InvariantCulture, $"The PNG has a row with filter type {filter}; only 0 to 4 exist."));
        }
    }

    /// <summary>The <paramref name="index"/>th sample of a row, at any bit depth.</summary>
    private static int Sample(byte[] row, int index, int depth) => depth switch
    {
        16 => (row[index * 2] << 8) | row[(index * 2) + 1],
        8 => row[index],
        _ => (row[index * depth / 8] >> (8 - depth - (index * depth % 8))) & ((1 << depth) - 1),
    };

    private static byte To8(int sample, int depth) => depth switch
    {
        16 => (byte)(sample >> 8),
        8 => (byte)sample,
        _ => (byte)(sample * 255 / ((1 << depth) - 1)),
    };

    private static bool TransparencyFits(Header h, byte[] transparency, byte[]? palette) => h.ColorType switch
    {
        0 => transparency.Length == 2,
        2 => transparency.Length == 6,
        3 => transparency.Length <= palette!.Length / 3,
        _ => false,
    };

    internal static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private sealed record Header(int Width, int Height, int BitDepth, int ColorType, bool Interlaced)
    {
        public int Channels => ColorType switch { 0 or 3 => 1, 2 => 3, 4 => 2, _ => 4 };

        public long RowBytes(int width) => (((long)width * Channels * BitDepth) + 7) / 8;

        public static Header Read(ReadOnlySpan<byte> body)
        {
            if (body.Length != 13)
            {
                throw new PdfException("The PNG's IHDR chunk has the wrong length.");
            }

            uint width = BinaryPrimitives.ReadUInt32BigEndian(body);
            uint height = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
            int depth = body[8], color = body[9];
            bool valid = color switch
            {
                0 => depth is 1 or 2 or 4 or 8 or 16,
                3 => depth is 1 or 2 or 4 or 8,
                2 or 4 or 6 => depth is 8 or 16,
                _ => false,
            };
            if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
            {
                throw new PdfException("The PNG has no size, or an impossible one.");
            }

            if (!valid || body[10] != 0 || body[11] != 0 || body[12] > 1)
            {
                throw new PdfException(string.Create(CultureInfo.InvariantCulture, $"The PNG's header is invalid (colour type {color}, bit depth {depth}, compression {body[10]}, filter {body[11]}, interlace {body[12]})."));
            }

            return new Header((int)width, (int)height, depth, color, body[12] == 1);
        }
    }
}

/// <summary>CRC-32 as PNG uses it (ISO 3309, polynomial 0xEDB88320).</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] Build()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
