using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Zorg.Pdf.Barcodes;

/// <summary>A linear barcode: bar and space widths in modules, starting with a bar, and the text to print under it.</summary>
/// <param name="Widths">Alternating bar and space widths, in modules, from the left; the first is a bar.</param>
/// <param name="Text">The human-readable text.</param>
/// <param name="QuietLeft">Modules of white needed to the left.</param>
/// <param name="QuietRight">Modules of white needed to the right.</param>
internal sealed record LinearCode(byte[] Widths, string Text, int QuietLeft, int QuietRight)
{
    public int Modules
    {
        get
        {
            int sum = 0;
            foreach (byte w in Widths)
            {
                sum += w;
            }

            return sum;
        }
    }
}

/// <summary>
/// Code 128 (ISO/IEC 15417): character sets A, B and C chosen as the text goes (C for runs of digits), with the
/// mod 103 check character; and GS1-128, which starts with FNC1 and puts FNC1 after each variable-length element.
/// </summary>
internal static class Code128
{
    private const int StartA = 103, StartB = 104, StartC = 105, CodeA = 101, CodeB = 100, CodeC = 99, Fnc1 = 102, Stop = 106;

    // Bar and space widths for values 0 to 105; 106, the stop, has a seventh element.
    internal static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    /// <summary>Code 128 for printable ASCII and control characters.</summary>
    public static LinearCode Encode(string text)
    {
        foreach (char c in text)
        {
            if (c > 127)
            {
                throw new ArgumentException($"Code 128 holds ASCII only; '{c}' isn't.", nameof(text));
            }
        }

        return Build(Values([(text, false)], gs1: false), text);
    }

    /// <summary>GS1-128 from element strings written as "(01)09501101530003(17)261231(10)ABC".</summary>
    public static LinearCode EncodeGs1(string text)
    {
        List<(string Ai, string Value)> elements = Gs1.Parse(text);
        var parts = new List<(string, bool)>();
        for (int i = 0; i < elements.Count; i++)
        {
            (string ai, string value) = elements[i];
            parts.Add((ai + value, false));
            if (i < elements.Count - 1 && !Gs1.IsFixedLength(ai))
            {
                parts.Add(("", true)); // FNC1 ends a variable-length element.
            }
        }

        return Build(Values(parts, gs1: true), text);
    }

    private static LinearCode Build(List<int> values, string text)
    {
        int sum = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            sum += values[i] * i;
        }

        values.Add(sum % 103);
        values.Add(Stop);
        var widths = new List<byte>();
        foreach (int value in values)
        {
            foreach (char w in Patterns[value])
            {
                widths.Add((byte)(w - '0'));
            }
        }

        return new LinearCode([.. widths], text, 10, 10);
    }

    /// <summary>The symbol values, start character first: set C for runs of 4 or more digits (6 inside), B or A otherwise.</summary>
    private static List<int> Values(List<(string Text, bool Fnc1)> parts, bool gs1)
    {
        var values = new List<int>();
        int set = 0; // 'A', 'B' or 'C' once started.
        void Start(char wanted)
        {
            if (set == 0)
            {
                values.Add(wanted switch { 'A' => StartA, 'B' => StartB, _ => StartC });
                if (gs1)
                {
                    values.Add(Fnc1);
                }
            }
            else if (set != wanted)
            {
                values.Add(wanted switch { 'A' => CodeA, 'B' => CodeB, _ => CodeC });
            }

            set = wanted;
        }

        string all = string.Concat(parts.ConvertAll(p => p.Text));
        int position = 0;
        foreach ((string text, bool fnc1) in parts)
        {
            if (fnc1)
            {
                if (set == 0)
                {
                    Start('C');
                }

                values.Add(Fnc1);
                continue;
            }

            int i = 0;
            while (i < text.Length)
            {
                int digits = DigitRun(text, i);
                bool atStart = position + i == 0, atEnd = i + digits == text.Length && position + text.Length == all.Length;
                if (digits >= 2 && (set == 'C' || digits >= (atStart || atEnd ? 4 : 6) || (atStart && digits == text.Length && digits % 2 == 0)))
                {
                    Start('C');
                    for (int pairs = digits / 2; pairs > 0; pairs--, i += 2)
                    {
                        values.Add(((text[i] - '0') * 10) + (text[i + 1] - '0'));
                    }

                    continue;
                }

                char c = text[i];
                if (c < 32)
                {
                    Start('A');
                    values.Add(c + 64);
                }
                else
                {
                    if (set is 0 or 'C' || (set == 'A' && c >= 96))
                    {
                        Start('B');
                    }

                    values.Add(c - 32);
                }

                i++;
            }

            position += text.Length;
        }

        if (set == 0)
        {
            Start('B');
        }

        return values;
    }

    private static int DigitRun(string text, int from)
    {
        int i = from;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        return i - from;
    }
}

