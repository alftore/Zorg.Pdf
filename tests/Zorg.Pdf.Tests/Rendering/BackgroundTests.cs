using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Reading;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Letterhead: a page of an existing PDF behind the pages, copied once as a form XObject and drawn as an artifact.</summary>
public sealed class BackgroundTests
{
    [Fact]
    public async Task ALetterheadGoesBehindTheFirstPageAndAPlainSheetBehindTheRest()
    {
        byte[] letterhead = await Letterhead();
        var letter = new PdfDocument("Letter")
        {
            Background = new PageBackground(ImageSource.FromSlot("letterhead")) { FirstPage = 1, OtherPages = 2 },
            Content = [new Paragraph("Dear Kari,"), new PageBreak(), new Paragraph("Page two"), new PageBreak(), new Paragraph("Page three")],
        };
        RenderOptions inspect = PdfInspect.Options(PdfConformance.PdfUA1);
        var options = new RenderOptions { Fonts = inspect.Fonts, Conformance = PdfConformance.PdfUA1, Data = PdfData.Empty.WithPdf("letterhead", letterhead) };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(letter, options);
        (byte[] again, _) = await RenderTests.RenderAsync(letter, options);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity != DiagnosticSeverity.Info);
        PdfFile.CheckStructure(file);
        Assert.Equal(file, again); // Deterministic.
        var cos = new CosFile(file, PdfReadLimits.Default, []);
        var pages = CosPages.Read(cos, PdfReadLimits.Default, []);
        Assert.Equal(3, pages.Count);
        string[] backgrounds = [.. pages.Select(p => string.Join(",", ((CosDictionary)cos.Resolve(((CosDictionary)cos.Resolve(p.Resources))["XObject"])).Entries.Keys.Where(k => k.StartsWith("Bg", StringComparison.Ordinal))))];
        Assert.Equal(["Bg1", "Bg2", "Bg2"], backgrounds);

