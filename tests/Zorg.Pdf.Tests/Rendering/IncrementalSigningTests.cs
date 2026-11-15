using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Signing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Signing existing files by incremental update: the original bytes stay, earlier signatures stay valid.</summary>
public sealed partial class IncrementalSigningTests
{
    private static PdfSignatureOptions Options(string field = "Signature1") =>
        new(PdfSigners.FromCertificate(SignatureTests.Pki.Value.Rsa)) { FieldName = field, Reason = "Countersigned", SigningTime = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero) };

    private static async Task<byte[]> SignAsync(byte[] file, PdfSignatureOptions options)
    {
        using var output = new MemoryStream();
        await PdfSigning.SignAsync(new MemoryStream(file), output, options, cancellationToken: TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    [Fact]
    public async Task AnUnsignedFileGetsAnInvisibleFieldAndKeepsItsBytes()
    {
        (byte[] original, _) = await RenderTests.RenderAsync(SignatureTests.Document(), PdfInspect.Options());
        byte[] signed = await SignAsync(original, Options());

        Assert.Equal(original, signed[..original.Length]);
        SignedCms cms = Assert.Single(VerifyAll(signed));
        Assert.Equal(SignatureTests.Pki.Value.Rsa.Thumbprint, cms.SignerInfos[0].Certificate!.Thumbprint);
        PdfFileInfo info = await PdfFileInfo.ReadAsync(new MemoryStream(signed), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal((1, "Signed invoice"), (info.PageCount, info.Title));
        Assert.Empty(info.Diagnostics); // Every object is where the new cross-reference stream says.
        string update = Encoding.Latin1.GetString(signed[original.Length..]);
        Assert.Contains("/FT /Sig /T (Signature1) /V ", update, StringComparison.Ordinal);
        Assert.Contains("/SigFlags 3", update, StringComparison.Ordinal);
        Assert.Contains("/Type /XRef", update, StringComparison.Ordinal); // The same kind as the file's own section.
    }

    [Fact]
    public async Task AnEmptyFieldIsSignedAndASecondSignatureKeepsTheFirstValid()
    {
        PdfDocument contract = new("Contract") { Language = "en", Content = [new Paragraph("The parties agree."), new SignatureField("supplier"), new SignatureField("customer")] };
        RenderOptions first = PdfInspect.Options() with { Signature = new PdfSignatureOptions(PdfSigners.FromCertificate(SignatureTests.Pki.Value.Ec)) { FieldName = "supplier" } };
        (byte[] supplierSigned, _) = await RenderTests.RenderAsync(contract, first);

        byte[] bothSigned = await SignAsync(supplierSigned, Options("customer"));

        SignedCms[] signatures = VerifyAll(bothSigned);
        Assert.Equal(2, signatures.Length);
        Assert.Equal(SignatureTests.Pki.Value.Ec.Thumbprint, signatures[0].SignerInfos[0].Certificate!.Thumbprint);
        Assert.Equal(SignatureTests.Pki.Value.Rsa.Thumbprint, signatures[1].SignerInfos[0].Certificate!.Thumbprint);
        string update = Encoding.Latin1.GetString(bothSigned[supplierSigned.Length..]);
        Assert.Matches(@"/T \(customer\) [^\n]*/V \d+ 0 R", update); // The field itself, given a value.
        Assert.DoesNotContain("/Rect [0 0 0 0]", update, StringComparison.Ordinal); // No extra invisible field.

        PdfException again = await Assert.ThrowsAsync<PdfException>(() => SignAsync(bothSigned, Options("supplier")));
        Assert.Contains("signed already", again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFieldSignedLaterShowsWhoSignedWithItsOwnEmbeddedFont()
    {
        PdfDocument contract = new("Contract") { Language = "en", Content = [new Paragraph("The parties agree."), new SignatureField("customer") { Label = "Signed for the customer" }] };
        (byte[] unsigned, _) = await RenderTests.RenderAsync(contract, PdfInspect.Options(PdfConformance.PdfUA1));

        PdfSignatureOptions options = Options("customer") with { AppearanceFont = Zorg.Pdf.Fonts.FontSource.FromBytes(Zorg.Pdf.Tests.Fonts.TestFonts.Require()) };
        byte[] signed = await SignAsync(unsigned, options);

        Assert.Single(VerifyAll(signed));
        string update = System.Text.Encoding.Latin1.GetString(signed[unsigned.Length..]);
        Assert.Matches(@"/T \(customer\) [^\n]*/AP <</N \d+ 0 R ?>>", update); // A new appearance on the field,
        Assert.Matches(@"/T \(customer\) [^\n]*/V \d+ 0 R", update); // and the signature as its value.
        Assert.Contains("/Subtype /Form /BBox [0 0 170.0", update, StringComparison.Ordinal);
        Assert.Contains("/FontFile2", update, StringComparison.Ordinal); // Its own subset, embedded in the update.
        Assert.DoesNotContain("/MCID", update, StringComparison.Ordinal); // No marked content in an appearance.
        PdfFileInfo info = await PdfFileInfo.ReadAsync(new MemoryStream(signed), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(info.Diagnostics);
    }

    [Fact]
    public async Task AFileWithAClassicTableGetsATableUpdate()
    {
        byte[] original = HandMadePdf();
        byte[] signed = await SignAsync(original, Options());

        Assert.Single(VerifyAll(signed));
        string update = Encoding.Latin1.GetString(signed[original.Length..]);
        Assert.Contains("\nxref\n", update, StringComparison.Ordinal);
        Assert.Contains("/Prev ", update, StringComparison.Ordinal);
        Assert.Contains("/AcroForm <</Fields [", update, StringComparison.Ordinal); // The catalog had none: added inline.
        PdfFileInfo info = await PdfFileInfo.ReadAsync(new MemoryStream(signed), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal((1, "Hand made"), (info.PageCount, info.Title));
        Assert.Empty(info.Diagnostics);
    }

    [Fact]
    public async Task ChangingAnyByteBreaksTheNewSignature()
    {
        byte[] signed = await SignAsync(HandMadePdf(), Options());
        long[] range = Ranges(signed).Last();
        foreach (long at in (long[])[20, range[1] - 2, range[2] + 2, signed.Length - 3])
        {
            byte[] tampered = (byte[])signed.Clone();
            tampered[at] ^= 0x01;
            Assert.ThrowsAny<CryptographicException>(() => Verify(tampered, range));
        }
    }

    [Fact]
    public async Task FilesThatCantTakeAnUpdateAreRefused()
    {
        byte[] damaged = Encoding.ASCII.GetBytes(Encoding.Latin1.GetString(HandMadePdf()).Replace("startxref", "startxrex", StringComparison.Ordinal));
        PdfException error = await Assert.ThrowsAsync<PdfException>(() => SignAsync(damaged, Options()));
        Assert.Contains("cross-reference data is damaged", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A minimal one-page PDF with a classic cross-reference table, offsets computed here.</summary>
    internal static byte[] HandMadePdf()
    {
        string[] objects =
        [
            "<</Type /Catalog /Pages 2 0 R>>",
            "<</Type /Pages /Kids [3 0 R] /Count 1>>",
            "<</Type /Page /Parent 2 0 R /MediaBox [0 0 595 842]>>",
            "<</Title (Hand made)>>",
        ];
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new long[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = text.Length;
            text.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        long xref = text.Length;
        text.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f\r\n");
        foreach (long offset in offsets)
        {
            text.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n\r\n");
        }

        text.Append(CultureInfo.InvariantCulture, $"trailer\n<</Size {objects.Length + 1} /Root 1 0 R /Info 4 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static long[][] Ranges(byte[] file) =>
        [.. ByteRange().Matches(Encoding.Latin1.GetString(file)).Select(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(n => long.Parse(n, CultureInfo.InvariantCulture)).ToArray())];

    /// <summary>Every signature, in file order, each checked over its own byte range.</summary>
    private static SignedCms[] VerifyAll(byte[] file) => [.. Ranges(file).Select(range => Verify(file, range))];

    private static SignedCms Verify(byte[] file, long[] range)
    {
        Assert.Equal(0, range[0]);
        byte[] signed = [.. file.AsSpan((int)range[0], (int)range[1]), .. file.AsSpan((int)range[2], (int)range[3])];
        var cms = new SignedCms(new ContentInfo(signed), detached: true);
        cms.Decode(Convert.FromHexString(Encoding.ASCII.GetString(file[(int)(range[1] + 1)..(int)(range[2] - 1)])));
        cms.CheckSignature(verifySignatureOnly: true);
        return cms;
    }

    [GeneratedRegex(@"/ByteRange \[([0-9 ]+)\]")]
    private static partial Regex ByteRange();
}
