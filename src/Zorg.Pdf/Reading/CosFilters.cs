using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Zorg.Pdf.Reading;

/// <summary>
/// Undoes the general-purpose stream filters: Flate and LZW (with TIFF and PNG predictors), ASCIIHex, ASCII85 and
/// RunLength. Image codecs (DCT, JPX, CCITT, JBIG2) aren't undone: their data is copied as it is when needed. Output is
/// bounded by a byte limit, so a decompression bomb stops at the limit instead of filling memory.
/// </summary>
internal static class CosFilters
{
    /// <summary>The filter names a stream lists, in order.</summary>
    public static List<string> Names(CosValue? filter, Func<CosValue?, CosValue> resolve) => resolve(filter) switch
    {
        CosName name => [name.Value],
        CosArray array => array.Items.ConvertAll(i => resolve(i) is CosName n ? n.Value : throw PdfReadException.Malformed("A stream's filter isn't a name.")),
        _ => [],
    };

    /// <summary>Undoes a stream's filters; <paramref name="damaged"/> is set when data was cut short by damage.</summary>
    public static byte[] Decode(CosStream stream, Func<CosValue?, CosValue> resolve, long limit, out bool damaged)
    {
        damaged = false;
        List<string> names = Names(stream.Dictionary["Filter"] ?? stream.Dictionary["F"], resolve);
        CosValue parameters = resolve(stream.Dictionary["DecodeParms"] ?? stream.Dictionary["DP"]);
        byte[] data = stream.Raw.ToArray();
        for (int i = 0; i < names.Count; i++)
        {
            CosDictionary? parms = parameters switch
            {
                CosDictionary d when i == 0 || names.Count == 1 => d,
                CosArray a when i < a.Items.Count => resolve(a.Items[i]) as CosDictionary,
                _ => null,
            };
            data = names[i] switch
            {
                "FlateDecode" or "Fl" => Predict(Inflate(data, limit, ref damaged), parms, resolve),
                "LZWDecode" or "LZW" => Predict(Lzw(data, limit, EarlyChange(parms, resolve), ref damaged), parms, resolve),
                "ASCIIHexDecode" or "AHx" => AsciiHex(data),
                "ASCII85Decode" or "A85" => Ascii85(data),
                "RunLengthDecode" or "RL" => RunLength(data, limit),
                "Crypt" => throw new PdfReadException("The stream is encrypted with a crypt filter, which isn't supported."),
                _ => throw new PdfReadException($"The {names[i]} filter can't be undone here; data using it is copied as it is."),
            };

            if (data.LongLength > limit)
            {
                throw new PdfReadException($"A stream decodes to more than {limit:N0} bytes (PdfReadLimits.MaxStreamBytes).");
            }
        }

        return data;
    }

    private static int EarlyChange(CosDictionary? parms, Func<CosValue?, CosValue> resolve) =>
        parms is not null && resolve(parms["EarlyChange"]).AsInteger is { } e ? (int)Math.Clamp(e, 0, 1) : 1;

    private static byte[] Inflate(byte[] raw, long limit, ref bool damaged)
    {
        // Most writers wrap deflate in zlib; a few write raw deflate. Try zlib, then raw.
        if (raw.Length >= 2 && (raw[0] & 0x0F) == 8 && ((raw[0] << 8) | raw[1]) % 31 == 0)
        {
            return Inflate(new ZLibStream(new MemoryStream(raw, writable: false), CompressionMode.Decompress), limit, ref damaged);
        }

        return Inflate(new DeflateStream(new MemoryStream(raw, writable: false), CompressionMode.Decompress), limit, ref damaged);
    }

