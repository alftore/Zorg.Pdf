using System;
using System.Buffers;
using System.Text;
using Xunit;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Tests.Writing;

public sealed class PdfFormatTests
{
    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(-0.00001, "0")]
    [InlineData(12.0, "12")]
    [InlineData(595.2755905, "595.2756")]
    [InlineData(-1.5, "-1.5")]
    [InlineData(0.12345, "0.1235")]
    [InlineData(1e14, "100000000000000")]
    public void RealsHaveAtMostFourDecimalsAndNoExponent(double value, string expected) =>
        Assert.Equal(expected, Write(b => PdfFormat.WriteReal(b, value)));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e16)]
    public void RealsRejectValuesPdfCannotHold(double value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Write(b => PdfFormat.WriteReal(b, value)));

    [Fact]
    public void RealsIgnoreTheCurrentCulture()
    {
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("nb-NO");
            Assert.Equal("1.25", Write(b => PdfFormat.WriteReal(b, 1.25)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void NamesEscapeDelimitersSpacesAndHash() =>
        Assert.Equal("/A#20B#2F#23#28", Write(b => PdfFormat.WriteName(b, "A B/#("u8)));

    [Fact]
    public void NamesEscapeNonAscii() =>
        Assert.Equal("/#C3#A6", Write(b => PdfFormat.WriteName(b, "æ"u8)));

    [Fact]
    public void LiteralStringsEscapeParenthesesBackslashAndCarriageReturn() =>
        Assert.Equal(@"(a\(b\)c\\d\re)", Write(b => PdfFormat.WriteLiteralString(b, "a(b)c\\d\re"u8)));

    [Fact]
    public void HexStringsAreUpperCase() =>
        Assert.Equal("<00ABFF>", Write(b => PdfFormat.WriteHexString(b, [0x00, 0xAB, 0xFF])));

    private static string Write(Action<ArrayBufferWriter<byte>> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        return Encoding.Latin1.GetString(buffer.WrittenSpan);
    }
}
