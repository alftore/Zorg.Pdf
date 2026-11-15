using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Pdf.Signing;

/// <summary>
/// Signs a digest with a private key: a key in this process (<see cref="PdfSigners.FromCertificate"/>), or one in an
/// HSM, Azure Key Vault or a signing service. Zorg.Pdf builds the CMS signature around it and never sees the key.
/// </summary>
/// <example>
/// A remote key: the service signs the digest, the certificate is fetched once.
/// <code>
/// public sealed class KeyVaultSigner(CryptographyClient client, X509Certificate2 certificate) : IPdfSigner
/// {
///     public X509Certificate2 Certificate => certificate;
///     public IReadOnlyList&lt;X509Certificate2&gt; Chain => [];
///     public PdfSignatureAlgorithm Algorithm => PdfSignatureAlgorithm.RsaPkcs1;
///     public HashAlgorithmName DigestAlgorithm => HashAlgorithmName.SHA256;
///     public async ValueTask&lt;byte[]&gt; SignDigestAsync(ReadOnlyMemory&lt;byte&gt; digest, CancellationToken cancellationToken) =>
///         (await client.SignAsync(SignatureAlgorithm.RS256, digest.ToArray(), cancellationToken)).Signature;
/// }
/// </code>
/// </example>
public interface IPdfSigner
{
    /// <summary>The signer's certificate. Only its public part is used and embedded.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>
    /// Other certificates to embed so a validator can build the path to a trusted root, usually the intermediates.
    /// The signer's certificate needn't be repeated here.
    /// </summary>
    public IReadOnlyList<X509Certificate2> Chain { get; }

    /// <summary>How the key signs: RSA with PKCS #1 v1.5 or PSS padding, or ECDSA.</summary>
    public PdfSignatureAlgorithm Algorithm { get; }

    /// <summary>The digest: SHA-256, SHA-384 or SHA-512. The file and the signed attributes are hashed with it.</summary>
    public HashAlgorithmName DigestAlgorithm { get; }

    /// <summary>
    /// Signs a digest made with <see cref="DigestAlgorithm"/>. Returns the raw signature: for RSA the signature
    /// bytes, for ECDSA r and s as fixed-width big-endian integers (IEEE P1363), as key services return them.
    /// </summary>
    /// <param name="digest">The digest of the CMS signed attributes.</param>
    /// <param name="cancellationToken">Cancels the call to the key.</param>
    public ValueTask<byte[]> SignDigestAsync(ReadOnlyMemory<byte> digest, CancellationToken cancellationToken);
}

/// <summary>How an <see cref="IPdfSigner"/>'s key signs.</summary>
public enum PdfSignatureAlgorithm
{
    /// <summary>RSA with PKCS #1 v1.5 padding: the most widely supported.</summary>
    RsaPkcs1,

    /// <summary>RSA with PSS padding, the salt as long as the digest.</summary>
    RsaPss,

    /// <summary>ECDSA on the certificate's curve (P-256, P-384 or P-521).</summary>
    Ecdsa,
}
