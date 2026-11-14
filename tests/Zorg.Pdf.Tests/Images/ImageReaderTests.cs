using System;
using System.Linq;
using Xunit;
using Zorg.Pdf.Images;

namespace Zorg.Pdf.Tests.Images;

/// <summary>The JPEG and PNG readers: what passes through, what's decoded, and what's refused.</summary>
public sealed class ImageReaderTests
{
    private static readonly PdfLimits Limits = PdfLimits.Default;

    [Theory]
    [InlineData(1, "Gray")]
    [InlineData(3, "Rgb")]
    [InlineData(4, "Cmyk")]
    public void AJpegPassesThroughWithItsSizeAndColourSpace(int components, string colorSpace)
    {
        var space = Enum.Parse<ImageColorSpace>(colorSpace);
        byte[] jpeg = TestImages.Jpeg(20, 12, components);
        PreparedImage image = JpegReader.Read(jpeg, Limits);

        Assert.Equal((20, 12, space, 8, true), (image.Width, image.Height, image.ColorSpace, image.BitsPerComponent, image.Dct));
        Assert.Equal(components == 4, image.InvertedCmyk); // Adobe CMYK is stored inverted.
        Assert.True(image.Data.Span.SequenceEqual(jpeg));
        Assert.Null(image.Mask);
    }

    [Fact]
    public void JpegsPdfCantHoldAreRefused()
    {
        Assert.Contains("12-bit", Assert.Throws<PdfException>(() => JpegReader.Read(TestImages.Jpeg(8, 8, precision: 12), Limits)).Message, StringComparison.Ordinal);
        Assert.Contains("arithmetic", Assert.Throws<PdfException>(() => JpegReader.Read(TestImages.Jpeg(8, 8, frameMarker: 0xC9), Limits)).Message, StringComparison.Ordinal);
        Assert.Throws<PdfException>(() => JpegReader.Read(TestImages.Jpeg(8, 8).AsSpan(0, 40).ToArray(), Limits));
        Assert.Throws<PdfLimitException>(() => JpegReader.Read(TestImages.Jpeg(400, 300), Limits with { MaxImagePixels = 100_000 }));
    }

    [Theory]
    [InlineData(2, 8, 3)]
    [InlineData(2, 16, 3)]
    [InlineData(0, 1, 1)]
    [InlineData(0, 8, 1)]
    public void AnOpaquePngPassesThroughWithThePngPredictor(int colorType, int depth, int colors)
    {
        int max = (1 << depth) - 1;
        byte[] png = TestImages.Png(13, 7, colorType, depth, (x, y) => [.. Enumerable.Range(0, colors).Select(c => (x * 31 + y * 17 + c * 5) % (max + 1))]);
        PreparedImage image = PngReader.Read(png, Limits);

        Assert.Equal((13, 7, depth, colors), (image.Width, image.Height, image.BitsPerComponent, image.PngPredictorColors));
        Assert.False(image.Dct);
        Assert.Null(image.Mask);
        Assert.Equal(7 * (1 + (((13 * colors * depth) + 7) / 8)), TestImages.Inflate(image.Data).Length); // The PNG's rows, filter bytes and all.
    }

    [Fact]
    public void APalettePngPassesThroughAsIndexed()
    {
        byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255];
        PreparedImage image = PngReader.Read(TestImages.Png(9, 9, 3, 2, (x, y) => [(x + y) % 3], palette: palette), Limits);

