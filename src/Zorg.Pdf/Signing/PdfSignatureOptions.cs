using System;

namespace Zorg.Pdf.Signing;

/// <summary>
/// Signs a document as it's rendered: a PAdES baseline signature (ETSI EN 319 142-1) over the whole file, in an
/// invisible signature field: B-B, or B-T with a <see cref="Timestamp"/>. Readers show it as signed, and as changed
/// if a byte is altered afterwards.
/// </summary>
/// <param name="Signer">Signs the digest: <see cref="PdfSigners.FromCertificate"/>, or a key in an HSM or a key vault.</param>
/// <example>
/// <code>
/// var options = new RenderOptions
/// {
///     Fonts = fonts,
///     Signature = new PdfSignatureOptions(PdfSigners.FromCertificate(certificate)) { Reason = "Invoice issued", Location = "Oslo" },
/// };
/// await document.RenderAsync(output, options, cancellationToken);
/// </code>
/// </example>
public sealed record PdfSignatureOptions(IPdfSigner Signer)
{
    /// <summary>The default <see cref="ReservedSize"/>: room for a certificate chain of a few certificates.</summary>
    public const int DefaultReservedSize = 16 * 1024;

    /// <summary>The signature field's name. "Signature1" by default.</summary>
    public string FieldName { get; init; } = "Signature1";

    /// <summary>Who signed, as readers show it. Null: readers take the name from the certificate.</summary>
    public string? Name { get; init; }

    /// <summary>Why the document is signed, such as "Invoice issued".</summary>
    public string? Reason { get; init; }

    /// <summary>Where it was signed, such as "Oslo".</summary>
    public string? Location { get; init; }

    /// <summary>How to reach the signer, such as an email address.</summary>
    public string? ContactInfo { get; init; }

    /// <summary>
    /// The signing time the signature claims, in the signature dictionary as PAdES requires. Null: the current time.
    /// It's the signer's claim; a timestamp from a timestamp authority is what proves it.
    /// </summary>
    public DateTimeOffset? SigningTime { get; init; }

    /// <summary>
    /// Gets a timestamp from a timestamp authority for the signature, making it PAdES B-T: the signing time is then
    /// proven, not only claimed. Null (the default): B-B. See <see cref="Rfc3161TimestampClient"/>.
    /// </summary>
    public ITimestampClient? Timestamp { get; init; }

    /// <summary>
    /// Bytes reserved in the file for the signature, which is written after the rest of the file is hashed. A
    /// signature that doesn't fit stops the render with a message naming the size it needs.
    /// </summary>
    public int ReservedSize { get; init; } = DefaultReservedSize;
}
