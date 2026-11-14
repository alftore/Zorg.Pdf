using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Tests.Images;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Watermarks: JSON, validation, one shared stream over or behind each page, opacity, and the artifact.</summary>
public sealed class WatermarkTests
{
    [Fact]
    public void AWatermarkRoundTripsThroughJson()
    {
        PdfDocument document = PdfDocument.ParseJson("""{"title":"x","watermark":{"text":"DRAFT","opacity":0.2,"layer":"behind"},"content":[]}""");

        Assert.Equal(new Watermark("DRAFT") { Opacity = 0.2, Layer = WatermarkLayer.Behind }, document.Watermark);
        Assert.Equal(document.ToJson(), PdfDocument.ParseJson(document.ToJson()).ToJson());
    }

    [Fact]
    public void AWatermarkNeedsTextOrAnImageAndAnOpacityFromZeroToOne()
    {
        var both = new PdfDocument("x") { Watermark = new Watermark("DRAFT") { Image = ImageSource.FromSlot("logo"), Opacity = 1.5 } };

        Assert.Equal(["ZPDF-J022 /watermark", "ZPDF-J022 /watermark/opacity"], both.Validate().Diagnostics.Select(d => $"{d.Code} {d.Path}"));
    }

    [Fact]
    public async Task ATextWatermarkIsOneSharedStreamOverEveryPage()
    {
        var document = new PdfDocument("x")
        {
            Watermark = new Watermark("DRAFT"),
            Footer = new PageBand([new Paragraph(["Page ", Field.PageNumber])]),
            Content = [new Paragraph("First page."), new PageBreak(), new Paragraph("Second page.")],
        };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, PdfInspect.Options(PdfConformance.PdfUA1));

        Assert.False(result.HasWarnings);
        PdfFile.CheckStructure(file);
        var pages = PdfInspect.PageStreams(file);
        Assert.Equal(2, pages.Count);
        Assert.All(pages, streams => Assert.Equal(3, streams.Length)); // Page, footer, watermark.
        Assert.Single(pages.Select(streams => streams[^1].Number).Distinct()); // The same watermark stream on both.

        string watermark = pages[0][^1].Content;
        Assert.StartsWith("q\n/GW gs\n0.7071", watermark, StringComparison.Ordinal); // Turned 45 degrees about the centre.
        Assert.Contains("/Artifact <</Type /Pagination /Subtype /Watermark>> BDC", watermark, StringComparison.Ordinal);
        Assert.EndsWith("Q\n", watermark, StringComparison.Ordinal);
        Assert.Contains("/Type /ExtGState /ca 0.15 /CA 0.15", PdfInspect.Text(file), StringComparison.Ordinal);
        Assert.Contains("/ExtGState <</GW ", PdfInspect.Text(file), StringComparison.Ordinal);

        // Automatic size: "DRAFT" across 80 % of A4's diagonal reach is far larger than body text.
        double size = double.Parse(Regex.Match(watermark, @" ([\d.]+) Tf").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(size, 150, 250);
    }

    [Fact]
    public async Task AnImageWatermarkCanSitBehindTheContent()
    {
        var document = new PdfDocument("x")
        {
            Watermark = new Watermark(ImageSource.FromSlot("logo")) { Layer = WatermarkLayer.Behind, Angle = 0, Size = Length.Cm(10) },
            Content = [new Paragraph("Text over the logo.")],
        };
        var options = new RenderOptions { Fonts = PdfInspect.Options().Fonts, Data = PdfData.Empty.WithImage("logo", TestImages.Jpeg(200, 100)) };

        (byte[] file, _) = await RenderTests.RenderAsync(document, options);

        var streams = PdfInspect.PageStreams(file)[0];
        Assert.Equal(2, streams.Length);
        Assert.Contains("/Im1 Do", streams[0].Content, StringComparison.Ordinal); // Drawn first: behind the page's own stream.
        Assert.Matches(@"(?m)^283\.46\d* 0 0 141\.73\d* -141\.73\d* -70\.86\d* cm$", streams[0].Content);
        Assert.Contains("/XObject <</Im1 ", PdfInspect.Text(file), StringComparison.Ordinal);
    }
}
