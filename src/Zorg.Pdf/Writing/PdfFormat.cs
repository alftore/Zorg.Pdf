using System;
using System.Buffers;
using System.Globalization;

namespace Zorg.Pdf.Writing;

/// <summary>Number and token formatting shared by the object writer and content streams. Output never depends on the culture.</summary>
internal static class PdfFormat
{
    /// <summary>The longest number either method writes: a long, or a real with 4 decimals and a sign.</summary>
    public const int MaxNumberLength = 32;

    public static void WriteInteger(IBufferWriter<byte> output, long value)
    {
        Span<byte> span = output.GetSpan(MaxNumberLength);
        if (!value.TryFormat(span, out int written, default, CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("An integer didn't fit its buffer.");
        }

        output.Advance(written);
    }

    /// <summary>
    /// Writes a real with at most 4 decimals, no exponent and no trailing zeros, so the same value always gives the
    /// same bytes. PDF readers are only required to handle about 5 significant decimals, and 4 is 1/10000 of a point.
    /// </summary>
    public static void WriteReal(IBufferWriter<byte> output, double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "PDF numbers must be finite.");
        }

        // PDF reals have no exponent, so very large values would need very long digit strings. Page geometry never gets near this.
        if (Math.Abs(value) > 1e15)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "The number is too large for a PDF real.");
        }

        double rounded = Math.Round(value, 4, MidpointRounding.AwayFromZero);
        if (rounded == 0)
        {
            rounded = 0; // Never "-0".
        }

        Span<byte> span = output.GetSpan(MaxNumberLength);
        if (!rounded.TryFormat(span, out int written, "0.####", CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("A real didn't fit its buffer.");
        }

        output.Advance(written);
    }

    public static void WriteAscii(IBufferWriter<byte> output, ReadOnlySpan<byte> ascii) => output.Write(ascii);

    /// <summary>Writes a literal string, escaping the bytes that would end it or be changed by end-of-line handling.</summary>
    public static void WriteLiteralString(IBufferWriter<byte> output, ReadOnlySpan<byte> bytes)
    {
        Span<byte> span = output.GetSpan((bytes.Length * 2) + 2);
        int at = 0;
        span[at++] = (byte)'(';
        foreach (byte b in bytes)
        {
            switch (b)
            {
                case (byte)'(':
                case (byte)')':
                case (byte)'\\':
                    span[at++] = (byte)'\\';
                    span[at++] = b;
                    break;
                case (byte)'\r':
                    span[at++] = (byte)'\\';
                    span[at++] = (byte)'r';
                    break;
                default:
                    span[at++] = b;
                    break;
            }
        }

        span[at++] = (byte)')';
        output.Advance(at);
    }

    public static void WriteHexString(IBufferWriter<byte> output, ReadOnlySpan<byte> bytes)
    {
        Span<byte> span = output.GetSpan((bytes.Length * 2) + 2);
        span[0] = (byte)'<';
        int at = 1;
        foreach (byte b in bytes)
        {
            span[at++] = HexDigit(b >> 4);
            span[at++] = HexDigit(b & 0xF);
        }

        span[at++] = (byte)'>';
        output.Advance(at);
    }

    /// <summary>Writes a name. Bytes outside the printable ASCII range, delimiters and '#' are written as #xx.</summary>
    public static void WriteName(IBufferWriter<byte> output, ReadOnlySpan<byte> name)
    {
        if (name.IsEmpty)
        {
            throw new ArgumentException("A PDF name can't be empty here.", nameof(name));
        }

        Span<byte> span = output.GetSpan((name.Length * 3) + 1);
        int at = 0;
        span[at++] = (byte)'/';
        foreach (byte b in name)
        {
            if (b is < 0x21 or > 0x7E || IsDelimiter(b) || b == (byte)'#')
            {
                span[at++] = (byte)'#';
                span[at++] = HexDigit(b >> 4);
                span[at++] = HexDigit(b & 0xF);
            }
            else
            {
                span[at++] = b;
            }
        }

        output.Advance(at);
    }

    private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
        or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static byte HexDigit(int value) => (byte)(value < 10 ? '0' + value : 'A' + value - 10);
}
