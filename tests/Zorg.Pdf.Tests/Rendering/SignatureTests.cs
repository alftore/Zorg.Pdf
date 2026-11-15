using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Signing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>
/// PAdES B-B signatures on new documents. <see cref="SignedCms"/> from System.Security.Cryptography.Pkcs checks them:
/// it isn't shipped, so it's an independent oracle for the CMS Zorg.Pdf builds with AsnWriter.
/// </summary>
public sealed partial class SignatureTests
{
    private static readonly DateTimeOffset SigningTime = new(2026, 11, 15, 10, 30, 0, TimeSpan.FromHours(1));
    private static readonly Lazy<(X509Certificate2 Root, X509Certificate2 Intermediate, X509Certificate2 Rsa, X509Certificate2 Ec)> Pki = new(CreatePki);

    private static PdfDocument Document() => new("Signed invoice") { Language = "en", Content = [new Heading("Invoice 1042"), new Paragraph("Total due: 1 250,00 NOK.")] };

    private static RenderOptions Options(IPdfSigner signer, PdfConformance conformance = PdfConformance.None) => PdfInspect.Options(conformance) with
    {
        Signature = new PdfSignatureOptions(signer) { Reason = "Invoice issued", Location = "Oslo", ContactInfo = "billing@example.com", SigningTime = SigningTime },
    };

