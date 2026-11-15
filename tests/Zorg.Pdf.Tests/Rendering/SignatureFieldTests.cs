using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Signing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Visible signature fields: signed in place, or left empty for later signers.</summary>
public sealed partial class SignatureFieldTests
{
    private static PdfDocument Contract(params SignatureField[] fields) =>
        new("Contract") { Language = "en", Content = [new Heading("Service agreement"), new Paragraph("The parties agree."), .. fields] };

    [Fact]
    public void AFieldRoundTripsThroughJsonAndBadNamesAreErrors()
    {
        PdfDocument document = PdfDocument.ParseJson("""{"title":"x","content":[{"type":"signatureField","name":"approver","label":"Approved by","width":"70mm","align":"right"}]}""");
        SignatureField field = Assert.IsType<SignatureField>(document.Content[0]);
        Assert.Equal(("approver", "Approved by", Length.Mm(70), TextAlign.Right), (field.Name, field.Label, field.Width!.Value, field.Align));
        Assert.Equal(document.ToJson(), PdfDocument.ParseJson(document.ToJson()).ToJson());
        Assert.True(document.Validate().IsValid);

        var bad = new PdfDocument("x") { Content = [new SignatureField("a.b"), new SignatureField("ok"), new SignatureField("ok"), new SignatureField("w") { Height = Length.Zero }] };
        Assert.Equal(["ZPDF-J034 /content/0/name", "ZPDF-J034 /content/2/name", "ZPDF-J013 /content/3/height"], bad.Validate().Diagnostics.Select(d => $"{d.Code} {d.Path}"));
    }

    [Fact]
    public async Task TheMatchingFieldIsSignedInPlaceAndTagged()
    {
        IPdfSigner signer = PdfSigners.FromCertificate(SignatureTests.Pki.Value.Rsa);
        RenderOptions options = PdfInspect.Options(PdfConformance.PdfUA1) with
        {
            Signature = new PdfSignatureOptions(signer) { FieldName = "approver", Reason = "Approved", SigningTime = new DateTimeOffset(2026, 11, 15, 10, 30, 0, TimeSpan.FromHours(1)) },
        };
        (byte[] file, _) = await RenderTests.RenderAsync(Contract(new SignatureField("approver") { Label = "Approved by the finance manager" }), options);

        SignatureTests.Verify(file);
        string flat = PdfInspect.Text(file);
        Assert.Contains("/Subtype /Widget /FT /Sig /T (approver) /TU (Approved by the finance manager) /V ", flat, StringComparison.Ordinal);
        Assert.DoesNotContain("/Rect [0 0 0 0]", flat, StringComparison.Ordinal); // No invisible field besides it.
        Assert.Matches(@"/F 132 /P \d+ 0 R /AP <</N \d+ 0 R>> /StructParent \d+", flat);
        Assert.Single(Regex.Matches(flat, @"/FT /Sig"));
        Assert.Contains("/SigFlags 3", flat, StringComparison.Ordinal);
        Assert.Single(PdfInspect.Elements(file, "Form"));
        Assert.Matches(@"/Subtype /Form /BBox \[0 0 170\.0\d* 56\.69\d*\]", flat);
    }

    [Fact]
    public async Task EmptyFieldsWaitForLaterSignersAndAnUnmatchedSignatureStaysInvisible()
    {
        (byte[] unsigned, _) = await RenderTests.RenderAsync(Contract(new SignatureField("buyer"), new SignatureField("seller") { Align = TextAlign.Right }), PdfInspect.Options(PdfConformance.PdfUA1));
        string flat = PdfInspect.Text(unsigned);
        Assert.Equal(2, Regex.Count(flat, @"/FT /Sig /T \((buyer|seller)\) /TU \(Signature\) /Rect"));
        Assert.DoesNotContain("/V ", flat, StringComparison.Ordinal);
        Assert.Contains("/SigFlags 1", flat, StringComparison.Ordinal); // Fields exist, none signed yet.
        Assert.Contains("/AcroForm <</Fields [", flat, StringComparison.Ordinal);
        Assert.Equal(2, PdfInspect.Elements(unsigned, "Form").Count);

        RenderOptions signing = PdfInspect.Options() with { Signature = new PdfSignatureOptions(PdfSigners.FromCertificate(SignatureTests.Pki.Value.Ec)) };
        (byte[] mixed, _) = await RenderTests.RenderAsync(Contract(new SignatureField("witness")), signing);
        SignatureTests.Verify(mixed);
        string mixedFlat = PdfInspect.Text(mixed);
        Assert.Contains("/T (Signature1) /V ", mixedFlat, StringComparison.Ordinal);
        Assert.Contains("/Rect [0 0 0 0]", mixedFlat, StringComparison.Ordinal);
        Assert.Contains("/T (witness) /TU (Signature) /Rect", mixedFlat, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFieldThatDoesntFitMovesToTheNextPage()
    {
        PdfDocument document = Contract(new SignatureField("late") { Height = Length.Mm(40) }) with
        {
            Content = [.. Enumerable.Range(0, 80).Select(i => (Block)new Paragraph($"Clause {i}: the parties agree to the terms set out in this clause.")), new SignatureField("late") { Height = Length.Mm(40) }],
        };
        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, PdfInspect.Options());

        Assert.True(result.PageCount >= 2);
        string[] pages = PdfInspect.PageContents(file);
        Assert.Matches(@"/Annots \[\d+ 0 R\]", PdfInspect.Text(file));
        Assert.NotEmpty(pages);
    }
}
