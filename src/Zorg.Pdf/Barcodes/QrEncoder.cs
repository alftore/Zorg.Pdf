using System;
using System.Collections.Generic;
using System.Text;

namespace Zorg.Pdf.Barcodes;

/// <summary>
/// QR Code (ISO/IEC 18004) encoding: one segment in numeric, alphanumeric or byte (UTF-8) mode, the smallest version
/// from 1 to 40 that holds it at the error-correction level asked for, Reed-Solomon codewords interleaved over the
/// blocks, and the mask with the lowest penalty. The result is the module grid, without the quiet zone.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1814:Prefer jagged arrays over multidimensional", Justification = "A QR symbol is a square grid of modules.")]
internal static class QrEncoder
{
    private const string Alphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    // Error-correction codewords per block and number of blocks, by level (L, M, Q, H) and version (index 1 to 40).
    private static readonly int[][] EccPerBlock =
    [
        [-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
        [-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
    ];

    private static readonly int[][] Blocks =
    [
        [-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
        [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
        [-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
        [-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81],
    ];

    /// <summary>The module grid for the text: true is dark. Throws when the text doesn't fit version 40 at this level.</summary>
    public static bool[,] Encode(string text, QrErrorCorrection level)
    {
        int ecl = (int)level;
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        int mode = IsNumeric(text) ? 1 : IsAlphanumeric(text) ? 2 : 4;
        int count = mode == 4 ? utf8.Length : text.Length;
        int version = 1;
        int dataBits;
        while (true)
        {
            dataBits = DataCodewords(version, ecl) * 8;
            int needed = 4 + CountBits(mode, version) + SegmentBits(mode, count);
            if (needed <= dataBits)
            {
                break;
            }

            if (++version > 40)
            {
                throw new ArgumentException($"The text is too long for a QR code at error correction {level}: {utf8.Length:N0} bytes.", nameof(text));
            }
        }

        var bits = new BitBuffer();
        bits.Append(mode, 4);
        bits.Append(count, CountBits(mode, version));
        switch (mode)
        {
            case 1:
                for (int i = 0; i < text.Length; i += 3)
                {
                    int n = Math.Min(3, text.Length - i);
                    bits.Append(int.Parse(text.AsSpan(i, n), System.Globalization.CultureInfo.InvariantCulture), (n * 3) + 1);
                }

                break;
            case 2:
                for (int i = 0; i < text.Length; i += 2)
                {
                    if (i + 1 < text.Length)
                    {
                        bits.Append((Alphanumeric.IndexOf(text[i], StringComparison.Ordinal) * 45) + Alphanumeric.IndexOf(text[i + 1], StringComparison.Ordinal), 11);
                    }
                    else
                    {
                        bits.Append(Alphanumeric.IndexOf(text[i], StringComparison.Ordinal), 6);
                    }
                }

                break;
            default:
                foreach (byte b in utf8)
                {
                    bits.Append(b, 8);
                }

                break;
        }

        // Terminator, byte boundary, then alternating pad bytes.
        bits.Append(0, Math.Min(4, dataBits - bits.Count));
        bits.Append(0, (8 - (bits.Count % 8)) % 8);
        for (int pad = 0xEC; bits.Count < dataBits; pad ^= 0xEC ^ 0x11)
        {
            bits.Append(pad, 8);
        }

        byte[] codewords = Interleave(bits.ToBytes(), version, ecl);
        return Draw(codewords, version, ecl);
    }

    private static bool IsNumeric(string text) => text.Length > 0 && text.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0;

    private static bool IsAlphanumeric(string text)
    {
        foreach (char c in text)
        {
            if (Alphanumeric.IndexOf(c, StringComparison.Ordinal) < 0)
            {
                return false;
            }
        }

        return text.Length > 0;
    }

    private static int CountBits(int mode, int version) => mode switch
    {
        1 => version < 10 ? 10 : version < 27 ? 12 : 14,
        2 => version < 10 ? 9 : version < 27 ? 11 : 13,
        _ => version < 10 ? 8 : 16,
    };

    private static int SegmentBits(int mode, int count) => mode switch
    {
        1 => (count / 3 * 10) + (count % 3 == 0 ? 0 : (count % 3 * 3) + 1),
        2 => (count / 2 * 11) + (count % 2 * 6),
        _ => count * 8,
    };

    /// <summary>The modules left for data and error correction once the function patterns are placed.</summary>
    internal static int RawDataModules(int version)
    {
        int result = (((16 * version) + 128) * version) + 64;
        if (version >= 2)
        {
            int alignments = (version / 7) + 2;
            result -= (((25 * alignments) - 10) * alignments) - 55;
            if (version >= 7)
            {
                result -= 36;
            }
        }

        return result;
    }

    internal static int DataCodewords(int version, int ecl) =>
        (RawDataModules(version) / 8) - (EccPerBlock[ecl][version] * Blocks[ecl][version]);

    /// <summary>Splits the data into blocks, adds each block's Reed-Solomon codewords, and interleaves them.</summary>
    private static byte[] Interleave(byte[] data, int version, int ecl)
    {
        int blocks = Blocks[ecl][version], eccLength = EccPerBlock[ecl][version];
        int raw = RawDataModules(version) / 8;
        int shortBlocks = blocks - (raw % blocks), shortLength = raw / blocks;
        byte[] divisor = ReedSolomonDivisor(eccLength);
        var all = new List<byte[]>(blocks);
        for (int i = 0, k = 0; i < blocks; i++)
        {
            int length = shortLength - eccLength + (i < shortBlocks ? 0 : 1);
            byte[] block = data[k..(k + length)];
            k += length;
            byte[] ecc = ReedSolomonRemainder(block, divisor);
            byte[] full = new byte[shortLength + 1];
            block.CopyTo(full, 0); // A short block's last data byte stays a placeholder, skipped below.
            ecc.CopyTo(full, shortLength + 1 - eccLength);
            all.Add(full);
        }

        var result = new List<byte>(raw);
        for (int i = 0; i < shortLength + 1; i++)
        {
            for (int j = 0; j < blocks; j++)
            {
                if (i != shortLength - eccLength || j >= shortBlocks)
                {
                    result.Add(all[j][i]);
                }
            }
        }

        return [.. result];
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        byte[] result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree)
                {
                    result[j] ^= result[j + 1];
                }
            }

            root = Multiply(root, 0x02);
        }

        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        byte[] result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++)
            {
                result[i] ^= Multiply(divisor[i], factor);
            }
        }

        return result;
    }

