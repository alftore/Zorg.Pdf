using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Pdf.Signing;

/// <summary>Built-in <see cref="IPdfSigner"/>s.</summary>
public static class PdfSigners
{
    /// <summary>
    /// Signs with a certificate's own private key, such as one loaded from a .pfx file or the certificate store. RSA
    /// keys sign with PKCS #1 v1.5 padding unless <paramref name="rsaPss"/> is set; EC keys sign with ECDSA.
    /// </summary>
    /// <param name="certificate">A certificate with its private key.</param>
    /// <param name="chain">Intermediate certificates to embed, so validators can reach a trusted root.</param>
    /// <param name="digestAlgorithm">SHA-256 (the default), SHA-384 or SHA-512.</param>
    /// <param name="rsaPss">Signs an RSA key with PSS padding instead of PKCS #1 v1.5.</param>
    /// <example>
    /// <code>
    /// using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile("signer.pfx", password);
    /// var options = new RenderOptions { Fonts = fonts, Signature = new PdfSignatureOptions(PdfSigners.FromCertificate(certificate)) };
    /// </code>
    /// </example>
    public static IPdfSigner FromCertificate(X509Certificate2 certificate, IEnumerable<X509Certificate2>? chain = null, HashAlgorithmName digestAlgorithm = default, bool rsaPss = false)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey)
        {
            throw new ArgumentException("The certificate has no private key. Load it with its key, for example from a .pfx file, or implement IPdfSigner for a key held elsewhere.", nameof(certificate));
        }

        HashAlgorithmName digest = digestAlgorithm.Name is null ? HashAlgorithmName.SHA256 : digestAlgorithm;
        if (!CmsSignature.IsSupportedDigest(digest))
        {
            throw new ArgumentException($"The digest {digest.Name} isn't supported. Use SHA256, SHA384 or SHA512.", nameof(digestAlgorithm));
        }

        PdfSignatureAlgorithm algorithm = certificate.GetKeyAlgorithm() switch
        {
            CmsSignature.RsaKey => rsaPss ? PdfSignatureAlgorithm.RsaPss : PdfSignatureAlgorithm.RsaPkcs1,
            CmsSignature.EcKey => PdfSignatureAlgorithm.Ecdsa,
            string other => throw new ArgumentException($"The certificate's key ({other}) isn't RSA or EC.", nameof(certificate)),
        };

        return new CertificateSigner(certificate, chain?.ToArray() ?? [], algorithm, digest);
    }

    private sealed class CertificateSigner(X509Certificate2 certificate, X509Certificate2[] chain, PdfSignatureAlgorithm algorithm, HashAlgorithmName digest) : IPdfSigner
    {
        public X509Certificate2 Certificate => certificate;

        public IReadOnlyList<X509Certificate2> Chain => chain;

        public PdfSignatureAlgorithm Algorithm => algorithm;

        public HashAlgorithmName DigestAlgorithm => digest;

        public ValueTask<byte[]> SignDigestAsync(ReadOnlyMemory<byte> hash, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (algorithm == PdfSignatureAlgorithm.Ecdsa)
            {
                using ECDsa ecdsa = certificate.GetECDsaPrivateKey()!;
                return ValueTask.FromResult(ecdsa.SignHash(hash.Span, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            }

            using RSA rsa = certificate.GetRSAPrivateKey()!;
            return ValueTask.FromResult(rsa.SignHash(hash.ToArray(), digest, algorithm == PdfSignatureAlgorithm.RsaPss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1));
        }
    }
}
