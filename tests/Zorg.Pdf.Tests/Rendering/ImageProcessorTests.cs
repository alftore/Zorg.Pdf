using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Tests.Images;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>RenderOptions.ImageProcessor: what it's asked, what it may return, and how its failures are reported.</summary>
public sealed class ImageProcessorTests
{
    private static readonly byte[] Unknown = [(byte)'B', (byte)'M', 1, 2, 3, 4]; // Not a JPEG or a PNG.

    [Fact]
    public async Task AProcessorDecodesWhatCoreCantAndKeepsTheNaturalSize()
    {
        var processor = new Fake(_ => PdfImage.FromPixels(40, 30, PdfPixelFormat.Rgb24, new byte[40 * 30 * 3], Enumerable.Repeat((byte)128, 40 * 30).ToArray()) with { NaturalWidth = 400, NaturalHeight = 300 });
        var document = new PdfDocument("x") { Content = [new Image(ImageSource.FromBytes(Unknown)) { Alt = "A shrunk photo" }] };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, Options(processor));

        Assert.False(result.HasWarnings);
        string pdf = PdfInspect.Text(file);
        Assert.Matches(@"/Width 40 /Height 30 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /SMask \d+ 0 R", pdf);
        Assert.Matches(@"(?m)^300 0 0 225 ", PdfInspect.PageContents(file)[0]); // 400 × 300 at 96 dpi, not 40 × 30.
    }

    [Fact]
    public async Task ItIsAskedOncePerImageWithTheLargestBoxItIsDrawnIn()
    {
        var processor = new Fake(_ => null);
        ImageSource logo = ImageSource.FromBytes(TestImages.Jpeg(64, 32));
        var document = new PdfDocument("x")
        {
            Header = new PageBand([new Image(logo) { Alt = "Logo", Height = Length.Cm(1) }]),
            Content = [new Image(logo) { Alt = "Logo", Width = Length.Cm(5) }, new Image(logo) { Alt = "Logo", Width = Length.Cm(8) }],
        };

        (byte[] file, _) = await RenderTests.RenderAsync(document, Options(processor));

        ImageRequest request = Assert.Single(processor.Requests);
        Assert.Equal("/content/0", request.Path);
        Assert.Equal(595.2756 - (2 * Length.Cm(2).Points), request.MaxWidth.Points, 2); // The header use sets only a height, so its width is bounded by the page.
        Assert.False(request.Synchronous);
        Assert.Contains("/Filter /DCTDecode", PdfInspect.Text(file), StringComparison.Ordinal); // Null: core read the JPEG itself.
    }

    [Fact]
    public async Task FailuresAreReportedAtTheImage()
    {
        var document = new PdfDocument("x")
        {
            Content =
            [
                new Image(ImageSource.FromBytes(new byte[] { 1 })) { Alt = "a" },
                new Image(ImageSource.FromBytes(new byte[] { 2 })) { Alt = "b" },
                new Image(ImageSource.FromBytes(new byte[] { 3 })) { Alt = "c" },
            ],
        };
        var processor = new Fake(request => request.Bytes.Span[0] switch
        {
            1 => throw new PdfException("Animated WebP isn't supported."),
            2 => throw new PdfLimitException("Too many pixels."),
            _ => PdfImage.Jpeg(new byte[] { 0, 1, 2 }),
        });

        PdfDocumentException error = await Assert.ThrowsAsync<PdfDocumentException>(
            () => document.RenderAsync(new MemoryStream(), Options(processor), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            ["ZPDF-I001 /content/0/source Animated WebP isn't supported.", "ZPDF-I002 /content/1/source Too many pixels.", "ZPDF-I001 /content/2/source The image processor returned JPEG data that isn't a JPEG."],
            error.Diagnostics.Select(d => $"{d.Code} {d.Path} {d.Message}"));
    }

    [Fact]
    public void ASynchronousRenderNeedsAProcessorThatFinishesAtOnce()
    {
        var document = new PdfDocument("x") { Content = [new Image(ImageSource.FromBytes(Unknown)) { Alt = "a" }] };
        var slow = new Fake(_ => null, delay: true);

        PdfDocumentException error = Assert.Throws<PdfDocumentException>(() => document.Render(new MemoryStream(), Options(slow)));
        Assert.Equal("ZPDF-R002 /content/0/source", Assert.Single(error.Diagnostics.Select(d => $"{d.Code} {d.Path}")));

        var quick = new Fake(_ => PdfImage.FromPixels(2, 2, PdfPixelFormat.Gray8, new byte[4]));
        using var output = new MemoryStream();
        document.Render(output, Options(quick));
        Assert.True(Assert.Single(quick.Requests).Synchronous);
    }

    [Fact]
    public void PixelsMustMatchTheirSize()
    {
        Assert.Throws<ArgumentException>(() => PdfImage.FromPixels(2, 2, PdfPixelFormat.Rgb24, new byte[11]));
        Assert.Throws<ArgumentException>(() => PdfImage.FromPixels(2, 2, PdfPixelFormat.Cmyk32, new byte[16], new byte[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfImage.FromPixels(0, 2, PdfPixelFormat.Gray8, Array.Empty<byte>()));
    }

    private static RenderOptions Options(IImageProcessor processor) => new() { Fonts = PdfInspect.Options().Fonts, ImageProcessor = processor };

    private sealed class Fake(Func<ImageRequest, PdfImage?> process, bool delay = false) : IImageProcessor
    {
        public List<ImageRequest> Requests { get; } = [];

        public async ValueTask<PdfImage?> ProcessAsync(ImageRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (delay)
            {
                await Task.Delay(10, cancellationToken);
            }

            return process(request);
        }
    }
}