    /// <summary>Multiplication in GF(2^8) modulo x^8 + x^4 + x^3 + x^2 + 1.</summary>
    private static byte Multiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }

        return (byte)z;
    }

    /// <summary>Places the function patterns and the codewords, then applies the mask with the lowest penalty.</summary>
    private static bool[,] Draw(byte[] codewords, int version, int ecl)
    {
        int size = (version * 4) + 17;
        var grid = new Grid(size);
        grid.FunctionPatterns(version);
        grid.DrawFormat(ecl, 0); // Reserves the format areas; redrawn with the chosen mask.
        grid.DrawCodewords(codewords);
        int best = 0, bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            grid.ApplyMask(mask);
            grid.DrawFormat(ecl, mask);
            int penalty = grid.Penalty();
            if (penalty < bestPenalty)
            {
                (best, bestPenalty) = (mask, penalty);
            }

            grid.ApplyMask(mask); // XOR again: undone.
        }

        grid.ApplyMask(best);
        grid.DrawFormat(ecl, best);
        return grid.Modules;
    }

    private sealed class BitBuffer
    {
        private readonly List<bool> _bits = [];

        public int Count => _bits.Count;

        public void Append(int value, int length)
        {
            for (int i = length - 1; i >= 0; i--)
            {
                _bits.Add(((value >> i) & 1) != 0);
            }
        }

        public byte[] ToBytes()
        {
            byte[] result = new byte[_bits.Count / 8];
            for (int i = 0; i < result.Length * 8; i++)
            {
                if (_bits[i])
                {
                    result[i / 8] |= (byte)(0x80 >> (i % 8));
                }
            }

            return result;
        }
    }

    /// <summary>The module grid, indexed [y, x], and which modules belong to function patterns.</summary>
    private sealed class Grid(int size)
    {
        private readonly bool[,] _function = new bool[size, size];

        public bool[,] Modules { get; } = new bool[size, size];

        private void Set(int x, int y, bool dark)
        {
            Modules[y, x] = dark;
            _function[y, x] = true;
        }

        public void FunctionPatterns(int version)
        {
            for (int i = 0; i < size; i++)
            {
                Set(6, i, i % 2 == 0);
                Set(i, 6, i % 2 == 0);
            }

            Finder(3, 3);
            Finder(size - 4, 3);
            Finder(3, size - 4);
            int[] positions = AlignmentPositions(version);
            for (int i = 0; i < positions.Length; i++)
            {
                for (int j = 0; j < positions.Length; j++)
                {
                    bool corner = (i == 0 && j == 0) || (i == 0 && j == positions.Length - 1) || (i == positions.Length - 1 && j == 0);
                    if (!corner)
                    {
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                Set(positions[i] + dx, positions[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                            }
                        }
                    }
                }
            }

            if (version >= 7)
            {
                int remainder = version;
                for (int i = 0; i < 12; i++)
                {
                    remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
                }

                int bits = (version << 12) | remainder;
                for (int i = 0; i < 18; i++)
                {
                    bool dark = ((bits >> i) & 1) != 0;
                    int a = size - 11 + (i % 3), b = i / 3;
                    Set(a, b, dark);
                    Set(b, a, dark);
                }
            }
        }

        private void Finder(int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x >= 0 && x < size && y >= 0 && y < size)
                    {
                        int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        Set(x, y, distance != 2 && distance != 4);
                    }
                }
            }
        }

        private static int[] AlignmentPositions(int version)
        {
            if (version == 1)
            {
                return [];
            }

            int count = (version / 7) + 2;
            int step = version == 32 ? 26 : ((version * 4) + (count * 2) + 1) / ((count * 2) - 2) * 2;
            int[] result = new int[count];
            result[0] = 6;
            for (int i = count - 1, position = (version * 4) + 17 - 7; i >= 1; i--, position -= step)
            {
                result[i] = position;
            }

            return result;
        }

        public void DrawFormat(int ecl, int mask)
        {
            int level = ecl switch { 0 => 1, 1 => 0, 2 => 3, _ => 2 }; // L, M, Q, H as the format bits write them.
            int data = (level << 3) | mask;
            int remainder = data;
            for (int i = 0; i < 10; i++)
            {
                remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
            }

            int bits = ((data << 10) | remainder) ^ 0x5412;
            bool Bit(int i) => ((bits >> i) & 1) != 0;
            for (int i = 0; i <= 5; i++)
            {
                Set(8, i, Bit(i));
            }

            Set(8, 7, Bit(6));
            Set(8, 8, Bit(7));
            Set(7, 8, Bit(8));
            for (int i = 9; i < 15; i++)
            {
                Set(14 - i, 8, Bit(i));
            }

            for (int i = 0; i < 8; i++)
            {
                Set(size - 1 - i, 8, Bit(i));
            }

            for (int i = 8; i < 15; i++)
            {
                Set(8, size - 15 + i, Bit(i));
            }

            Set(8, size - 8, true); // The dark module.
        }

        public void DrawCodewords(byte[] codewords)
        {
            int i = 0;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6)
                {
                    right = 5;
                }

                for (int vertical = 0; vertical < size; vertical++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? size - 1 - vertical : vertical;
                        if (!_function[y, x] && i < codewords.Length * 8)
                        {
                            Modules[y, x] = ((codewords[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                            i++;
                        }
                    }
                }
            }
        }

        public void ApplyMask(int mask)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool invert = mask switch
                    {
                        0 => (x + y) % 2 == 0,
                        1 => y % 2 == 0,
                        2 => x % 3 == 0,
                        3 => (x + y) % 3 == 0,
                        4 => ((x / 3) + (y / 2)) % 2 == 0,
                        5 => ((x * y % 2) + (x * y % 3)) == 0,
                        6 => (((x * y % 2) + (x * y % 3)) % 2) == 0,
                        _ => ((((x + y) % 2) + (x * y % 3)) % 2) == 0,
                    };
                    if (invert && !_function[y, x])
                    {
                        Modules[y, x] = !Modules[y, x];
                    }
                }
            }
        }

        /// <summary>The standard's four penalty rules: runs, 2×2 blocks, finder-like patterns and dark/light balance.</summary>
        public int Penalty()
        {
            int penalty = 0, dark = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int a = 0; a < size; a++)
                {
                    int run = 0;
                    bool previous = false;
                    for (int b = 0; b < size; b++)
                    {
                        bool m = pass == 0 ? Modules[a, b] : Modules[b, a];
                        if (pass == 0 && m)
                        {
                            dark++;
                        }

                        if (b > 0 && m == previous)
                        {
                            run++;
                            if (run == 5)
                            {
                                penalty += 3;
                            }
                            else if (run > 5)
                            {
                                penalty++;
                            }
                        }
                        else
                        {
                            run = 1;
                        }

                        previous = m;

                        // 1:1:3:1:1 with four light modules before or after (outside the grid counts as light).
                        if (b >= 6 && Finderlike(pass, a, b - 6))
                        {
                            penalty += 40;
                        }
                    }
                }
            }

            for (int y = 0; y + 1 < size; y++)
            {
                for (int x = 0; x + 1 < size; x++)
                {
                    bool m = Modules[y, x];
                    if (m == Modules[y, x + 1] && m == Modules[y + 1, x] && m == Modules[y + 1, x + 1])
                    {
                        penalty += 3;
                    }
                }
            }

            int total = size * size;
            int k = (int)((Math.Abs((dark * 20L) - (total * 10L)) + total - 1) / total) - 1;
            return penalty + (Math.Max(0, k) * 10);
        }

        private bool Finderlike(int pass, int line, int start)
        {
            bool At(int i) => i >= 0 && i < size && (pass == 0 ? Modules[line, i] : Modules[i, line]);
            ReadOnlySpan<bool> pattern = [true, false, true, true, true, false, true];
            for (int i = 0; i < 7; i++)
            {
                if (At(start + i) != pattern[i])
                {
                    return false;
                }
            }

            bool lightBefore = !At(start - 1) && !At(start - 2) && !At(start - 3) && !At(start - 4);
            bool lightAfter = !At(start + 7) && !At(start + 8) && !At(start + 9) && !At(start + 10);
            return lightBefore || lightAfter;
        }
    }
}
