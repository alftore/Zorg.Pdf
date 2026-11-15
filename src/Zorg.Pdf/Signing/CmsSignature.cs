using System;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Pdf.Signing;

/// <summary>
/// The CMS <c>SignedData</c> (RFC 5652) of a PAdES baseline signature, built with <see cref="AsnWriter"/> so the key
/// can be anywhere and asynchronous: detached <c>id-data</c> content, one <c>SignerInfo</c> identified by issuer and
/// serial number, and the signed attributes PAdES B-B asks for (content type, message digest and
/// <c>signing-certificate-v2</c>). There is no <c>signing-time</c> attribute: PAdES puts the time in the signature
/// dictionary's <c>/M</c> instead (ETSI EN 319 142-1, 5.2).
/// </summary>
internal static class CmsSignature
{
    internal const string RsaKey = "1.2.840.113549.1.1.1";
    internal const string EcKey = "1.2.840.10045.2.1";
    private const string SignedData = "1.2.840.113549.1.7.2";
    private const string Data = "1.2.840.113549.1.7.1";
    private const string ContentType = "1.2.840.113549.1.9.3";
    private const string MessageDigest = "1.2.840.113549.1.9.4";
    private const string SigningCertificateV2 = "1.2.840.113549.1.9.16.2.47";
    private const string RsaPss = "1.2.840.113549.1.1.10";
    private const string Mgf1 = "1.2.840.113549.1.1.8";
    private const string SignatureTimeStampToken = "1.2.840.113549.1.9.16.2.14";

    public static bool IsSupportedDigest(HashAlgorithmName digest) =>
        digest == HashAlgorithmName.SHA256 || digest == HashAlgorithmName.SHA384 || digest == HashAlgorithmName.SHA512;

    /// <summary>Builds the signed attributes, has the signer sign them, and returns the DER <c>ContentInfo</c>.</summary>
    /// <param name="signer">The key.</param>
    /// <param name="documentDigest">The digest of the signed byte ranges, made with the signer's digest algorithm.</param>
    /// <param name="timestamp">Gets a timestamp token for the signature value (PAdES B-T), or null for B-B.</param>
    /// <param name="cancellationToken">Passed to the signer and the timestamp client.</param>
    public static async ValueTask<byte[]> CreateAsync(IPdfSigner signer, byte[] documentDigest, ITimestampClient? timestamp, CancellationToken cancellationToken)
    {
        HashAlgorithmName digest = signer.DigestAlgorithm;
        byte[] attributes = SignedAttributes(signer.Certificate, documentDigest);
        byte[] attributesDigest = CryptographicOperations.HashData(digest, attributes);
        byte[] signature = await signer.SignDigestAsync(attributesDigest, cancellationToken).ConfigureAwait(false);
        if (signer.Algorithm == PdfSignatureAlgorithm.Ecdsa)
        {
            signature = EcdsaToDer(signature);
        }

        byte[]? token = null;
        if (timestamp is not null)
        {
            // The token covers the signature value (RFC 3161, appendix A), hashed with the signer's digest.
            token = await timestamp.GetTokenAsync(CryptographicOperations.HashData(digest, signature), digest, cancellationToken).ConfigureAwait(false);
        }

        return Build(signer, attributes, signature, token);
    }

    /// <summary>The signed attributes as a DER <c>SET OF Attribute</c>: what the signature covers.</summary>
    internal static byte[] SignedAttributes(X509Certificate2 certificate, byte[] documentDigest)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSetOf();

        writer.PushSequence();
        writer.WriteObjectIdentifier(ContentType);
        writer.PushSetOf();
        writer.WriteObjectIdentifier(Data);
        writer.PopSetOf();
        writer.PopSequence();

        writer.PushSequence();
        writer.WriteObjectIdentifier(MessageDigest);
        writer.PushSetOf();
        writer.WriteOctetString(documentDigest);
        writer.PopSetOf();
        writer.PopSequence();

