# Digital signatures

A signed PDF shows in Acrobat and other readers as signed by a named certificate and unchanged since. If one byte changes afterwards, the signature breaks. Zorg.Pdf signs documents as it renders them. Each one gets a PAdES baseline signature (ETSI EN 319 142-1), the kind eIDAS validators expect, over the whole file, in an invisible field or one you place on a page. Level B-B is the signature alone; add a timestamp and it's B-T.

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

`PdfSignatureOptions` also takes a `Name`, `ContactInfo`, the `FieldName` ("Signature1" by default) and a `SigningTime`. The time goes in the signature dictionary, as PAdES requires. It's the signer's own claim; a timestamp proves it.

## Visible signature fields

A `signatureField` block puts a box in the flow. The field whose name matches `FieldName` is signed in place. It shows who signed, when, and the reason and place, so a printed copy carries them too. Fields with other names stay empty, for someone to sign later in Acrobat or another signing tool:

```csharp
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
```

In JSON a field is `{ "type": "signatureField", "name": "customer", "label": "Signed for Nordlys Consulting" }`, so a template can lay out where signatures go while the key stays in code. A field is 60 × 20 mm unless you set `width` and `height`, and it moves to the next page if it doesn't fit. Its `label` is what screen readers announce and what an empty field shows. Each field is tagged as a Form element, and signed and empty fields both pass veraPDF as PDF/UA-1 and PDF/A-2a. Some viewers draw their own "sign here" mark over an empty field instead of its box.

If no field matches `FieldName`, the signature goes in an invisible field, as before.

## Timestamps (PAdES B-T)

A timestamp authority (TSA) signs the time it saw your signature, so the signing time is proven, not only claimed, and the signature stays valid after the certificate expires. Give `Timestamp` an RFC 3161 client with your TSA's URL. Trust service providers run them, and some are free:

```csharp
var signature = new PdfSignatureOptions(signer)
{
    Timestamp = new Rfc3161TimestampClient(new Uri("http://timestamp.digicert.com")),
};
```

Only a digest of the signature is sent. Zorg.Pdf sends a random nonce with it and checks that the token answers it: the request was granted, for the same digest, with the same nonce. It also bounds the response to 256 KB. Anything else stops the render with a `PdfException` that names the TSA. The token's own signature is for validators to check, as with any timestamp. Pass your own `HttpClient` for proxies, retries or a TSA that needs credentials, or implement `ITimestampClient` for another transport.

A token with the TSA's certificate adds 5 to 7 KB, which fits the default `ReservedSize`.

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

A remote signer or a timestamp needs `RenderAsync`. The synchronous `Render` throws `PdfException` if either doesn't complete at once.

## How it works

The file still streams. Zorg.Pdf hashes pages as they're written, and only the signature dictionary and the cross-reference section at the very end wait in memory. That dictionary leaves a hole of `ReservedSize` bytes (16 KB by default) for the signature. Once the rest of the file is known, the byte range is filled in, the file is hashed except for the hole, and the signature goes into the hole.

Before anything is written, the render checks the options and throws `ArgumentException` for a bad field name or a `ReservedSize` too small for the certificates. A signature that still doesn't fit throws `PdfException`, naming the size it needs.

Signed files are checked in the tests with .NET's `SignedCms` and `Rfc3161TimestampToken`, an independent CMS implementation, against tokens from a test TSA. Tampering with any covered byte must break them. By hand, signed and timestamped files (with a DigiCert timestamp) pass veraPDF as PDF/UA-1 and PDF/A-2a, and MuPDF reports them "unchanged since signing".

## What isn't there yet

- **An image in a signature's appearance**, such as a scanned handwritten signature.
- **Signing existing PDFs** and adding a second signature, by incremental update.
- **Long-term validation (B-LT, B-LTA):** revocation data and archive timestamps embedded in the file.

The key is only ever given in code, so a JSON document can't carry a signature, and the `zorg-pdf` tool doesn't sign.
