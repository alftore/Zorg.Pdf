using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Tests.Images;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Images in the content and in bands: placed, tagged, shared, and refused before writing when they can't be.</summary>
public sealed class ImageRenderTests
{
    private static readonly byte[] Photo = TestImages.Jpeg(400, 300);
    private static readonly byte[] Logo = TestImages.Png(64, 32, 6, 8, (x, y) => [x * 4, y * 8, 200, x < 32 ? 255 : 0]);

    [Fact]
    public async Task AnImageIsAFigureWithAltTextAndABoundingBox()
    {
        var document = new PdfDocument("x")
        {
            Content = [new Heading("Photo"), new Image(ImageSource.FromBytes(Photo)) { Alt = "A grey square", Align = TextAlign.Center }, new Paragraph("After.")],
        };
        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document);

        Assert.False(result.HasWarnings);
        PdfFile.CheckStructure(file);
        string pdf = PdfInspect.Text(file);
        Assert.Single(PdfInspect.Elements(file, "Figure"));
        Assert.Contains("/Alt (A grey square)", pdf, StringComparison.Ordinal);
        Assert.Matches(@"/A <</O /Layout /BBox \[[\d.]+ [\d.]+ [\d.]+ [\d.]+\]>>", pdf);
        Assert.Contains("/Subtype /Image /Width 400 /Height 300 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", pdf, StringComparison.Ordinal);
        Assert.Contains("/XObject <</Im1 ", pdf, StringComparison.Ordinal);

        // 400 × 300 px at 96 dpi is 300 × 225 pt, centred on A4 between 2 cm margins.
        string page = PdfInspect.PageContents(file)[0];
        Assert.Contains("/Figure <</MCID 1>> BDC", page, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^300 0 0 225 147\.6\d* [\d.]+ cm$", page);
        Assert.Contains("/Im1 Do", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADecorativeImageIsAnArtifactAndAPngWithAlphaHasASoftMask()
    {
        var document = new PdfDocument("x") { Content = [new Image(ImageSource.FromBytes(Logo)) { Decorative = true, Width = Length.Cm(4) }] };
        (byte[] file, _) = await RenderTests.RenderAsync(document);

        Assert.Empty(PdfInspect.Elements(file, "Figure"));
        Assert.Contains("/Artifact <</Type /Layout>> BDC", PdfInspect.PageContents(file)[0], StringComparison.Ordinal);
        string pdf = PdfInspect.Text(file);
        Assert.Matches(@"/Subtype /Image /Width 64 /Height 32 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /SMask \d+ 0 R", pdf);
        Assert.Contains("/Subtype /Image /Width 64 /Height 32 /ColorSpace /DeviceGray", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameImageTwiceIsOneXObject()
    {
        ImageSource logo = ImageSource.FromSlot("logo");
        var document = new PdfDocument("x")
        {
            Header = new PageBand([new Image(logo) { Alt = "Logo", Height = Length.Cm(1) }]),
            Content = [new Image(logo) { Alt = "Logo" }, new PageBreak(), new Image(logo) { Alt = "Logo again" }],
        };
        var options = new RenderOptions { Fonts = PdfInspect.Options().Fonts, Data = PdfData.Empty.WithImage("logo", Logo) };
        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, options);

        Assert.Equal(2, result.PageCount);
        Assert.Equal(2, Regex.Count(PdfInspect.Text(file), "/Subtype /Image /Width 64 ")); // The image and its mask, once.
        Assert.All(PdfInspect.PageOverlays(file), overlay => Assert.Contains("/Im1 Do", overlay!.Value.Content, StringComparison.Ordinal));
        Assert.Equal(2, PdfInspect.Elements(file, "Figure").Count);
    }

    [Fact]
    public async Task ASlotCanLoadItsImageAsynchronouslyButNotInASyncRender()
    {
        var document = new PdfDocument("x") { Content = [new Image(ImageSource.FromSlot("photo")) { Alt = "Photo" }] };
        var options = new RenderOptions
        {
            Fonts = PdfInspect.Options().Fonts,
            Data = PdfData.Empty.WithImage("photo", async ct =>
            {
                await Task.Yield();
                return Photo;
            }),
        };

        (byte[] file, _) = await RenderTests.RenderAsync(document, options);
        Assert.Contains("/Filter /DCTDecode", PdfInspect.Text(file), StringComparison.Ordinal);

        PdfDocumentException error = Assert.Throws<PdfDocumentException>(() => RenderSync(document, options));
        Assert.Equal("ZPDF-R002 /content/0/source", Assert.Single(error.Diagnostics.Select(d => $"{d.Code} {d.Path}")));
    }

    private static void RenderSync(PdfDocument document, RenderOptions options) => document.Render(new MemoryStream(), options);

    [Fact]
    public async Task MissingDamagedAndOversizedImagesAreErrorsBeforeAnyOutput()
    {
        var document = new PdfDocument("x")
        {
            Content =
            [
                new Image(ImageSource.FromSlot("missing")) { Alt = "a" },
                new Image(ImageSource.FromBytes(new byte[] { 1, 2, 3 })) { Alt = "b" },
                new Image(ImageSource.FromBytes(Photo)) { Alt = "c" },
            ],
        };
        var output = new MemoryStream();
        var options = new RenderOptions { Fonts = PdfInspect.Options().Fonts, Limits = PdfLimits.Default with { MaxImagePixels = 1000 } };

        PdfDocumentException error = await Assert.ThrowsAsync<PdfDocumentException>(() => document.RenderAsync(output, options, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(["ZPDF-D001 /content/0/source", "ZPDF-I001 /content/1/source", "ZPDF-I002 /content/2/source"], error.Diagnostics.Select(d => $"{d.Code} {d.Path}"));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task ATallImageIsScaledToTheContentAndMovesToTheNextPageWhenItDoesntFit()
    {
        byte[] tall = TestImages.Jpeg(200, 2000, components: 1);
        var document = new PdfDocument("x")
        {
            Content = [new Paragraph("Before."), new Image(ImageSource.FromBytes(tall)) { Alt = "Tall" }],
        };
        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document);

        Assert.Equal(2, result.PageCount);
        string second = PdfInspect.PageContents(file)[1];
        Match place = Regex.Match(second, @"([\d.]+) 0 0 ([\d.]+) ([\d.]+) ([\d.]+) cm");
        double height = double.Parse(place.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(841.89 - (2 * 56.69), height, 1); // The A4 content height.
        Assert.Equal(height / 10, double.Parse(place.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 1);
    }
}