        // Each form is written once, without the letterhead's own tags, and drawn first as an artifact.
        var form = (CosStream)cos.Resolve(((CosDictionary)cos.Resolve(((CosDictionary)cos.Resolve(pages[0].Resources))["XObject"]))["Bg1"]);
        string drawn = Encoding.Latin1.GetString(cos.Decode(form));
        Assert.DoesNotContain("BDC", drawn, StringComparison.Ordinal);
        Assert.Matches(@"\bT[jJ]\b", drawn); // The letterhead's text, kerned (TJ) or not (Tj) depending on the font.
        Assert.Null(form.Dictionary["StructParents"]);
        string first = Encoding.Latin1.GetString(cos.Decode((CosStream)cos.Resolve(((CosArray)cos.Resolve(pages[0].Dictionary["Contents"])).Items[0])));
        Assert.Contains("/Artifact <</Type /Layout>> BDC", first, StringComparison.Ordinal);
        Assert.Contains("/Bg1 Do", first, StringComparison.Ordinal);
        Assert.Equal(2, Encoding.Latin1.GetString(file).Split("/Subtype /Form").Length - 1);
    }

    [Fact]
    public async Task FontsThatArentEmbeddedWarnAndFailUnderConformance()
    {
        byte[] helvetica = Hand("<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>", "BT /F1 24 Tf 72 720 Td (ACME) Tj ET");

        RenderResult plain = await Render(helvetica, PdfConformance.None);
        PdfDocumentException failed = await Assert.ThrowsAsync<PdfDocumentException>(() => Render(helvetica, PdfConformance.PdfUA1));

        Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(plain.Diagnostics, d => d.Code == "ZPDF-G004").Severity);
        Assert.Contains(failed.Diagnostics, d => d.Code == "ZPDF-G004" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task CmykFailsUnderPdfA()
    {
        byte[] cmyk = Hand("<< >>", "0 0 0 1 k 0 0 100 100 re f");

        RenderResult plain = await Render(cmyk, PdfConformance.None);
        PdfDocumentException failed = await Assert.ThrowsAsync<PdfDocumentException>(() => Render(cmyk, PdfConformance.None, PdfArchive.PdfA2b));

        Assert.DoesNotContain(plain.Diagnostics, d => d.Code == "ZPDF-G005");
        Assert.Contains(failed.Diagnostics, d => d.Code == "ZPDF-G005");
    }

    [Fact]
    public async Task FilesThatCantBeUsedStopTheRenderWithAReason()
    {
        PdfDocumentException notPdf = await Assert.ThrowsAsync<PdfDocumentException>(() => Render("not a pdf"u8.ToArray(), PdfConformance.None));
        PdfDocumentException missing = await Assert.ThrowsAsync<PdfDocumentException>(() => Render(Hand("<< >>", "0 0 m"), PdfConformance.None, otherPages: 3));
        byte[] encrypted = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(Hand("<< >>", "0 0 m")).Replace("/Root 1 0 R", "/Root 1 0 R /Encrypt << /Filter /Standard >>", StringComparison.Ordinal));
        PdfDocumentException locked = await Assert.ThrowsAsync<PdfDocumentException>(() => Render(encrypted, PdfConformance.None));

        Assert.Equal(("ZPDF-G001", "/background/source"), (notPdf.Diagnostics[0].Code, notPdf.Diagnostics[0].Path));
        Assert.Equal(("ZPDF-G002", "/background/otherPages"), (missing.Diagnostics[0].Code, missing.Diagnostics[0].Path));
        Assert.Contains("encrypted", Assert.Single(locked.Diagnostics).Message, StringComparison.Ordinal);
        Assert.Equal("ZPDF-J030", Assert.Single(new PdfDocument("x") { Background = new PageBackground(ImageSource.FromSlot("a")) { FirstPage = -1 } }.Validate().Diagnostics).Code);
    }

    [Theory]
    [InlineData(0, 612, 792, 0.97268, 0)] // US Letter on A4: scaled to the width, centred vertically.
    [InlineData(90, 842, 595, 1, 0)] // A4 landscape stored as portrait, turned: the same size once turned.
    public void PagesAreTurnedAsShownThenScaledToFit(int rotate, double width, double height, double scale, double ox)
    {
        var page = new CosPage(new CosDictionary([]), 1, [0, 0, rotate == 0 ? width : height, rotate == 0 ? height : width], [0, 0, rotate == 0 ? width : height, rotate == 0 ? height : width], rotate, null);
        var a4 = new Zorg.Pdf.Rendering.PageGeometry(595.28, 841.89, 0, 0, 0, 0);
        var landscape = new Zorg.Pdf.Rendering.PageGeometry(841.89, 595.28, 0, 0, 0, 0);

        (double a, double b, double c, double d, double e, double f) = Zorg.Pdf.Rendering.BackgroundSet.Matrix(page, rotate == 0 ? a4 : landscape);

        if (rotate == 0)
        {
            Assert.Equal(scale, a, 3);
            Assert.Equal((0.0, 0.0), (b, c));
            Assert.Equal(scale, d, 4);
            Assert.Equal(ox, e, 1);
            Assert.Equal((841.89 - (792 * a)) / 2, f, 3);
        }
        else
        {
            // (x, y) → (y, w - x): the stored page's bottom left lands top left.
            Assert.Equal((0, -1, 1, 0), (Math.Round(a, 3), Math.Round(b, 3), Math.Round(c, 3), Math.Round(d, 3)));
            Assert.Equal(595, f, 0);
        }
    }

    [Fact]
    public void MarkedContentIsTakenOutAndInlineImagesKept()
    {
        byte[] content = Encoding.Latin1.GetBytes("/P <</MCID 0>> BDC BT /F1 9 Tf (x) Tj ET EMC /Artifact BMC 0 0 m EMC\nBI /W 1 /H 1 /CS /G /BPC 8 ID ÿBDC EI Q");

        string stripped = Encoding.Latin1.GetString(CosContent.Strip(content, out bool cmyk));

        Assert.False(cmyk);
        Assert.DoesNotContain("MCID", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("EMC", stripped, StringComparison.Ordinal);
        Assert.Contains("(x) Tj", stripped, StringComparison.Ordinal);
        Assert.Contains("ID ÿBDC EI", stripped, StringComparison.Ordinal); // The image's bytes, "BDC" among them, untouched.
    }

    private static async Task<RenderResult> Render(byte[] background, PdfConformance conformance, PdfArchive archive = PdfArchive.None, int otherPages = 1)
    {
        var document = new PdfDocument("x") { Background = new PageBackground(ImageSource.FromSlot("bg")) { OtherPages = otherPages }, Content = [new Paragraph("Body")] };
        RenderOptions inspect = PdfInspect.Options();
        (_, RenderResult result) = await RenderTests.RenderAsync(document, new RenderOptions { Fonts = inspect.Fonts, Conformance = conformance, Archive = archive, Data = PdfData.Empty.WithPdf("bg", background) });
        return result;
    }

    /// <summary>A letterhead made with Zorg.Pdf itself: tagged, two pages, A4.</summary>
    private static async Task<byte[]> Letterhead()
    {
        var document = new PdfDocument("ACME letterhead")
        {
            Content = [new Heading("ACME AS"), new Paragraph("Storgata 1, 0155 Oslo"), new PageBreak(), new Paragraph("ACME AS · continued")],
        };
        (byte[] file, _) = await RenderTests.RenderAsync(document, PdfInspect.Options(PdfConformance.PdfUA1));
        return file;
    }

    /// <summary>A one-page A4 PDF written by hand, with the given resources and content.</summary>
    private static byte[] Hand(string resources, string content)
    {
        var text = new StringBuilder("%PDF-1.4\n");
        int[] offsets = new int[5];
        void Add(int n, string body)
        {
            offsets[n] = text.Length;
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{n} 0 obj\n{body}\nendobj\n");
        }

        Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.28 841.89] /Resources {resources} /Contents 4 0 R >>");
        Add(4, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        int xref = text.Length;
        text.Append("xref\n0 5\n0000000000 65535 f \n");
        for (int i = 1; i < 5; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offsets[i]:D10} 00000 n \n");
        }

        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }
}
