using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Zorg.Pdf.Images;

namespace Zorg.Pdf.Tests.Images;

/// <summary>
/// Small PNG and JPEG files made in code, so no image is committed. The PNG writer cycles through all five row
/// filters and can interlace; the JPEG writer makes a flat grey baseline JPEG with one-code Huffman tables.
/// </summary>
internal static class TestImages
{
    /// <summary>A PNG from raw samples (big-endian for 16-bit), one byte array per row, packed for the bit depth.</summary>
    public static byte[] Png(int width, int height, int colorType, int bitDepth, Func<int, int, int[]> pixel, bool interlaced = false, byte[]? palette = null, byte[]? transparency = null, Func<byte[], byte[]>? corruptData = null)
    {
        int channels = colorType switch { 0 or 3 => 1, 2 => 3, 4 => 2, _ => 4 };
        var raw = new MemoryStream();
        (int X, int Y, int Dx, int Dy)[] passes = interlaced
            ? [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]
            : [(0, 0, 1, 1)];
        int bpp = Math.Max(1, channels * bitDepth / 8);
        int filter = 0;
        foreach ((int sx, int sy, int dx, int dy) in passes)
        {
            int w = (width - sx + dx - 1) / dx, h = (height - sy + dy - 1) / dy;
            if (w <= 0 || h <= 0)
            {
                continue;
            }

            int rowBytes = ((w * channels * bitDepth) + 7) / 8;
            byte[] prior = new byte[rowBytes];
            for (int y = 0; y < h; y++)
            {
                byte[] row = new byte[rowBytes];
                for (int x = 0; x < w; x++)
                {
                    int[] samples = pixel((x * dx) + sx, (y * dy) + sy);
                    for (int c = 0; c < channels; c++)
                    {
                        Put(row, (x * channels) + c, bitDepth, samples[c]);
                    }
                }

                raw.WriteByte((byte)filter);
                raw.Write(Filter(filter, row, prior, bpp));
                prior = row;
                filter = (filter + 1) % 5;
            }
        }

        byte[] data = Deflate(raw.ToArray());
        data = corruptData?.Invoke(data) ?? data;
        var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        (header[8], header[9], header[12]) = ((byte)bitDepth, (byte)colorType, (byte)(interlaced ? 1 : 0));
        Chunk(png, "IHDR", header);
        if (palette is not null)
        {
            Chunk(png, "PLTE", palette);
        }

        if (transparency is not null)
        {
            Chunk(png, "tRNS", transparency);
        }

        // Two IDAT chunks, as encoders split long data.
        Chunk(png, "IDAT", data.AsSpan(0, data.Length / 2).ToArray());
        Chunk(png, "IDAT", data.AsSpan(data.Length / 2).ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>A flat grey baseline JPEG of the given size and component count (1 grey, 3 colour, 4 CMYK with an Adobe marker).</summary>
    public static byte[] Jpeg(int width, int height, int components = 3, int precision = 8, byte frameMarker = 0xC0)
    {
        var jpeg = new List<byte> { 0xFF, 0xD8 };
        if (components == 4)
        {
            Segment(jpeg, 0xEE, [.. "Adobe"u8.ToArray(), 0, 100, 0, 0, 0, 0, 0]); // Transform 0: CMYK, stored inverted.
        }

        byte[] quantisation = new byte[65]; // Table 0, 8-bit, all ones.
        Array.Fill(quantisation, (byte)1, 1, 64);
        Segment(jpeg, 0xDB, quantisation);
        var frame = new List<byte> { (byte)precision, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components };
        for (int c = 0; c < components; c++)
        {
            frame.AddRange([(byte)(c + 1), 0x11, 0]);
        }

        Segment(jpeg, frameMarker, [.. frame]);

        // One code of length 1 in each table: DC category 0 (no difference), AC end of block.
        Segment(jpeg, 0xC4, [0x00, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x00]);
        Segment(jpeg, 0xC4, [0x10, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x00]);
        var scan = new List<byte> { (byte)components };
        for (int c = 0; c < components; c++)
        {
            scan.AddRange([(byte)(c + 1), 0x00]);
        }

        scan.AddRange([0, 63, 0]);
        Segment(jpeg, 0xDA, [.. scan]);

        // Two zero bits per block and component, padded with ones.
        int bits = ((width + 7) / 8) * ((height + 7) / 8) * components * 2;
        int bytes = (bits + 7) / 8;
        for (int i = 0; i < bytes; i++)
        {
            int used = Math.Min(8, bits - (i * 8));
            jpeg.Add((byte)((1 << (8 - used)) - 1));
        }

        jpeg.AddRange([0xFF, 0xD9]);
        return [.. jpeg];
    }

    /// <summary>Inflates Flate data.</summary>
    public static byte[] Inflate(ReadOnlyMemory<byte> data)
    {
        using var zlib = new ZLibStream(new MemoryStream(data.ToArray()), CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static void Segment(List<byte> jpeg, byte marker, byte[] body)
    {
        jpeg.AddRange([0xFF, marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2)]);
        jpeg.AddRange(body);
    }

    private static void Put(byte[] row, int index, int depth, int value)
    {
        switch (depth)
        {
            case 16:
                row[index * 2] = (byte)(value >> 8);
                row[(index * 2) + 1] = (byte)value;
                break;
            case 8:
                row[index] = (byte)value;
                break;
            default:
                row[index * depth / 8] |= (byte)(value << (8 - depth - (index * depth % 8)));
                break;
        }
    }

    private static byte[] Filter(int filter, byte[] row, byte[] prior, int bpp)
    {
        byte[] output = new byte[row.Length];
        for (int i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0, b = prior[i], c = i >= bpp ? prior[i - bpp] : 0;
            int predicted = filter switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) >> 1,
                _ => Paeth(a, b, c),
            };
            output[i] = (byte)(row[i] - predicted);
        }

        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream png, string type, byte[] body)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        png.Write(length);
        byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. body];
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(length, Crc32.Compute(typed));
        png.Write(length);
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }
}
