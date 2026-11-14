using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Invoicing;
using Zorg.Pdf.Reading;
using Zorg.Pdf.Tests.Model;
using Zorg.Pdf.Tests.Rendering;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Extensions;

/// <summary>Factur-X and ZUGFeRD: the profile read from the CII XML, and the attachment, metadata and PDF/A-3 added for it.</summary>
public sealed class FacturXTests
{
    private static byte[] Sample() => File.ReadAllBytes(RepoFiles.Path("docs", "examples", "facturx", "invoice-en16931.xml"));

    [Fact]
    public async Task AnInvoiceCarriesItsXmlMetadataAndPdfA3()
    {
        FacturXInvoice facturX = FacturX.Read(Sample());
        var invoice = new PdfDocument("Invoice 2026-0042") { Language = "de", Content = [new Heading("Rechnung 2026-0042"), new Paragraph("Beratung, November: 1 190,00 EUR")] };
        RenderOptions inspect = PdfInspect.Options(PdfConformance.PdfUA1);

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(facturX.ApplyTo(invoice), facturX.ApplyTo(new RenderOptions { Fonts = inspect.Fonts, Conformance = PdfConformance.PdfUA1 }));

        Assert.Equal((FacturXProfile.EN16931, "2026-0042", "380", "EN 16931", "factur-x.xml"), (facturX.Profile, facturX.InvoiceNumber, facturX.TypeCode, facturX.ConformanceLevel, facturX.FileName));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity != DiagnosticSeverity.Info);
        PdfFile.CheckStructure(file);
        string text = PdfInspect.Text(file);
        Assert.Contains("<pdfaid:part>3</pdfaid:part>", text, StringComparison.Ordinal);
        Assert.Contains("<pdfaid:conformance>B</pdfaid:conformance>", text, StringComparison.Ordinal);
        Assert.Contains("<fx:ConformanceLevel>EN 16931</fx:ConformanceLevel>", text, StringComparison.Ordinal);
        Assert.Contains("<fx:DocumentFileName>factur-x.xml</fx:DocumentFileName>", text, StringComparison.Ordinal);
        Assert.Contains("<pdfaSchema:prefix>fx</pdfaSchema:prefix>", text, StringComparison.Ordinal);
        Assert.Contains("<pdfaSchema:prefix>pdfuaid</pdfaSchema:prefix>", text, StringComparison.Ordinal); // Both described.

        var cos = new CosFile(file, PdfReadLimits.Default, []);
        var names = (CosArray)cos.Resolve(((CosDictionary)cos.Resolve(((CosDictionary)cos.Resolve(cos.Catalog["Names"]))["EmbeddedFiles"]))["Names"]);
        var spec = (CosDictionary)cos.Resolve(names.Items[1]);
        Assert.Equal("Alternative", ((CosName)cos.Resolve(spec["AFRelationship"])).Value);
        Assert.Equal(Sample(), cos.Decode((CosStream)cos.Resolve(((CosDictionary)cos.Resolve(spec["EF"]))["F"])));
    }

    [Theory]
    [InlineData("urn:factur-x.eu:1p0:minimum", FacturXProfile.Minimum, "Data", "factur-x.xml")]
    [InlineData("urn:factur-x.eu:1p0:basicwl", FacturXProfile.BasicWL, "Data", "factur-x.xml")]
    [InlineData("urn:cen.eu:en16931:2017#compliant#urn:factur-x.eu:1p0:basic", FacturXProfile.Basic, "Alternative", "factur-x.xml")]
    [InlineData("urn:cen.eu:en16931:2017#conformant#urn:factur-x.eu:1p0:extended", FacturXProfile.Extended, "Alternative", "factur-x.xml")]
    [InlineData("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0", FacturXProfile.XRechnung, "Alternative", "xrechnung.xml")]
    public void ProfilesAreReadFromTheGuideline(string guideline, FacturXProfile profile, string relationship, string fileName)
    {
        FacturXInvoice facturX = FacturX.Read(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Sample()).Replace("urn:cen.eu:en16931:2017<", guideline + "<", StringComparison.Ordinal)));

        Assert.Equal((profile, fileName), (facturX.Profile, facturX.FileName));
        Assert.Equal(relationship, facturX.Attachment.Relationship.ToString());
    }

    [Theory]
    [InlineData("<rsm:CrossIndustryInvoice", "ZPDF-X001")]
    [InlineData("<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\"/>", "ZPDF-X002")]
    [InlineData("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>", "ZPDF-X001")]
    public void XmlThatIsntCiiIsRefused(string xml, string code)
    {
        PdfDocumentException failed = Assert.Throws<PdfDocumentException>(() => FacturX.Read(Encoding.UTF8.GetBytes(xml)));

        Assert.Equal(code, Assert.Single(failed.Diagnostics).Code);
    }

    [Fact]
    public void AnUnknownProfileIsRefused()
    {
        byte[] xml = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Sample()).Replace("urn:cen.eu:en16931:2017<", "urn:example:custom<", StringComparison.Ordinal));

        Assert.Equal("ZPDF-X003", Assert.Single(Assert.Throws<PdfDocumentException>(() => FacturX.Read(xml)).Diagnostics).Code);
    }

    [Fact]
    public void OptionsKeepTheirLevelAndReplaceAnEarlierAttachment()
    {
        FacturXInvoice facturX = FacturX.Read(Sample());
        var document = new PdfDocument("x") { Attachments = [new Attachment("FACTUR-X.xml", ImageSource.FromBytes(new byte[] { 1 })), new Attachment("terms.pdf", ImageSource.FromBytes(new byte[] { 2 })) { MimeType = "application/pdf" }] };

        Assert.Equal(PdfArchive.PdfA3a, facturX.ApplyTo(new RenderOptions { Archive = PdfArchive.PdfA2a }).Archive);
        Assert.Equal(PdfArchive.PdfA3u, facturX.ApplyTo(new RenderOptions { Archive = PdfArchive.PdfA3u }).Archive);
        Assert.Single(facturX.ApplyTo(facturX.ApplyTo(new RenderOptions())).MetadataSchemas); // Applied twice: one fx schema.
        Assert.Equal(["factur-x.xml", "terms.pdf"], facturX.ApplyTo(document).Attachments.Select(a => a.Name));
    }
}