/// <summary>GS1 element strings: "(AI)value" pairs, with the fixed lengths the general specifications predefine.</summary>
internal static class Gs1
{
    public static List<(string Ai, string Value)> Parse(string text)
    {
        var elements = new List<(string, string)>();
        int i = 0;
        while (i < text.Length)
        {
            int close = text[i] == '(' ? text.IndexOf(')', i) : -1;
            if (close < 0)
            {
                throw new ArgumentException("GS1 data is element strings like \"(01)09501101530003(10)ABC123\".", nameof(text));
            }

            string ai = text[(i + 1)..close];
            int next = text.IndexOf('(', close);
            string value = next < 0 ? text[(close + 1)..] : text[(close + 1)..next];
            if (ai.Length is < 2 or > 4 || ai.AsSpan().IndexOfAnyExceptInRange('0', '9') >= 0 || value.Length == 0)
            {
                throw new ArgumentException($"\"({ai}){value}\" isn't a GS1 element: an application identifier of 2 to 4 digits in brackets, then its value.", nameof(text));
            }

            if (FixedLength(ai) is { } length && value.Length != length)
            {
                throw new ArgumentException($"GS1 ({ai}) takes {length} characters; \"{value}\" has {value.Length}.", nameof(text));
            }

            if (ai is "01" or "02" && !Ean13.CheckDigitValid(value))
            {
                throw new ArgumentException($"The GTIN {value} in ({ai}) has a wrong check digit.", nameof(text));
            }

            elements.Add((ai, value));
            i = next < 0 ? text.Length : next;
        }

        return elements;
    }

    public static bool IsFixedLength(string ai) => FixedLength(ai) is not null;

    /// <summary>The predefined lengths by the identifier's first two digits (GS1 General Specifications, 5.10.1).</summary>
    private static int? FixedLength(string ai) => ai[..2] switch
    {
        "00" => 18,
        "01" or "02" or "03" => 14,
        "04" => 16,
        "11" or "12" or "13" or "14" or "15" or "16" or "17" or "18" or "19" => 6,
        "20" => 2,
        "31" or "32" or "33" or "34" or "35" or "36" => 6,
        "41" => 13,
        _ => null,
    };
}

/// <summary>EAN-13 (ISO/IEC 15420): 12 digits and a check digit, the first digit set by the parity of the left half.</summary>
internal static class Ean13
{
    private static readonly string[] L = ["0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"];
    private static readonly string[] G = ["0100111", "0110011", "0011011", "0100001", "0011101", "0111001", "0000101", "0010001", "0001001", "0010111"];
    private static readonly string[] R = ["1110010", "1100110", "1101100", "1000010", "1011100", "1001110", "1010000", "1000100", "1001000", "1110100"];
    private static readonly string[] Parity = ["LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"];

    /// <summary>12 digits (the check digit is added) or 13 (it's checked).</summary>
    public static LinearCode Encode(string digits)
    {
        if (digits.Length is not (12 or 13) || digits.AsSpan().IndexOfAnyExceptInRange('0', '9') >= 0)
        {
            throw new ArgumentException("EAN-13 takes 12 digits, or 13 with the check digit.", nameof(digits));
        }

        if (digits.Length == 12)
        {
            digits += CheckDigit(digits).ToString(CultureInfo.InvariantCulture);
        }
        else if (!CheckDigitValid(digits))
        {
            throw new ArgumentException($"The EAN-13 {digits} has a wrong check digit: it should be {CheckDigit(digits[..12])}.", nameof(digits));
        }

        var modules = new StringBuilder("101");
        string parity = Parity[digits[0] - '0'];
        for (int i = 1; i <= 6; i++)
        {
            modules.Append(parity[i - 1] == 'L' ? L[digits[i] - '0'] : G[digits[i] - '0']);
        }

        modules.Append("01010");
        for (int i = 7; i <= 12; i++)
        {
            modules.Append(R[digits[i] - '0']);
        }

        modules.Append("101");
        return new LinearCode(RunLengths(modules.ToString()), digits, 11, 7);
    }

    /// <summary>The GTIN check digit: weights 3 and 1 from the right of the digits before it.</summary>
    public static int CheckDigit(string digits)
    {
        int sum = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            int weight = (digits.Length - i) % 2 == 1 ? 3 : 1;
            sum += (digits[i] - '0') * weight;
        }

        return (10 - (sum % 10)) % 10;
    }

    public static bool CheckDigitValid(string digits) =>
        digits.Length >= 2 && digits.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0 && CheckDigit(digits[..^1]) == digits[^1] - '0';

    /// <summary>"1011001" as widths 1, 1, 2, 2, 1 starting with a bar.</summary>
    private static byte[] RunLengths(string modules)
    {
        var widths = new List<byte>();
        int run = 1;
        for (int i = 1; i <= modules.Length; i++)
        {
            if (i < modules.Length && modules[i] == modules[i - 1])
            {
                run++;
            }
            else
            {
                widths.Add((byte)run);
                run = 1;
            }
        }

        return [.. widths];
    }
}
