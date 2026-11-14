using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Imaging;
using Zorg.Pdf.Tests.Rendering;
using ZorgImage = Zorg.Imaging.Image;

namespace Zorg.Pdf.Tests.Images;

/// <summary>Zorg.Pdf.Imaging: formats beyond JPEG and PNG, orientation, shrinking, and what it leaves alone.</summary>
public sealed class ImagingProcessorTests
{
    [Fact]
    public async Task JpegAndPngThatNeedNothingAreLeftToZorgPdf()
    {
        var processor = new ImagingProcessor();
        Assert.Null(await processor.ProcessAsync(Request(TestImages.Jpeg(40, 20), 300, 300), TestContext.Current.CancellationToken));
        Assert.Null(await processor.ProcessAsync(Request(TestImages.Png(40, 20, 6, 8, (x, y) => [x, y, 0, 128]), 300, 300), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWebpWithAlphaBecomesPixelsAndASoftMask()
    {
        byte[] webp = Encode(80, 40, (x, y) => [(byte)(x * 3), (byte)(y * 6), 90, (byte)(x < 40 ? 255 : 60)], image => image.SaveAsWebp);
        (byte[] file, RenderResult result) = await RenderAsync(webp, width: null);

        Assert.False(result.HasWarnings);
        string pdf = PdfInspect.Text(file);
        Assert.Matches(@"/Width 80 /Height 40 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /SMask \d+ 0 R", pdf);
        Assert.Matches(@"(?m)^60 0 0 30 ", PdfInspect.PageContents(file)[0]); // 80 × 40 px at 96 dpi.
    }

    [Fact]
    public async Task AnAlphaChannelNothingUsesGetsNoSoftMask()
    {
        byte[] webp = Encode(80, 40, (x, y) => [(byte)(x * 3), (byte)(y * 6), 90, 255], image => image.SaveAsWebp);
        (byte[] file, _) = await RenderAsync(webp, width: null);

        string pdf = PdfInspect.Text(file);
        Assert.Contains("/Width 80 /Height 40 /ColorSpace /DeviceRGB", pdf, StringComparison.Ordinal);
        Assert.DoesNotContain("/SMask", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CmykPhotosBecomeSrgbUnlessTheirInksAreKept()
    {
        byte[] cmyk = TestImages.Jpeg(400, 200, components: 4);
        var keep = new ImagingProcessor { KeepCmyk = true, Resolution = 72 };

        PdfImage? converted = await new ImagingProcessor().ProcessAsync(Request(cmyk, 400, 200), TestContext.Current.CancellationToken);
        PdfImage? untouched = await keep.ProcessAsync(Request(cmyk, 400, 200), TestContext.Current.CancellationToken);
        PdfImage? shrunk = await keep.ProcessAsync(Request(cmyk, 100, 50), TestContext.Current.CancellationToken);

        Assert.NotNull(converted); // Decoded to sRGB, as the screen wants (see the test above).
        Assert.Null(untouched); // Left to Zorg.Pdf, which embeds a CMYK JPEG as DeviceCMYK.
        (byte[] file, _) = await RenderAsync(cmyk, width: Length.Pt(100), keep);
        Assert.Matches(@"/Width 100 /Height 50 /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Filter /FlateDecode", PdfInspect.Text(file));
        Assert.NotNull(shrunk);
    }

    [Fact]
    public async Task ACmykJpegWithoutAProfileBecomesSrgbToo()
    {
        // Zorg.Pdf alone embeds it as DeviceCMYK, which PDF/A with an sRGB output intent rejects; the processor converts it.
        byte[] cmyk = TestImages.Jpeg(40, 20, components: 4);

        (byte[] file, _) = await RenderAsync(cmyk, width: null);
        (_, RenderResult archived) = await RenderTests.RenderAsync(
            new PdfDocument("x") { Content = [new Image(ImageSource.FromBytes(cmyk)) { Alt = "A swatch" }] },
            new RenderOptions { Fonts = PdfInspect.Options().Fonts, Archive = PdfArchive.PdfA2b, ImageProcessor = new ImagingProcessor() });

        Assert.Contains("/ColorSpace /DeviceRGB", PdfInspect.Text(file), StringComparison.Ordinal);
        Assert.Empty(archived.Diagnostics);
    }

    [Fact]
    public async Task ACameraJpegIsTurnedTheRightWayUp()
    {
        byte[] sideways = WithOrientation(TestImages.Jpeg(40, 24), 6); // Stored landscape, shown portrait.
        (byte[] file, _) = await RenderAsync(sideways, width: null);

        Assert.Matches(@"/Width 24 /Height 40 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", PdfInspect.Text(file));
        Assert.Matches(@"(?m)^18 0 0 30 ", PdfInspect.PageContents(file)[0]);
    }

    [Fact]
    public async Task APhotoLargerThanItIsPrintedIsShrunkToTheResolution()
    {
        byte[] wide = TestImages.Jpeg(3000, 600, components: 1);
        (byte[] file, _) = await RenderAsync(wide, width: Length.Cm(5), new ImagingProcessor { Resolution = 150 });

        // 5 cm at 150 dpi is 296 pixels; the height keeps the aspect ratio.
        Match size = Regex.Match(PdfInspect.Text(file), @"/Subtype /Image /Width (\d+) /Height (\d+)");
        Assert.Equal((296, 59), (int.Parse(size.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(size.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Matches(@"(?m)^141\.73\d* 0 0 28\.34\d* ", PdfInspect.PageContents(file)[0]); // Still drawn 5 cm wide.
    }

    [Fact]
    public async Task UnreadableImagesAreErrorsAtTheImage()
    {
        var document = new PdfDocument("x") { Content = [new Image(ImageSource.FromBytes(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })) { Alt = "x" }] };
        var options = new RenderOptions { Fonts = PdfInspect.Options().Fonts, ImageProcessor = new ImagingProcessor() };

        PdfDocumentException error = await Assert.ThrowsAsync<PdfDocumentException>(() => document.RenderAsync(new MemoryStream(), options, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("ZPDF-I001 /content/0/source", Assert.Single(error.Diagnostics.Select(d => $"{d.Code} {d.Path}")));
    }

    private static async Task<(byte[] File, RenderResult Result)> RenderAsync(byte[] image, Length? width, ImagingProcessor? processor = null)
    {
        var document = new PdfDocument("x") { Content = [new Image(ImageSource.FromBytes(image)) { Alt = "Test image", Width = width }] };
        var options = new RenderOptions { Fonts = PdfInspect.Options().Fonts, ImageProcessor = processor ?? new ImagingProcessor(), Conformance = PdfConformance.PdfUA1 };
        return await RenderTests.RenderAsync(document, options);
    }

    private static ImageRequest Request(byte[] bytes, double maxWidth, double maxHeight) =>
        new(bytes, "/content/0", maxWidth, maxHeight, synchronous: false);

    /// <summary>An image made with Zorg.Imaging, RGBA, saved by <paramref name="save"/>.</summary>
    private static byte[] Encode(int width, int height, Func<int, int, byte[]> pixel, Func<ZorgImage, Action<Stream, Zorg.Imaging.WebpEncoderOptions?>> save)
    {
        using var image = new ZorgImage(width, height, Zorg.Imaging.PixelFormat.Rgba32);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = image.GetRowBytes(y);
            for (int x = 0; x < width; x++)
            {
                pixel(x, y).CopyTo(row[(x * 4)..]);
            }
        }

        using var output = new MemoryStream();
        save(image)(output, new Zorg.Imaging.WebpEncoderOptions { Lossless = true });
        return output.ToArray();
    }

    /// <summary>The JPEG with an EXIF APP1 segment holding only an orientation, right after SOI.</summary>
    private static byte[] WithOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] tiff =
        [
            (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, // Little-endian TIFF header, first IFD at 8.
            1, 0, // One entry.
            0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, // Orientation, SHORT, 1 value.
            0, 0, 0, 0, // No next IFD.
        ];
        byte[] body = [.. "Exif\0\0"u8.ToArray(), .. tiff];
        byte[] app1 = [0xFF, 0xE1, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];
        return [.. jpeg.AsSpan(0, 2), .. app1, .. jpeg.AsSpan(2)];
    }
}
