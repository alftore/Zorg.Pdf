using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Tests.Fonts;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Headers and footers: room kept for them, drawn on every page as artifacts, with exact page numbers.</summary>
public sealed class BandRenderTests
{
    private static readonly PageBand PageXOfY = new([new Paragraph(["Page ", Field.PageNumber, " of ", Field.PageCount]) { Align = TextAlign.Right }]);

    [Fact]
    public async Task AFooterWithPageNumbersIsDrawnOnEveryPageAsAnArtifact()
    {
        PdfDocument document = TableRenderTests.Invoice with { Footer = PageXOfY };
        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, Rows(120));

        Assert.False(result.HasWarnings);
        Assert.True(result.PageCount >= 3, $"{result.PageCount} pages");
        PdfFile.CheckStructure(file);
        var overlays = PdfInspect.PageOverlays(file).Select(o => o!.Value).ToList();
        Assert.Equal(result.PageCount, overlays.Count);
        Assert.Equal(result.PageCount, overlays.Select(o => o.Number).Distinct().Count()); // One per page: the number differs.

        var font = TrueTypeFont.Parse(TestFonts.Require());
        for (int page = 1; page <= overlays.Count; page++)
        {
            string content = overlays[page - 1].Content;
            Assert.Contains("/Artifact <</Type /Pagination /Subtype /Footer>> BDC", content, StringComparison.Ordinal);
            Assert.DoesNotContain("MCID", content, StringComparison.Ordinal);
            Assert.Contains(Glyph(font, page), content, StringComparison.Ordinal);
            Assert.Contains(Glyph(font, result.PageCount), content, StringComparison.Ordinal);
        }

        // Nothing of the footer is in the tagged content or the structure tree.
        Assert.All(PdfInspect.PageContents(file), page => Assert.DoesNotContain("/Subtype /Footer", page, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABandWithoutFieldsIsOneStreamThatEveryPageShares()
    {
        PdfDocument document = TableRenderTests.Invoice with
        {
            Header = new PageBand([new Paragraph("Zorg AS · Confidential")]),
        };
        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, Rows(120));

        var overlays = PdfInspect.PageOverlays(file).Select(o => o!.Value).ToList();
        Assert.Equal(result.PageCount, overlays.Count);
        Assert.Single(overlays.Select(o => o.Number).Distinct());
        Assert.Contains("/Artifact <</Type /Pagination /Subtype /Header>> BDC", overlays[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BandsTakeTheirRoomFromTheContent()
    {
        (_, RenderResult plain) = await RenderTests.RenderAsync(TableRenderTests.Invoice, Rows(200));
        var tall = new PageBand([new Paragraph("Letterhead"), new Spacer(Length.Cm(6))]);
        (_, RenderResult banded) = await RenderTests.RenderAsync(TableRenderTests.Invoice with { Header = tall, Footer = PageXOfY }, Rows(200));

        Assert.True(banded.PageCount > plain.PageCount, $"{banded.PageCount} pages with bands, {plain.PageCount} without");
    }

    [Fact]
    public async Task BandsThatLeaveNoRoomAreAnErrorBeforeAnyOutput()
    {
        var output = new MemoryStream();
        PdfDocument document = new("x") { Footer = new PageBand([new Spacer(Length.Cm(26))]), Content = [new Paragraph("Hello")] };

        PdfDocumentException error = await Assert.ThrowsAsync<PdfDocumentException>(
            () => document.RenderAsync(output, PdfInspect.Options(), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("ZPDF-L004 /footer", Assert.Single(error.Diagnostics.Select(d => $"{d.Code} {d.Path}")));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task FieldsInTheContentAreLeftOutWithAWarning()
    {
        PdfDocument document = new("x")
        {
            Header = new PageBand([new Image(ImageSource.FromSlot("logo")) { Alt = "Logo" }, new Paragraph(["Page ", Field.PageNumber])]),
            Content = [new Paragraph(["On page ", Field.PageNumber])],
        };
        var options = new RenderOptions { Fonts = PdfInspect.Options().Fonts, Data = PdfData.Empty.WithImage("logo", Images.TestImages.Jpeg(16, 16)) };
        (_, RenderResult result) = await RenderTests.RenderAsync(document, options);

        Assert.Equal(["ZPDF-R001 /content/0/inlines/1"], result.Diagnostics.Select(d => $"{d.Code} {d.Path}"));
    }

    [Fact]
    public async Task SyncAndAsyncRendersWithBandsGiveTheSameBytes()
    {
        PdfDocument document = TableRenderTests.Invoice with { Header = new PageBand([new Paragraph("Zorg AS")]), Footer = PageXOfY };
        (byte[] asynchronous, _) = await RenderTests.RenderAsync(document, Rows(90));
        Assert.Equal(asynchronous, RenderSync(document, Rows(90)));
    }

    private static byte[] RenderSync(PdfDocument document, RenderOptions options)
    {
        using var output = new MemoryStream();
        document.Render(output, options);
        return output.ToArray();
    }

    [Fact]
    public async Task ABandParagraphUsesTheHeaderOrFooterStyle()
    {
        var styles = new StyleSheet { Styles = StyleSheet.Default.Styles.SetItem("footer", new TextStyle { Size = Length.Pt(13) }) };
        (byte[] file, _) = await RenderTests.RenderAsync(new PdfDocument("x") { Styles = styles, Footer = PageXOfY, Content = [new Paragraph("Body")] });

        Assert.Contains(" 13 Tf", PdfInspect.PageOverlays(file)[0]!.Value.Content, StringComparison.Ordinal);
    }

    /// <summary>The digits of a number as the glyph IDs a content stream shows (the subset keeps the font's IDs).</summary>
    private static string Glyph(TrueTypeFont font, int number) =>
        string.Concat(number.ToString(CultureInfo.InvariantCulture).Select(c => font.GlyphFor(c).ToString("X4", CultureInfo.InvariantCulture)));

    /// <summary>The invoice's rows, from an IEnumerable so a synchronous render can read them too.</summary>
    private static RenderOptions Rows(int count) => new()
    {
        Fonts = PdfInspect.Options().Fonts,
        Data = PdfData.Empty.WithRows("lines", Enumerable.Range(1, count), TableRenderTests.Cells),
    };
}
