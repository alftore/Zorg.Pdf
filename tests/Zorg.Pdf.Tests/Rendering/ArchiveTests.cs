using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Tests.Images;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>PDF/A-2: the sRGB output intent, the identifier in XMP (with PDF/UA's declared as an extension), and what it forbids.</summary>
public sealed class ArchiveTests
{
    private const string ProfileHash = "384B832DE3412066743B52A75EE906B6FB9FB8D9E09E936FC2C43223815C6E0A"; // sRGB2014.icc, unchanged.

    [Theory]
    [InlineData(PdfArchive.PdfA2a, "A")]
    [InlineData(PdfArchive.PdfA2u, "U")]
    [InlineData(PdfArchive.PdfA2b, "B")]
    public async Task AnArchiveClaimWritesTheIdentifierAndAnSrgbOutputIntent(PdfArchive archive, string level)
    {
        var options = new RenderOptions { Fonts = PdfInspect.Options().Fonts, Archive = archive };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(new PdfDocument("Archive") { Content = [new Paragraph("Kept for years.")] }, options);

        Assert.Empty(result.Diagnostics);
        PdfFile.CheckStructure(file);
        string text = PdfInspect.Text(file);
        Assert.Contains($"<pdfaid:part>2</pdfaid:part>\n<pdfaid:conformance>{level}</pdfaid:conformance>", text, StringComparison.Ordinal);
        Assert.Matches(@"/OutputIntents \[<</Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier \(sRGB IEC61966-2\.1\)", text);
        Assert.DoesNotContain("pdfaExtension", text, StringComparison.Ordinal); // Only needed beside PDF/UA's identifier.
        byte[] profile = PdfReader.Read(file).Values.Single(v => v.IsStream && v.Value.Contains("/N 3", StringComparison.Ordinal)).Decoded!;
        Assert.Equal(ProfileHash, Convert.ToHexString(SHA256.HashData(profile)));
    }

    [Fact]
    public async Task WithPdfUAThePdfUAIdentifierIsDeclaredAsAnExtensionSchema()
    {
        RenderOptions options = new() { Fonts = PdfInspect.Options().Fonts, Conformance = PdfConformance.PdfUA1, Archive = PdfArchive.PdfA2a };

        (byte[] file, _) = await RenderTests.RenderAsync(new PdfDocument("Both") { Content = [new Paragraph("Accessible and archived.")] }, options);

        string text = PdfInspect.Text(file);
        Assert.Contains("<pdfuaid:part>1</pdfuaid:part>", text, StringComparison.Ordinal);
        Assert.Contains("<pdfaSchema:namespaceURI>http://www.aiim.org/pdfua/ns/id/</pdfaSchema:namespaceURI>", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAClaimThereIsNoOutputIntent()
    {
        (byte[] file, _) = await RenderTests.RenderAsync(new PdfDocument("x") { Content = [new Paragraph("Plain.")] }, PdfInspect.Options());

        Assert.DoesNotContain("OutputIntents", PdfInspect.Text(file), StringComparison.Ordinal);
        Assert.DoesNotContain("pdfaid", PdfInspect.Text(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CmykImagesAndMissingCharactersStopAnArchiveRender()
    {
        var document = new PdfDocument("x")
        {
            Content = [new Image(ImageSource.FromBytes(TestImages.Jpeg(8, 8, components: 4))) { Alt = "A swatch" }, new Paragraph("Snowman: ☃￿".Replace("￿", "\U0001F9E9", StringComparison.Ordinal))],
        };
        RenderOptions plain = PdfInspect.Options();

        (_, RenderResult allowed) = await RenderTests.RenderAsync(document, plain);
        PdfDocumentException stopped = await Assert.ThrowsAsync<PdfDocumentException>(() => RenderTests.RenderAsync(document, new RenderOptions { Fonts = plain.Fonts, Archive = PdfArchive.PdfA2b }));

        Assert.Contains(allowed.Diagnostics, d => d.Code == "ZPDF-F002" && d.Severity == DiagnosticSeverity.Warning);
        Assert.Contains(stopped.Diagnostics, d => d.Code == "ZPDF-I001" && d.Message.Contains("CMYK", StringComparison.Ordinal));
        Assert.Contains(stopped.Diagnostics, d => d.Code == "ZPDF-F002" && d.Severity == DiagnosticSeverity.Error && d.Message.Contains("PDF/A", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEmbeddedProfileIsTheIccsUnchanged()
    {
        // Its licence allows embedding the profile unchanged; this fails if the resource is ever edited.
        using Stream stream = typeof(PdfDocument).Assembly.GetManifestResourceStream("sRGB2014.icc")!;
        Assert.Equal(ProfileHash, Convert.ToHexString(SHA256.HashData(stream)));
    }
}
