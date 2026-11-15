using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Signing;

#pragma warning disable IDE0059 // Unnecessary assignment of a value

namespace Zorg.Pdf.Tests.Guide;

/// <summary>signatures.md, with a stand-in for a key service client.</summary>
internal static partial class GuideSnippets
{
    // signatures.md
    public static async Task SignaturesAsync(PdfDocument document, FontCollection fonts, Stream output, string password, IEnumerable<X509Certificate2> intermediates, CancellationToken cancellationToken)
    {
        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile("signer.pfx", password);
        var options = new RenderOptions
        {
            Fonts = fonts,
            Signature = new PdfSignatureOptions(PdfSigners.FromCertificate(certificate, intermediates))
            {
                Reason = "Invoice issued",
                Location = "Oslo",
            },
        };
        await document.RenderAsync(output, options, cancellationToken);
    }

    // signatures.md
    public static PdfSignatureOptions Timestamped(IPdfSigner signer)
    {
        var signature = new PdfSignatureOptions(signer)
        {
            Timestamp = new Rfc3161TimestampClient(new Uri("http://timestamp.digicert.com")),
        };
        return signature;
    }

    // signatures.md
    public static PdfSignatureOptions VisibleFields(IPdfSigner signer)
    {
        var contract = new PdfDocument("Service agreement")
        {
            Content =
            [
                new Heading("Service agreement"),
                new Paragraph("Fjordlys AS and Nordlys Consulting agree to the terms below."),
                new SignatureField("supplier") { Label = "Signed for Fjordlys AS" },
                new SignatureField("customer") { Label = "Signed for Nordlys Consulting", Align = TextAlign.Right },
            ],
        };
        var signature = new PdfSignatureOptions(signer) { FieldName = "supplier" };
        return signature;
    }

    // signatures.md
    public static async Task SignExistingAsync(IPdfSigner signer, CancellationToken cancellationToken)
    {
        await using FileStream input = File.OpenRead("contract-signed-by-supplier.pdf");
        await using FileStream output = File.Create("contract-signed-by-both.pdf");
        await PdfSigning.SignAsync(input, output, new PdfSignatureOptions(signer) { FieldName = "customer" }, cancellationToken: cancellationToken);
    }

    /// <summary>Stands for a key service's client, such as Azure Key Vault's.</summary>
    public sealed class CryptographyClient
    {
        /// <summary>Signs a digest in the service.</summary>
        public Task<SignResult> SignAsync(SignatureAlgorithm algorithm, byte[] digest, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A key service's algorithm name.</summary>
    public enum SignatureAlgorithm
    {
        /// <summary>RSA PKCS #1 v1.5 with SHA-256.</summary>
        RS256,
    }

    /// <summary>A key service's answer.</summary>
    public sealed record SignResult(byte[] Signature);

    // signatures.md
    public sealed class KeyVaultSigner(CryptographyClient client, X509Certificate2 certificate) : IPdfSigner
    {
        public X509Certificate2 Certificate => certificate;
        public IReadOnlyList<X509Certificate2> Chain => [];
        public PdfSignatureAlgorithm Algorithm => PdfSignatureAlgorithm.RsaPkcs1;
        public HashAlgorithmName DigestAlgorithm => HashAlgorithmName.SHA256;
        public async ValueTask<byte[]> SignDigestAsync(ReadOnlyMemory<byte> digest, CancellationToken cancellationToken) =>
            (await client.SignAsync(SignatureAlgorithm.RS256, digest.ToArray(), cancellationToken)).Signature;
    }
}