        Assert.Equal(ImageColorSpace.Indexed, image.ColorSpace);
        Assert.Equal(palette, image.Palette.ToArray());
        Assert.Equal(2, image.BitsPerComponent);
    }

    [Fact]
    public void AnRgbaPngIsDecodedWithItsAlphaAsASoftMask()
    {
        byte[] png = TestImages.Png(11, 5, 6, 8, (x, y) => [x * 20, y * 40, 7, x == 0 ? 0 : 128 + y]);
        PreparedImage image = PngReader.Read(png, Limits);

        Assert.Equal((ImageColorSpace.Rgb, 8, (int?)null), (image.ColorSpace, image.BitsPerComponent, image.PngPredictorColors));
        byte[] color = TestImages.Inflate(image.Data);
        byte[] alpha = TestImages.Inflate(image.Mask!.Data);
        Assert.Equal(11 * 5 * 3, color.Length);
        Assert.Equal([(byte)(3 * 20), (byte)(2 * 40), 7], color.AsSpan(((2 * 11) + 3) * 3, 3).ToArray());
        Assert.Equal(0, alpha[2 * 11]);
        Assert.Equal(128 + 2, alpha[(2 * 11) + 3]);
    }

    [Fact]
    public void AnRgbaPngThatIsFullyOpaqueHasNoMask()
    {
        PreparedImage image = PngReader.Read(TestImages.Png(4, 4, 6, 8, (x, y) => [x, y, 0, 255]), Limits);
        Assert.Null(image.Mask);
    }

    [Fact]
    public void AnInterlaced16BitGreyAlphaPngIsDecodedPixelForPixel()
    {
        byte[] png = TestImages.Png(13, 11, 4, 16, (x, y) => [(x * 4000) + y, y * 5000], interlaced: true);
        PreparedImage image = PngReader.Read(png, Limits);

        byte[] grey = TestImages.Inflate(image.Data);
        byte[] alpha = TestImages.Inflate(image.Mask!.Data);
        for (int y = 0; y < 11; y++)
        {
            for (int x = 0; x < 13; x++)
            {
                Assert.Equal((byte)(((x * 4000) + y) >> 8), grey[(y * 13) + x]);
                Assert.Equal((byte)((y * 5000) >> 8), alpha[(y * 13) + x]);
            }
        }
    }

    [Fact]
    public void TransparencyChunksBecomeAMask()
    {
        byte[] palette = [10, 20, 30, 40, 50, 60];
        PreparedImage indexed = PngReader.Read(TestImages.Png(4, 2, 3, 4, (x, y) => [x % 2], palette: palette, transparency: [0, 200]), Limits);
        Assert.Equal(ImageColorSpace.Rgb, indexed.ColorSpace);
        Assert.Equal([0, 200, 0, 200, 0, 200, 0, 200], TestImages.Inflate(indexed.Mask!.Data));
        Assert.Equal([10, 20, 30, 40, 50, 60], TestImages.Inflate(indexed.Data).AsSpan(0, 6).ToArray());

        PreparedImage keyed = PngReader.Read(TestImages.Png(3, 1, 0, 2, (x, y) => [x], transparency: [0, 1]), Limits);
        Assert.Equal([255, 0, 255], TestImages.Inflate(keyed.Mask!.Data));
        Assert.Equal([0, 85, 170], TestImages.Inflate(keyed.Data));
    }

    [Fact]
    public void DamagedPngsAreRefused()
    {
        byte[] good = TestImages.Png(8, 8, 2, 8, (x, y) => [x, y, 0]);
        byte[] badCrc = (byte[])good.Clone();
        badCrc[20] ^= 0xFF; // Inside IHDR.
        Assert.Contains("checksum", Message(badCrc), StringComparison.Ordinal);

        Assert.Contains("shorter", Message(TestImages.Png(8, 8, 2, 8, (x, y) => [x, y, 0], corruptData: d => Recompress(TestImages.Inflate(d).AsSpan(0, 50).ToArray()))), StringComparison.Ordinal);
        Assert.Contains("filter type 9", Message(TestImages.Png(8, 8, 0, 8, (x, y) => [x], corruptData: BadFilter)), StringComparison.Ordinal);
        Assert.Contains("palette index", Message(TestImages.Png(4, 4, 3, 8, (x, y) => [x], palette: [0, 0, 0, 255, 255, 255])), StringComparison.Ordinal);
        Assert.Contains("zlib", Message(TestImages.Png(8, 8, 0, 8, (x, y) => [x], corruptData: d => [.. d.Select(b => (byte)~b)])), StringComparison.Ordinal);
        Assert.Contains("IEND", Message(good.AsSpan(0, good.Length - 12).ToArray()), StringComparison.Ordinal);
        Assert.Throws<PdfLimitException>(() => PngReader.Read(TestImages.Png(500, 300, 0, 8, (x, y) => [0]), Limits with { MaxImagePixels = 100_000 }));

        static string Message(byte[] png) => Assert.Throws<PdfException>(() => PngReader.Read(png, Limits)).Message;
    }

    private static byte[] BadFilter(byte[] data)
    {
        byte[] raw = TestImages.Inflate(data);
        raw[0] = 9;
        return Recompress(raw);
    }

    private static byte[] Recompress(byte[] raw)
    {
        using var output = new System.IO.MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return output.ToArray();
    }
}