    private static byte[] Inflate(Stream decompressor, long limit, ref bool damaged)
    {
        using Stream input = decompressor;
        using var output = new MemoryStream();
        byte[] buffer = new byte[65536];
        try
        {
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > limit)
                {
                    throw new PdfReadException($"A stream decodes to more than {limit:N0} bytes (PdfReadLimits.MaxStreamBytes).");
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException && output.Length > 0)
        {
            damaged = true; // What came out before the damage is kept, as viewers keep it.
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw PdfReadException.Malformed($"A Flate stream is damaged: {ex.Message}");
        }

        return output.ToArray();
    }

    private static byte[] Lzw(byte[] data, long limit, int earlyChange, ref bool damaged)
    {
        var output = new List<byte>(data.Length * 2);
        var table = new List<byte[]>(4096);
        void Reset()
        {
            table.Clear();
            for (int i = 0; i < 256; i++)
            {
                table.Add([(byte)i]);
            }

            table.Add([]); // 256: clear
            table.Add([]); // 257: end
        }

        Reset();
        int bits = 9, bitBuffer = 0, bitCount = 0;
        byte[]? previous = null;
        foreach (byte b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;
            while (bitCount >= bits)
            {
                int code = (bitBuffer >> (bitCount - bits)) & ((1 << bits) - 1);
                bitCount -= bits;
                bitBuffer &= (1 << bitCount) - 1;
                if (code == 256)
                {
                    Reset();
                    bits = 9;
                    previous = null;
                    continue;
                }

                if (code == 257)
                {
                    return [.. output];
                }

                byte[] entry;
                if (code < table.Count)
                {
                    entry = table[code];
                }
                else if (code == table.Count && previous is not null)
                {
                    entry = [.. previous, previous[0]];
                }
                else
                {
                    if (output.Count > 0)
                    {
                        damaged = true;
                        return [.. output];
                    }

                    throw PdfReadException.Malformed("An LZW stream is damaged.");
                }

                output.AddRange(entry);
                if (output.Count > limit)
                {
                    throw new PdfReadException($"A stream decodes to more than {limit:N0} bytes (PdfReadLimits.MaxStreamBytes).");
                }

                if (previous is not null && table.Count < 4096)
                {
                    table.Add([.. previous, entry[0]]);
                }

                previous = entry;
                int next = table.Count + earlyChange;
                bits = next >= 2048 ? 12 : next >= 1024 ? 11 : next >= 512 ? 10 : 9;
            }
        }

        return [.. output];
    }

    private static byte[] AsciiHex(byte[] data)
    {
        var output = new List<byte>(data.Length / 2);
        int high = -1;
        foreach (byte c in data)
        {
            if (c == '>')
            {
                break;
            }

            if (CosLexer.IsSpace(c))
            {
                continue;
            }

            int digit = CosLexer.HexDigit(c);
            if (digit < 0)
            {
                throw PdfReadException.Malformed("ASCIIHex data has a character that isn't hex.");
            }

            if (high < 0)
            {
                high = digit;
            }
            else
            {
                output.Add((byte)((high << 4) | digit));
                high = -1;
            }
        }

        if (high >= 0)
        {
            output.Add((byte)(high << 4));
        }

        return [.. output];
    }

    private static byte[] Ascii85(byte[] data)
    {
        var output = new List<byte>(data.Length * 4 / 5);
        Span<int> group = stackalloc int[5];
        int count = 0;
        for (int i = 0; i < data.Length; i++)
        {
            byte c = data[i];
            if (CosLexer.IsSpace(c))
            {
                continue;
            }

            if (c == '~')
            {
                break;
            }

            if (c == 'z' && count == 0)
            {
                output.AddRange([0, 0, 0, 0]);
                continue;
            }

            if (c is < (byte)'!' or > (byte)'u')
            {
                throw PdfReadException.Malformed("ASCII85 data has a character outside its alphabet.");
            }

            group[count++] = c - '!';
            if (count == 5)
            {
                Emit(group, 4, output);
                count = 0;
            }
        }

        if (count == 1)
        {
            throw PdfReadException.Malformed("ASCII85 data ends with a single character.");
        }

        if (count > 1)
        {
            for (int i = count; i < 5; i++)
            {
                group[i] = 84;
            }

            Emit(group, count - 1, output);
        }

        return [.. output];

        static void Emit(Span<int> group, int bytes, List<byte> output)
        {
            long value = 0;
            foreach (int g in group)
            {
                value = (value * 85) + g;
            }

            if (value > uint.MaxValue)
            {
                throw PdfReadException.Malformed("ASCII85 data has a group out of range.");
            }

            for (int i = 0; i < bytes; i++)
            {
                output.Add((byte)(value >> (24 - (8 * i))));
            }
        }
    }

