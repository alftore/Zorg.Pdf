using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Reading;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Attachments: embedded files with their file specifications, the attachments panel and PDF/A-3's associated files.</summary>
public sealed class AttachmentTests
{
    private static readonly byte[] Xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><invoice id=\"42\"/>");

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "The PDF checksum is MD5 by definition.")]
    public async Task FilesAreEmbeddedListedAndAssociated()
    {
        var document = new PdfDocument("Invoice 42")
        {
            Attachments =
            [
                new Attachment("zeta.csv", ImageSource.FromBytes("a;b\n1;2"u8.ToArray())) { MimeType = "text/csv" },
                new Attachment("faktura-ø.xml", ImageSource.FromSlot("xml")) { MimeType = "application/xml", Relationship = AttachmentRelationship.Data, Description = "The invoice as XML" },
            ],
            Content = [new Paragraph("See the attachments.")],
        };
        RenderOptions inspect = PdfInspect.Options(PdfConformance.PdfUA1);

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, new RenderOptions { Fonts = inspect.Fonts, Conformance = PdfConformance.PdfUA1, Archive = PdfArchive.PdfA3a, Data = PdfData.Empty.WithFile("xml", Xml) });

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity != DiagnosticSeverity.Info);
        PdfFile.CheckStructure(file);
        var cos = new CosFile(file, PdfReadLimits.Default, []);
        var names = (CosArray)cos.Resolve(((CosDictionary)cos.Resolve(((CosDictionary)cos.Resolve(cos.Catalog["Names"]))["EmbeddedFiles"]))["Names"]);
        Assert.Equal(4, names.Items.Count);
        Assert.Equal(["faktura-ø.xml", "zeta.csv"], new[] { names.Items[0], names.Items[2] }.Select(k => CosPages.Text(cos.Resolve(k)))); // Sorted.
        var spec = (CosDictionary)cos.Resolve(names.Items[1]);
        Assert.Equal(("faktura-_.xml", "faktura-ø.xml", "Data", "The invoice as XML"), (CosPages.Text(cos.Resolve(spec["F"])), CosPages.Text(cos.Resolve(spec["UF"])), ((CosName)cos.Resolve(spec["AFRelationship"])).Value, CosPages.Text(cos.Resolve(spec["Desc"]))));
        var embedded = (CosStream)cos.Resolve(((CosDictionary)cos.Resolve(spec["EF"]))["F"]);
        Assert.Equal(Xml, cos.Decode(embedded));
        Assert.Equal("application/xml", ((CosName)cos.Resolve(embedded.Dictionary["Subtype"])).Value);
        var parameters = (CosDictionary)cos.Resolve(embedded.Dictionary["Params"]);
        Assert.Equal((long)Xml.Length, cos.Resolve(parameters["Size"]).AsInteger);
        Assert.Equal(MD5.HashData(Xml), ((CosString)cos.Resolve(parameters["CheckSum"])).Value);
        Assert.Equal(2, ((CosArray)cos.Resolve(cos.Catalog["AF"])).Items.Count);
        Assert.Contains("<pdfaid:part>3</pdfaid:part>", PdfInspect.Text(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PdfA2RefusesAttachmentsAndLimitsApply()
    {
        var document = new PdfDocument("x") { Attachments = [new Attachment("a.xml", ImageSource.FromBytes(Xml)) { MimeType = "application/xml" }], Content = [new Paragraph("x")] };
        RenderOptions inspect = PdfInspect.Options();

        PdfDocumentException a2 = await Assert.ThrowsAsync<PdfDocumentException>(() => RenderTests.RenderAsync(document, new RenderOptions { Fonts = inspect.Fonts, Archive = PdfArchive.PdfA2b }));
        PdfDocumentException large = await Assert.ThrowsAsync<PdfDocumentException>(() => RenderTests.RenderAsync(document, new RenderOptions { Fonts = inspect.Fonts, Limits = PdfLimits.Default with { MaxAttachmentBytes = 10 } }));
        (_, RenderResult plain) = await RenderTests.RenderAsync(document, inspect);

        Assert.Equal("ZPDF-A005", Assert.Single(a2.Diagnostics).Code);
        Assert.Equal("ZPDF-R004", Assert.Single(large.Diagnostics).Code);
        Assert.Empty(plain.Diagnostics); // Without PDF/A, any attachment is fine.
    }

    [Fact]
    public void NamesTypesAndSourcesAreChecked()
    {
        var document = new PdfDocument("x")
        {
            Attachments =
            [
                new Attachment("data/a.xml", ImageSource.FromBytes(Xml)),
                new Attachment("b.xml", ImageSource.FromBytes(Xml)) { MimeType = "xml" },
                new Attachment("B.XML", ImageSource.FromBytes(Xml)),
            ],
        };

        Assert.Equal(["/attachments/0/name", "/attachments/1/mimeType", "/attachments/2/name"], document.Validate().Diagnostics.Select(d => d.Path));
        Assert.All(document.Validate().Diagnostics, d => Assert.Equal("ZPDF-J031", d.Code));
    }

    [Fact]
    public void AttachmentsRoundTripThroughJson()
    {
        PdfDocument parsed = PdfDocument.ParseJson("""{"title":"x","attachments":[{"name":"a.xml","source":{"$data":"xml"},"mimeType":"application/xml","relationship":"data","description":"XML"}]}""");

        Attachment attachment = Assert.Single(parsed.Attachments);
        Assert.Equal(("a.xml", "application/xml", AttachmentRelationship.Data, "XML"), (attachment.Name, attachment.MimeType, attachment.Relationship, attachment.Description));
        Assert.Equal(parsed.ToJson(), PdfDocument.ParseJson(parsed.ToJson()).ToJson());
        Assert.DoesNotContain("attachments", new PdfDocument("y").ToJson(), StringComparison.Ordinal);
    }
}
