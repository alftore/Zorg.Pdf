using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Imaging;
using Zorg.Pdf.Tests.Model;
using Zorg.Pdf.Tests.Rendering;
using Bitmap = Zorg.Imaging.Image;

namespace Zorg.Pdf.Tests.Images;

/// <summary>Page previews: Zorg.Pdf's own PDFs drawn through Zorg.Imaging's canvas, and anything else refused.</summary>
public sealed class PdfPreviewTests
{
    [Theory]
    [MemberData(nameof(ExampleRenderTests.Examples), MemberType = typeof(ExampleRenderTests))]
    public async Task EveryPageOfEveryExampleDraws(string name)
    {
        byte[] pdf = await RenderExampleAsync(name);

        int pages = PdfPreview.PageCount(pdf);
        Assert.InRange(pages, 1, 20);
        for (int page = 0; page < pages; page++)
        {
            using Bitmap image = await PdfPreview.RenderPageAsync(pdf, page, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(image.Width >= 300 && image.Height >= 300, $"{image.Width} x {image.Height}"); // A4 portrait or landscape, or a receipt.
            Assert.InRange(Ink(image, 0, 0, image.Width, image.Height), 0.002, 0.5);
        }
    }

    [Fact]
    public async Task TextAppearsWhereTheLayoutPutsItAndTheSamePngComesBackEveryTime()
    {
        var document = new PdfDocument("x")
        {
            Content = [new TextBox([new Paragraph("Kari Nordmann"), new Paragraph("Storgata 1")]) { Preset = TextBoxPreset.Din5008B }],
        };
        (byte[] pdf, _) = await RenderTests.RenderAsync(document, PdfInspect.Options());

        using Bitmap image = await PdfPreview.RenderPageAsync(pdf, 0, new PreviewOptions { Dpi = 72 }, TestContext.Current.CancellationToken);
        double inside = Ink(image, Pt(25), Pt(45), Pt(60), Pt(12));  // The first line, 25 mm in and 45 mm down.
        double outside = Ink(image, Pt(120), Pt(100), Pt(60), Pt(60));
        Assert.True(inside > 0.05, $"Ink in the address: {inside:P1}");
        Assert.Equal(0, outside);

        byte[] first = await PdfPreview.RenderPagePngAsync(pdf, 0, cancellationToken: TestContext.Current.CancellationToken);
        byte[] second = await PdfPreview.RenderPagePngAsync(pdf, 0, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(first, second);
        Assert.Equal("\x89PNG", Encoding.Latin1.GetString(first, 0, 4));

        static int Pt(double mm) => (int)Math.Round(Length.Mm(mm).Points);
    }

    [Fact]
    public async Task PdfsZorgPdfDidntWriteAreRefused()
    {
        byte[] foreign = Encoding.Latin1.GetBytes(ForeignPdf());

        PdfPreviewException error = await Assert.ThrowsAsync<PdfPreviewException>(async () => await PdfPreview.RenderPageAsync(foreign, 0, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Zorg.Pdf", error.Message, StringComparison.Ordinal);
        Assert.Throws<PdfPreviewException>(() => PdfPreview.PageCount("not a pdf at all"u8.ToArray()));
    }

    [Fact]
    public async Task LimitsAndPageNumbersAreChecked()
    {
        byte[] pdf = await RenderExampleAsync("letter");

        await Assert.ThrowsAsync<PdfPreviewException>(async () => await PdfPreview.RenderPageAsync(pdf, 0, new PreviewOptions { Limits = new PdfPreviewLimits { MaxStreamBytes = 100 } }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PdfPreviewException>(async () => await PdfPreview.RenderPageAsync(pdf, 0, new PreviewOptions { Limits = new PdfPreviewLimits { MaxOperators = 10 } }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PdfPreviewException>(async () => await PdfPreview.RenderPageAsync(pdf, 0, new PreviewOptions { Limits = new PdfPreviewLimits { MaxFileBytes = 100 } }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await PdfPreview.RenderPageAsync(pdf, 1, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await PdfPreview.RenderPageAsync(pdf, 0, new PreviewOptions { Dpi = 5 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DamagedFilesFailAsPreviewExceptionsOnly()
    {
        byte[] pdf = await RenderExampleAsync("timesheet");
        var random = new Random(20261114);
        for (int trial = 0; trial < 300; trial++)
        {
            byte[] damaged = (byte[])pdf.Clone();
            if (trial % 3 == 0)
            {
                damaged = damaged[..random.Next(damaged.Length)]; // Truncated.
            }
            else
            {
                for (int flips = 0; flips < 1 + (trial % 8); flips++)
                {
                    damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
                }
            }

            try
            {
                using Bitmap image = await PdfPreview.RenderPageAsync(damaged, 0, new PreviewOptions { Dpi = 24 }, TestContext.Current.CancellationToken);
            }
            catch (PdfPreviewException)
            {
                // Refused with a message: fine.
            }
        }
    }

    private static async Task<byte[]> RenderExampleAsync(string name)
    {
        PdfDocument document = PdfDocument.ParseJson(await File.ReadAllTextAsync(RepoFiles.Path("docs", "examples", name + ".json"), TestContext.Current.CancellationToken));
        string dataPath = RepoFiles.Path("docs", "examples", "data", name + ".json");
        var options = new RenderOptions
        {
            Fonts = PdfInspect.Options().Fonts,
            Data = File.Exists(dataPath) ? PdfData.ParseJson(await File.ReadAllTextAsync(dataPath, TestContext.Current.CancellationToken)) : PdfData.Empty,
        };
        (byte[] pdf, _) = await RenderTests.RenderAsync(document, options);
        return pdf;
    }

    /// <summary>The share of pixels in the rectangle darker than mid-light grey.</summary>
    private static double Ink(Bitmap image, int x, int y, int width, int height)
    {
        int dark = 0;
        for (int row = y; row < y + height; row++)
        {
            Span<byte> pixels = image.GetRowBytes(row);
            for (int column = x; column < x + width; column++)
            {
                dark += pixels[column * 3] + pixels[(column * 3) + 1] + pixels[(column * 3) + 2] < 3 * 200 ? 1 : 0;
            }
        }

        return (double)dark / (width * height);
    }

    /// <summary>A minimal PDF with a standard Type1 font, as other tools write: not Zorg.Pdf's.</summary>
    private static string ForeignPdf()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            "<< /Length 31 >>\nstream\nBT /F1 12 Tf 10 10 Td (Hi) Tj ET\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = text.Length;
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f\r\n");
        foreach (int offset in offsets)
        {
            text.Append($"{offset:D10} 00000 n\r\n");
        }

        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return text.ToString();
    }
}