    private static byte[] RunLength(byte[] data, long limit)
    {
        var output = new List<byte>(data.Length * 2);
        int i = 0;
        while (i < data.Length)
        {
            int length = data[i++];
            if (length == 128)
            {
                break;
            }

            if (length < 128)
            {
                int n = Math.Min(length + 1, data.Length - i);
                output.AddRange(data.AsSpan(i, n));
                i += n;
            }
            else if (i < data.Length)
            {
                for (int k = 0; k < 257 - length; k++)
                {
                    output.Add(data[i]);
                }

                i++;
            }

            if (output.Count > limit)
            {
                throw new PdfReadException($"A stream decodes to more than {limit:N0} bytes (PdfReadLimits.MaxStreamBytes).");
            }
        }

        return [.. output];
    }

    /// <summary>Undoes a TIFF (2) or PNG (10–15) predictor.</summary>
    private static byte[] Predict(byte[] data, CosDictionary? parms, Func<CosValue?, CosValue> resolve)
    {
        long predictor = parms is null ? 1 : resolve(parms["Predictor"]).AsInteger ?? 1;
        if (predictor < 2)
        {
            return data;
        }

        long colors = resolve(parms!["Colors"]).AsInteger ?? 1;
        long bits = resolve(parms["BitsPerComponent"]).AsInteger ?? 8;
        long columns = resolve(parms["Columns"]).AsInteger ?? 1;
        if (colors is < 1 or > 32 || bits is not (1 or 2 or 4 or 8 or 16) || columns is < 1 or > 1 << 24)
        {
            throw PdfReadException.Malformed("A predictor's parameters are out of range.");
        }

        int bpp = (int)Math.Max(1, colors * bits / 8);
        int stride = (int)(((columns * colors * bits) + 7) / 8);
        return predictor == 2 ? Tiff(data, stride, bpp, bits) : Png(data, stride, bpp);
    }

    private static byte[] Tiff(byte[] data, int stride, int bpp, long bits)
    {
        if (bits != 8)
        {
            return data; // Rare; left as stored.
        }

        byte[] output = (byte[])data.Clone();
        for (int row = 0; row + stride <= output.Length; row += stride)
        {
            for (int i = bpp; i < stride; i++)
            {
                output[row + i] = (byte)(output[row + i] + output[row + i - bpp]);
            }
        }

        return output;
    }

    private static byte[] Png(byte[] data, int stride, int bpp)
    {
        int rows = data.Length / (stride + 1); // A short last row is dropped, as viewers do.
        byte[] output = new byte[rows * stride];
        byte[] zero = new byte[stride];
        for (int row = 0; row < rows; row++)
        {
            int type = data[row * (stride + 1)];
            ReadOnlySpan<byte> input = data.AsSpan((row * (stride + 1)) + 1, stride);
            Span<byte> current = output.AsSpan(row * stride, stride);
            ReadOnlySpan<byte> above = row > 0 ? output.AsSpan((row - 1) * stride, stride) : zero;
            for (int i = 0; i < stride; i++)
            {
                int left = i >= bpp ? current[i - bpp] : 0;
                int up = above[i];
                int upLeft = i >= bpp ? above[i - bpp] : 0;
                current[i] = (byte)(input[i] + type switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => throw PdfReadException.Malformed("A row has an unknown PNG filter."),
                });
            }
        }

        return output;

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }
}