        // SigningCertificateV2 (RFC 5035): binds the signature to this certificate, not only to its key. The hash
        // is SHA-256, the default, so its algorithm is left out.
        writer.PushSequence();
        writer.WriteObjectIdentifier(SigningCertificateV2);
        writer.PushSetOf();
        writer.PushSequence(); // SigningCertificateV2
        writer.PushSequence(); // certs
        writer.PushSequence(); // ESSCertIDv2
        writer.WriteOctetString(SHA256.HashData(certificate.RawData));
        writer.PushSequence(); // IssuerSerial
        writer.PushSequence(); // GeneralNames
        Asn1Tag directoryName = new(TagClass.ContextSpecific, 4, isConstructed: true);
        writer.PushSequence(directoryName);
        writer.WriteEncodedValue(certificate.IssuerName.RawData);
        writer.PopSequence(directoryName);
        writer.PopSequence();
        writer.WriteInteger(certificate.SerialNumberBytes.Span);
        writer.PopSequence();
        writer.PopSequence();
        writer.PopSequence();
        writer.PopSequence();
        writer.PopSetOf();
        writer.PopSequence();

        writer.PopSetOf();
        return writer.Encode();
    }

    private static byte[] Build(IPdfSigner signer, byte[] attributes, byte[] signature, byte[]? timestampToken)
    {
        X509Certificate2 certificate = signer.Certificate;
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence(); // ContentInfo
        writer.WriteObjectIdentifier(SignedData);
        Asn1Tag explicit0 = new(TagClass.ContextSpecific, 0, isConstructed: true);
        writer.PushSequence(explicit0);
        writer.PushSequence(); // SignedData
        writer.WriteInteger(1);
        writer.PushSetOf();
        DigestAlgorithm(writer, signer.DigestAlgorithm);
        writer.PopSetOf();
        writer.PushSequence(); // EncapsulatedContentInfo: detached, so no content.
        writer.WriteObjectIdentifier(Data);
        writer.PopSequence();

        Asn1Tag implicit0 = new(TagClass.ContextSpecific, 0, isConstructed: true);
        writer.PushSetOf(implicit0);
        writer.WriteEncodedValue(certificate.RawData);
        foreach (X509Certificate2 other in signer.Chain)
        {
            if (!other.RawDataMemory.Span.SequenceEqual(certificate.RawDataMemory.Span))
            {
                writer.WriteEncodedValue(other.RawData);
            }
        }

        writer.PopSetOf(implicit0);

        writer.PushSetOf(); // SignerInfos
        writer.PushSequence(); // SignerInfo
        writer.WriteInteger(1);
        writer.PushSequence(); // IssuerAndSerialNumber
        writer.WriteEncodedValue(certificate.IssuerName.RawData);
        writer.WriteInteger(certificate.SerialNumberBytes.Span);
        writer.PopSequence();
        DigestAlgorithm(writer, signer.DigestAlgorithm);

        // The same attributes, tagged [0] IMPLICIT instead of SET (RFC 5652, 5.4).
        byte[] tagged = (byte[])attributes.Clone();
        tagged[0] = 0xA0;
        writer.WriteEncodedValue(tagged);
        SignatureAlgorithm(writer, signer.Algorithm, signer.DigestAlgorithm);
        writer.WriteOctetString(signature);
        if (timestampToken is not null)
        {
            // unsignedAttrs [1] IMPLICIT: id-aa-signatureTimeStampToken (RFC 3161, appendix A).
            Asn1Tag implicit1 = new(TagClass.ContextSpecific, 1, isConstructed: true);
            writer.PushSetOf(implicit1);
            writer.PushSequence();
            writer.WriteObjectIdentifier(SignatureTimeStampToken);
            writer.PushSetOf();
            writer.WriteEncodedValue(timestampToken);
            writer.PopSetOf();
            writer.PopSequence();
            writer.PopSetOf(implicit1);
        }

        writer.PopSequence();
        writer.PopSetOf();

        writer.PopSequence();
        writer.PopSequence(explicit0);
        writer.PopSequence();
        return writer.Encode();
    }

    internal static string DigestOid(HashAlgorithmName digest) =>
        digest == HashAlgorithmName.SHA384 ? "2.16.840.1.101.3.4.2.2"
        : digest == HashAlgorithmName.SHA512 ? "2.16.840.1.101.3.4.2.3"
        : "2.16.840.1.101.3.4.2.1";

    /// <summary>A SHA-2 algorithm identifier, parameters absent (RFC 5754, 2).</summary>
    private static void DigestAlgorithm(AsnWriter writer, HashAlgorithmName digest)
    {
        writer.PushSequence();
        writer.WriteObjectIdentifier(DigestOid(digest));
        writer.PopSequence();
    }

    private static void SignatureAlgorithm(AsnWriter writer, PdfSignatureAlgorithm algorithm, HashAlgorithmName digest)
    {
        int index = digest == HashAlgorithmName.SHA384 ? 1 : digest == HashAlgorithmName.SHA512 ? 2 : 0;
        writer.PushSequence();
        switch (algorithm)
        {
            case PdfSignatureAlgorithm.Ecdsa:
                writer.WriteObjectIdentifier(index switch { 1 => "1.2.840.10045.4.3.3", 2 => "1.2.840.10045.4.3.4", _ => "1.2.840.10045.4.3.2" });
                break;
            case PdfSignatureAlgorithm.RsaPss:
                // RSASSA-PSS-params (RFC 4055, 3.1): the digest, MGF1 with the same digest, a salt as long as it.
                writer.WriteObjectIdentifier(RsaPss);
                writer.PushSequence();
                Asn1Tag tag0 = new(TagClass.ContextSpecific, 0, isConstructed: true);
                writer.PushSequence(tag0);
                DigestAlgorithm(writer, digest);
                writer.PopSequence(tag0);
                Asn1Tag tag1 = new(TagClass.ContextSpecific, 1, isConstructed: true);
                writer.PushSequence(tag1);
                writer.PushSequence();
                writer.WriteObjectIdentifier(Mgf1);
                DigestAlgorithm(writer, digest);
                writer.PopSequence();
                writer.PopSequence(tag1);
                Asn1Tag tag2 = new(TagClass.ContextSpecific, 2, isConstructed: true);
                writer.PushSequence(tag2);
                writer.WriteInteger(32 + (16 * index));
                writer.PopSequence(tag2);
                writer.PopSequence();
                break;
            default:
                writer.WriteObjectIdentifier(index switch { 1 => "1.2.840.113549.1.1.12", 2 => "1.2.840.113549.1.1.13", _ => "1.2.840.113549.1.1.11" });
                writer.WriteNull();
                break;
        }

        writer.PopSequence();
    }

    /// <summary>Whether the bytes are exactly a DER SEQUENCE of two positive INTEGERs, as an ECDSA signature in DER is.</summary>
    private static bool IsDerSignature(byte[] value)
    {
        if (value.Length < 8 || value[0] != 0x30)
        {
            return false;
        }

        try
        {
            var reader = new AsnReader(value, AsnEncodingRules.DER);
            AsnReader sequence = reader.ReadSequence();
            bool positive = sequence.ReadInteger().Sign > 0 && sequence.ReadInteger().Sign > 0;
            return positive && !sequence.HasData && !reader.HasData;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    /// <summary>r and s, each half of an IEEE P1363 signature, as the DER <c>Ecdsa-Sig-Value</c> CMS carries.</summary>
    private static byte[] EcdsaToDer(byte[] p1363)
    {
        if (IsDerSignature(p1363))
        {
            return p1363; // Some services (AWS KMS) already return the DER Ecdsa-Sig-Value.
        }

        if (p1363.Length == 0 || p1363.Length % 2 != 0)
        {
            throw new PdfException("The signer returned an ECDSA signature that isn't r and s of equal length (IEEE P1363).");
        }

        int half = p1363.Length / 2;
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.WriteIntegerUnsigned(Trim(p1363.AsSpan(0, half)));
        writer.WriteIntegerUnsigned(Trim(p1363.AsSpan(half)));
        writer.PopSequence();
        return writer.Encode();

        static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
        {
            int start = 0;
            while (start < value.Length - 1 && value[start] == 0)
            {
                start++;
            }

            return value[start..];
        }
    }
}