    [Fact]
    public async Task ARsaSignatureCoversTheWholeFileButItsContents()
    {
        IPdfSigner signer = PdfSigners.FromCertificate(Pki.Value.Rsa, [Pki.Value.Intermediate]);
        (byte[] file, _) = await RenderTests.RenderAsync(Document(), Options(signer));

        (long[] range, byte[] signature) = Signature(file);
        Assert.Equal(0, range[0]);
        Assert.Equal(file.Length, range[2] + range[3]);
        Assert.Equal((byte)'<', file[range[1]]);
        Assert.Equal((byte)'>', file[range[2] - 1]);

        SignedCms cms = Verify(file);
        Assert.Equal(Pki.Value.Rsa.Thumbprint, cms.SignerInfos[0].Certificate!.Thumbprint);
        Assert.Contains(cms.Certificates.Cast<X509Certificate2>(), c => c.Thumbprint == Pki.Value.Intermediate.Thumbprint);
        Assert.Equal("1.2.840.113549.1.1.11", cms.SignerInfos[0].SignatureAlgorithm.Value); // sha256WithRSAEncryption
        Assert.True(signature.Length < PdfSignatureOptions.DefaultReservedSize);

        string text = Encoding.Latin1.GetString(file);
        Assert.Contains("/Type /Sig /Filter /Adobe.PPKLite /SubFilter /ETSI.CAdES.detached", text, StringComparison.Ordinal);
        Assert.Contains("/M (D:20261115103000+01'00') /Reason (Invoice issued) /Location (Oslo) /ContactInfo (billing@example.com)", text, StringComparison.Ordinal);
        string flat = PdfInspect.Text(file);
        Assert.Contains("/AcroForm <</Fields [", flat, StringComparison.Ordinal);
        Assert.Contains("/SigFlags 3", flat, StringComparison.Ordinal);
        Assert.Contains("/Subtype /Widget /FT /Sig /T (Signature1)", flat, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSignedAttributesArePadesBaseline()
    {
        (byte[] file, _) = await RenderTests.RenderAsync(Document(), Options(PdfSigners.FromCertificate(Pki.Value.Rsa)));
        SignerInfo signer = Verify(file).SignerInfos[0];

        string[] attributes = [.. signer.SignedAttributes.Cast<CryptographicAttributeObject>().Select(a => a.Oid.Value!)];
        Assert.Contains("1.2.840.113549.1.9.3", attributes); // content-type
        Assert.Contains("1.2.840.113549.1.9.4", attributes); // message-digest
        Assert.Contains("1.2.840.113549.1.9.16.2.47", attributes); // signing-certificate-v2
        Assert.DoesNotContain("1.2.840.113549.1.9.5", attributes); // No signing-time: PAdES has /M instead.
        Assert.Equal(SubjectIdentifierType.IssuerAndSerialNumber, signer.SignerIdentifier.Type);

        // The signing-certificate-v2 hash is the signer's certificate.
        byte[] attribute = signer.SignedAttributes.Cast<CryptographicAttributeObject>().Single(a => a.Oid.Value == "1.2.840.113549.1.9.16.2.47").Values[0].RawData;
        Assert.True(attribute.AsSpan().IndexOf(SHA256.HashData(Pki.Value.Rsa.RawData)) > 0);
    }

    [Theory]
    [InlineData("ecdsa", "SHA256", "1.2.840.10045.4.3.2")]
    [InlineData("ecdsa", "SHA384", "1.2.840.10045.4.3.3")]
    [InlineData("pss", "SHA256", "1.2.840.113549.1.1.10")]
    [InlineData("rsa", "SHA512", "1.2.840.113549.1.1.13")]
    public async Task OtherAlgorithmsVerify(string key, string digest, string algorithm)
    {
        IPdfSigner signer = PdfSigners.FromCertificate(key == "ecdsa" ? Pki.Value.Ec : Pki.Value.Rsa, digestAlgorithm: new HashAlgorithmName(digest), rsaPss: key == "pss");
        (byte[] file, _) = await RenderTests.RenderAsync(Document(), Options(signer));

        SignedCms cms = Verify(file);
        Assert.Equal(algorithm, cms.SignerInfos[0].SignatureAlgorithm.Value);
        Assert.Equal(digest switch { "SHA384" => "2.16.840.1.101.3.4.2.2", "SHA512" => "2.16.840.1.101.3.4.2.3", _ => "2.16.840.1.101.3.4.2.1" }, cms.SignerInfos[0].DigestAlgorithm.Value);
    }

    [Fact]
    public async Task ChangingAnyCoveredByteBreaksTheSignature()
    {
        (byte[] file, _) = await RenderTests.RenderAsync(Document(), Options(PdfSigners.FromCertificate(Pki.Value.Rsa)));
        (long[] range, _) = Signature(file);

        foreach (long at in (long[])[100, range[1] - 2, range[2] + 2, file.Length - 3])
        {
            byte[] tampered = (byte[])file.Clone();
            tampered[at] ^= 0x01;
            Assert.ThrowsAny<CryptographicException>(() => Verify(tampered, range));
        }
    }

    [Fact]
    public async Task RsaSignaturesAreDeterministicAndTheSyncApiSignsToo()
    {
        RenderOptions options = Options(PdfSigners.FromCertificate(Pki.Value.Rsa));
        (byte[] first, _) = await RenderTests.RenderAsync(Document(), options);
        (byte[] second, _) = await RenderTests.RenderAsync(Document(), options);

        Assert.Equal(first, second);
        Assert.Equal(first, RenderSync(options));
    }

    private static byte[] RenderSync(RenderOptions options)
    {
        using var output = new MemoryStream();
        Document().Render(output, options);
        return output.ToArray();
    }

    [Fact]
    public async Task ARemoteSignerWorksAsynchronouslyButNotWithTheSyncApi()
    {
        var remote = new RemoteSigner(PdfSigners.FromCertificate(Pki.Value.Ec));
        (byte[] file, _) = await RenderTests.RenderAsync(Document(), Options(remote));

        Verify(file);
        Assert.Equal(1, remote.Calls);
        PdfException error = Assert.Throws<PdfException>(() => RenderSync(Options(remote)));
        Assert.Contains("RenderAsync", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MistakesAreCaughtBeforeAnythingIsWritten()
    {
        IPdfSigner signer = PdfSigners.FromCertificate(Pki.Value.Rsa, [Pki.Value.Intermediate, Pki.Value.Root]);
        using var output = new MemoryStream();

        RenderOptions small = Options(signer) with { Signature = new PdfSignatureOptions(signer) { ReservedSize = 2048 } };
        ArgumentException tooSmall = await Assert.ThrowsAsync<ArgumentException>(async () => await Document().RenderAsync(output, small, TestContext.Current.CancellationToken));
        Assert.Contains("ReservedSize", tooSmall.Message, StringComparison.Ordinal);

        RenderOptions dotted = Options(signer) with { Signature = new PdfSignatureOptions(signer) { FieldName = "a.b" } };
        await Assert.ThrowsAsync<ArgumentException>(async () => await Document().RenderAsync(output, dotted, TestContext.Current.CancellationToken));
        Assert.Equal(0, output.Length);

        using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(Pki.Value.Rsa.RawData);
        Assert.Throws<ArgumentException>(() => PdfSigners.FromCertificate(publicOnly));
        Assert.Throws<ArgumentException>(() => PdfSigners.FromCertificate(Pki.Value.Rsa, digestAlgorithm: HashAlgorithmName.SHA1));
    }

    [Fact]
    public async Task ASignatureThatOutgrowsItsRoomSaysHowMuchItNeeds()
    {
        var bloated = new RemoteSigner(PdfSigners.FromCertificate(Pki.Value.Rsa)) { Padding = 4096 };
        RenderOptions options = Options(bloated) with { Signature = new PdfSignatureOptions(bloated) { ReservedSize = 4096 } };

        PdfException error = await Assert.ThrowsAsync<PdfException>(async () => await Document().RenderAsync(new MemoryStream(), options, TestContext.Current.CancellationToken));
        Assert.Contains("Set ReservedSize to at least", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASignedPdfUaDocumentKeepsItsOtherParts()
    {
        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(Document(), Options(PdfSigners.FromCertificate(Pki.Value.Rsa), PdfConformance.PdfUA1));

        Verify(file);
        Assert.All(result.Diagnostics, d => Assert.Equal("ZPDF-F003", d.Code)); // Only the test font's synthetic bold.
        PdfFileInfo info = await PdfFileInfo.ReadAsync(new MemoryStream(file), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal((1, "Signed invoice", true), (info.PageCount, info.Title, info.IsTagged));
        Assert.Empty(info.Diagnostics); // The reader finds every object where the cross-reference stream says.
    }

    /// <summary>The byte range and the signature's bytes.</summary>
    internal static (long[] Range, byte[] Signature) Signature(byte[] file)
    {
        string text = Encoding.Latin1.GetString(file);
        Match match = ByteRange().Match(text);
        Assert.True(match.Success, "No /ByteRange.");
        long[] range = [.. match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(n => long.Parse(n, CultureInfo.InvariantCulture))];
        byte[] hole = Convert.FromHexString(text.AsSpan((int)range[1] + 1, (int)(range[2] - range[1] - 2)));
        AsnDecoder.ReadEncodedValue(hole, AsnEncodingRules.DER, out _, out _, out int length); // The rest is zero padding.
        return (range, hole[..length]);
    }

    private static SignedCms Verify(byte[] file) => Verify(file, Signature(file).Range);

    private static SignedCms Verify(byte[] file, long[] range)
    {
        byte[] signed = [.. file.AsSpan((int)range[0], (int)range[1]), .. file.AsSpan((int)range[2], (int)range[3])];
        byte[] hex = file[(int)(range[1] + 1)..(int)(range[2] - 1)];
        var cms = new SignedCms(new ContentInfo(signed), detached: true);
        cms.Decode(Convert.FromHexString(Encoding.ASCII.GetString(hex)));
        cms.CheckSignature(verifySignatureOnly: true);
        return cms;
    }

    [GeneratedRegex(@"/ByteRange \[([0-9 ]+)\]")]
    private static partial Regex ByteRange();

    /// <summary>A test PKI made in the run: no key is ever committed.</summary>
    private static (X509Certificate2, X509Certificate2, X509Certificate2, X509Certificate2) CreatePki()
    {
        DateTimeOffset from = DateTimeOffset.UtcNow.AddDays(-1), to = DateTimeOffset.UtcNow.AddYears(1);
        using RSA rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Zorg.Pdf Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        X509Certificate2 root = rootRequest.CreateSelfSigned(from, to);

        using RSA intermediateKey = RSA.Create(2048);
        var intermediateRequest = new CertificateRequest("CN=Zorg.Pdf Test Intermediate", intermediateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 intermediatePublic = intermediateRequest.Create(root, from, to, [1, 2, 3, 4]);
        X509Certificate2 intermediate = intermediatePublic.CopyWithPrivateKey(intermediateKey);

        RSA rsaKey = RSA.Create(2048);
        var rsaRequest = new CertificateRequest("CN=Kari Nordmann, O=Fjordlys AS", rsaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rsaRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using X509Certificate2 rsaPublic = rsaRequest.Create(intermediate, from, to, [5, 6, 7, 8]);

        ECDsa ecKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ecRequest = new CertificateRequest("CN=Ola Nordmann, O=Fjordlys AS", ecKey, HashAlgorithmName.SHA256);
        ecRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using X509Certificate2 ecPublic = ecRequest.Create(intermediate.SubjectName, X509SignatureGenerator.CreateForRSA(intermediateKey, RSASignaturePadding.Pkcs1), from, to, [0x80, 9, 10, 11]); // A serial with the high bit set.

        return (root, intermediate, rsaPublic.CopyWithPrivateKey(rsaKey), ecPublic.CopyWithPrivateKey(ecKey));
    }

    /// <summary>A key behind a network call: completes asynchronously, as Key Vault or an HSM service would.</summary>
    private sealed class RemoteSigner(IPdfSigner inner) : IPdfSigner
    {
        public int Calls { get; private set; }

        public int Padding { get; init; }

        public X509Certificate2 Certificate => inner.Certificate;

        public IReadOnlyList<X509Certificate2> Chain => inner.Chain;

        public PdfSignatureAlgorithm Algorithm => inner.Algorithm;

        public HashAlgorithmName DigestAlgorithm => inner.DigestAlgorithm;

        public async ValueTask<byte[]> SignDigestAsync(ReadOnlyMemory<byte> digest, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Calls++;
            byte[] signature = await inner.SignDigestAsync(digest, cancellationToken);
            return Padding == 0 ? signature : [.. signature, .. new byte[Padding]];
        }
    }
}
