# Digital signatures

A signed PDF shows in Acrobat and other readers as signed by a named certificate and unchanged since. If one byte changes afterwards, the signature breaks. Zorg.Pdf signs documents as it renders them. Each one gets a PAdES baseline B-B signature (ETSI EN 319 142-1), the kind eIDAS validators expect, over the whole file in an invisible signature field.

## Sign with a certificate

`PdfSigners.FromCertificate` signs with a certificate's own private key, such as one loaded from a `.pfx` file:

```csharp
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
```

RSA keys sign with PKCS #1 v1.5 padding, or with PSS if you pass `rsaPss: true`. EC keys sign with ECDSA. The digest is SHA-256 unless you choose SHA-384 or SHA-512. Pass the intermediate certificates too, so a validator can build the path to a root it trusts. The signer's own certificate is always embedded.

`PdfSignatureOptions` also takes a `Name`, `ContactInfo`, the `FieldName` ("Signature1" by default) and a `SigningTime`. The time goes in the signature dictionary, as PAdES requires. It's the signer's own claim; only a timestamp from a timestamp authority proves it, and that comes with B-T signatures in a later version.

## Keys in an HSM or a key vault

A key that never leaves Azure Key Vault, an HSM or a signing service needs only an `IPdfSigner`. Zorg.Pdf hashes the document, builds the CMS structure around the signature and asks the signer to sign one digest. The call is asynchronous, so the key service can be remote:

```csharp
public sealed class KeyVaultSigner(CryptographyClient client, X509Certificate2 certificate) : IPdfSigner
{
    public X509Certificate2 Certificate => certificate;
    public IReadOnlyList<X509Certificate2> Chain => [];
    public PdfSignatureAlgorithm Algorithm => PdfSignatureAlgorithm.RsaPkcs1;
    public HashAlgorithmName DigestAlgorithm => HashAlgorithmName.SHA256;
    public async ValueTask<byte[]> SignDigestAsync(ReadOnlyMemory<byte> digest, CancellationToken cancellationToken) =>
        (await client.SignAsync(SignatureAlgorithm.RS256, digest.ToArray(), cancellationToken)).Signature;
}
```

`CryptographyClient` here stands for your key service's client (Azure.Security.KeyVault.Keys has one with this shape). Zorg.Pdf has no dependency on any of them. An ECDSA signer returns r and s side by side (IEEE P1363), which is what key services return.

A remote signer needs `RenderAsync`. The synchronous `Render` throws `PdfException` if the signer doesn't complete at once.

## How it works

The file still streams. Zorg.Pdf hashes pages as they're written, and only the signature dictionary and the cross-reference section at the very end wait in memory. That dictionary leaves a hole of `ReservedSize` bytes (16 KB by default) for the signature. Once the rest of the file is known, the byte range is filled in, the file is hashed except for the hole, and the signature goes into the hole.

Before anything is written, the render checks the options and throws `ArgumentException` for a bad field name or a `ReservedSize` too small for the certificates. A signature that still doesn't fit throws `PdfException`, naming the size it needs.

Signed files are checked in the tests with .NET's `SignedCms` (an independent CMS implementation). Tampering with any covered byte must break them. By hand, they pass veraPDF as PDF/UA-1 and PDF/A-2a, and MuPDF reports them "unchanged since signing".

## What isn't there yet

- **Timestamps (PAdES B-T):** an RFC 3161 timestamp from a timestamp authority, so the signing time is proven.
- **Visible signatures:** a signature field with an appearance (name, date, image) on a page, and empty fields for others to sign later.
- **Signing existing PDFs** and adding a second signature, by incremental update.
- **Long-term validation (B-LT, B-LTA):** revocation data and archive timestamps embedded in the file.

The key is only ever given in code, so a JSON document can't carry a signature, and the `zorg-pdf` tool doesn't sign.
