using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Pdf.Signing;

/// <summary>
/// Gets a timestamp token for a signature from a timestamp authority, which turns a PAdES B-B signature into B-T: the
/// signing time is then proven by a third party, not only claimed.
/// </summary>
public interface ITimestampClient
{
    /// <summary>Returns an RFC 3161 <c>TimeStampToken</c> (a DER CMS <c>ContentInfo</c>) for a digest.</summary>
    /// <param name="digest">The digest of the signature value.</param>
    /// <param name="digestAlgorithm">How <paramref name="digest"/> was made: SHA-256, SHA-384 or SHA-512.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public ValueTask<byte[]> GetTokenAsync(ReadOnlyMemory<byte> digest, HashAlgorithmName digestAlgorithm, CancellationToken cancellationToken);
}

/// <summary>
/// An RFC 3161 timestamp client over HTTP: posts a <c>TimeStampReq</c> with a random nonce and checks that the token
/// answers it (granted, the same digest, the same nonce). The token's own signature is the validator's to check, as
/// with any timestamp.
/// </summary>
/// <param name="url">The timestamp authority's URL, such as your trust service provider's.</param>
/// <param name="httpClient">The client to send with. Null: one shared by every instance.</param>
/// <example>
/// <code>
/// var signature = new PdfSignatureOptions(signer) { Timestamp = new Rfc3161TimestampClient(new Uri("https://tsa.example.com")) };
/// </code>
/// </example>
public sealed class Rfc3161TimestampClient(Uri url, HttpClient? httpClient = null) : ITimestampClient
{
    /// <summary>The largest response read: tokens with a certificate chain are a few kilobytes.</summary>
    internal const int MaxResponseBytes = 256 * 1024;

    private static readonly Lazy<HttpClient> Shared = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

    private readonly Uri _url = url ?? throw new ArgumentNullException(nameof(url));

    /// <summary>
    /// How long the authority has to answer, the whole response included; past it the render stops with a
    /// <see cref="PdfException"/>. 30 seconds by default.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async ValueTask<byte[]> GetTokenAsync(ReadOnlyMemory<byte> digest, HashAlgorithmName digestAlgorithm, CancellationToken cancellationToken)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(8);
        nonce[0] = (byte)((nonce[0] & 0x7F) | 0x01); // Positive and without leading zeros, so it round-trips unchanged.
        using var content = new ByteArrayContent(Rfc3161.Request(digest.Span, digestAlgorithm, nonce));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
        using var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = content };

        // One deadline for the request and the body: a server that sends headers and then trickles can't hold the
        // render (the client's own Timeout ends at the headers).
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        byte[] body;
        try
        {
            using HttpResponseMessage response = await (httpClient ?? Shared.Value).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new PdfException($"The timestamp authority {_url.Host} answered HTTP {(int)response.StatusCode}.");
            }

            body = await ReadBoundedAsync(response, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PdfException($"The timestamp authority {_url.Host} didn't answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException e)
        {
            throw new PdfException($"The timestamp authority {_url.Host} couldn't be reached: {e.Message}", e);
        }

        return Rfc3161.TokenFromResponse(body, digest.Span, digestAlgorithm, nonce, _url.Host);
    }

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new PdfException($"The timestamp authority {_url.Host} sent more than {MaxResponseBytes:N0} bytes.");
        }

        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw new PdfException($"The timestamp authority {_url.Host} sent more than {MaxResponseBytes:N0} bytes.");
                }

                buffer.Write(chunk.AsSpan(0, read));
            }

            return buffer.ToArray();
        }
    }
}

/// <summary>RFC 3161 messages: the request, and the checks on a response and its token.</summary>
internal static class Rfc3161
{
    private const string SignedData = "1.2.840.113549.1.7.2";
    private const string TstInfo = "1.2.840.113549.1.9.16.1.4";

    /// <summary><c>TimeStampReq</c>: version 1, the message imprint, the nonce, and a request for the TSA's certificate.</summary>
    public static byte[] Request(ReadOnlySpan<byte> digest, HashAlgorithmName digestAlgorithm, ReadOnlySpan<byte> nonce)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.WriteInteger(1);
        MessageImprint(writer, digest, digestAlgorithm);
        writer.WriteIntegerUnsigned(nonce);
        writer.WriteBoolean(true);
        writer.PopSequence();
        return writer.Encode();
    }

    /// <summary>Checks a <c>TimeStampResp</c> and returns its token, which must answer this digest and nonce.</summary>
    public static byte[] TokenFromResponse(byte[] response, ReadOnlySpan<byte> digest, HashAlgorithmName digestAlgorithm, ReadOnlySpan<byte> nonce, string authority)
    {
        try
        {
            var reader = new AsnReader(response, AsnEncodingRules.BER);
            AsnReader resp = reader.ReadSequence();
            AsnReader status = resp.ReadSequence();
            if (!status.TryReadInt32(out int granted) || granted is not (0 or 1))
            {
                throw new PdfException($"The timestamp authority {authority} refused the request (status {granted}).");
            }

            if (!resp.HasData)
            {
                throw new PdfException($"The timestamp authority {authority} granted the request but sent no token.");
            }

            byte[] token = resp.ReadEncodedValue().ToArray();
            CheckToken(token, digest, digestAlgorithm, nonce, authority);
            return token;
        }
        catch (AsnContentException e)
        {
            throw new PdfException($"The timestamp authority {authority} sent a response that isn't RFC 3161.", e);
        }
    }

    private static void CheckToken(byte[] token, ReadOnlySpan<byte> digest, HashAlgorithmName digestAlgorithm, ReadOnlySpan<byte> nonce, string authority)
    {
        AsnReader contentInfo = new AsnReader(token, AsnEncodingRules.BER).ReadSequence();
        if (contentInfo.ReadObjectIdentifier() != SignedData)
        {
            throw new PdfException($"The timestamp token from {authority} isn't CMS SignedData.");
        }

        AsnReader signedData = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        signedData.ReadInteger();
        signedData.ReadSetOf();
        AsnReader encapsulated = signedData.ReadSequence();
        if (encapsulated.ReadObjectIdentifier() != TstInfo)
        {
            throw new PdfException($"The timestamp token from {authority} doesn't carry TSTInfo.");
        }

        byte[] tstInfo = encapsulated.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadOctetString();
        VerifySigner(signedData, tstInfo, authority);
        AsnReader info = new AsnReader(tstInfo, AsnEncodingRules.BER).ReadSequence();
        info.ReadInteger(); // version
        info.ReadObjectIdentifier(); // policy
        AsnReader imprint = info.ReadSequence();
        string algorithm = imprint.ReadSequence().ReadObjectIdentifier();
        byte[] hashed = imprint.ReadOctetString();
        if (algorithm != CmsSignature.DigestOid(digestAlgorithm) || !hashed.AsSpan().SequenceEqual(digest))
        {
            throw new PdfException($"The timestamp token from {authority} is for a different digest.");
        }

        info.ReadInteger(); // serialNumber
        info.ReadGeneralizedTime();
        byte[]? answered = null;
        while (info.HasData && answered is null)
        {
            Asn1Tag tag = info.PeekTag();
            if (tag.HasSameClassAndValue(Asn1Tag.Integer))
            {
                answered = info.ReadIntegerBytes().ToArray();
            }
            else
            {
                info.ReadEncodedValue(); // accuracy, ordering, or the tagged fields after the nonce.
            }
        }

        if (answered is null || !answered.AsSpan().SequenceEqual(nonce))
        {
            throw new PdfException($"The timestamp token from {authority} doesn't answer this request (its nonce differs).");
        }
    }

    /// <summary>
    /// Checks the token is signed (RFC 3161, 2.4.2; RFC 5652, 5.4 and 5.6): one SignerInfo whose message-digest
    /// attribute is the TSTInfo's digest and whose signature over the signed attributes verifies with the certificate
    /// it names, which the token must carry (the request asks for it). Whether that certificate chains to a trusted
    /// authority is the validator's question, as for the signature itself.
    /// </summary>
    private static void VerifySigner(AsnReader signedData, byte[] tstInfo, string authority)
    {
        PdfException Bad(string why) => new($"The timestamp token from {authority} isn't validly signed: {why}.");
        var certificates = new List<byte[]>();
        if (signedData.HasData && signedData.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            AsnReader set = signedData.ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0));
            while (set.HasData && certificates.Count < 32)
            {
                certificates.Add(set.ReadEncodedValue().ToArray());
            }
        }

        if (signedData.HasData && signedData.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1)))
        {
            signedData.ReadEncodedValue(); // CRLs.
        }

        AsnReader signers = signedData.ReadSetOf();
        if (!signers.HasData)
        {
            throw Bad("it has no signer");
        }

        AsnReader signer = signers.ReadSequence();
        signer.ReadInteger();
        ReadOnlyMemory<byte> identifier = signer.ReadEncodedValue();
        HashAlgorithmName digest = HashFor(signer.ReadSequence().ReadObjectIdentifier()) ?? throw Bad("its digest algorithm isn't SHA-1 or SHA-2");
        if (!signer.HasData || !signer.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            throw Bad("it has no signed attributes");
        }

        byte[] attributes = signer.ReadEncodedValue().ToArray();
        AsnReader algorithm = signer.ReadSequence();
        string signatureOid = algorithm.ReadObjectIdentifier();
        byte[] signature = signer.ReadOctetString();

        // The message-digest attribute must be the TSTInfo's digest.
        byte[]? messageDigest = null;
        AsnReader attributeSet = new AsnReader(attributes, AsnEncodingRules.BER).ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0));
        while (attributeSet.HasData)
        {
            AsnReader attribute = attributeSet.ReadSequence();
            if (attribute.ReadObjectIdentifier() == "1.2.840.113549.1.9.4")
            {
                messageDigest = attribute.ReadSetOf().ReadOctetString();
            }
        }

        if (messageDigest is null || !messageDigest.AsSpan().SequenceEqual(CryptographicOperations.HashData(digest, tstInfo)))
        {
            throw Bad("its message digest isn't the digest of its content");
        }

        X509Certificate2? certificate = null;
        foreach (byte[] raw in certificates)
        {
            X509Certificate2 candidate = X509CertificateLoader.LoadCertificate(raw);
            if (Identifies(identifier.Span, candidate))
            {
                certificate = candidate;
                break;
            }

            candidate.Dispose();
        }

        using (certificate)
        {
            if (certificate is null)
            {
                throw Bad("it doesn't carry the certificate that signed it");
            }

            // The signature covers the attributes encoded as a SET (RFC 5652, 5.4), not as [0].
            attributes[0] = 0x31;
            bool valid = signatureOid switch
            {
                "1.2.840.113549.1.1.10" => certificate.GetRSAPublicKey()?.VerifyData(attributes, signature, digest, RSASignaturePadding.Pss) ?? false,
                _ when signatureOid.StartsWith("1.2.840.113549.1.1.", StringComparison.Ordinal) => certificate.GetRSAPublicKey()?.VerifyData(attributes, signature, digest, RSASignaturePadding.Pkcs1) ?? false,
                _ when signatureOid.StartsWith("1.2.840.10045.", StringComparison.Ordinal) => certificate.GetECDsaPublicKey()?.VerifyData(attributes, signature, digest, DSASignatureFormat.Rfc3279DerSequence) ?? false,
                _ => throw Bad($"its signature algorithm {signatureOid} isn't RSA or ECDSA"),
            };
            if (!valid)
            {
                throw Bad("its signature doesn't verify");
            }
        }
    }

    /// <summary>Whether a SignerIdentifier (issuer and serial, or [0] subject key identifier) names the certificate.</summary>
    private static bool Identifies(ReadOnlySpan<byte> identifier, X509Certificate2 certificate)
    {
        var reader = new AsnReader(identifier.ToArray(), AsnEncodingRules.BER);
        if (reader.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            byte[] key = reader.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0));
            return certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifierBytes.Span.SequenceEqual(key) ?? false;
        }

        AsnReader issuerAndSerial = reader.ReadSequence();
        ReadOnlyMemory<byte> issuer = issuerAndSerial.ReadEncodedValue();
        ReadOnlyMemory<byte> serial = issuerAndSerial.ReadIntegerBytes();
        return issuer.Span.SequenceEqual(certificate.IssuerName.RawData) && serial.Span.SequenceEqual(certificate.SerialNumberBytes.Span);
    }

    private static HashAlgorithmName? HashFor(string oid) => oid switch
    {
        "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
        "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        _ => null,
    };

    /// <summary><c>MessageImprint</c>: the digest algorithm and the digest.</summary>
    internal static void MessageImprint(AsnWriter writer, ReadOnlySpan<byte> digest, HashAlgorithmName digestAlgorithm)
    {
        writer.PushSequence();
        writer.PushSequence();
        writer.WriteObjectIdentifier(CmsSignature.DigestOid(digestAlgorithm));
        writer.PopSequence();
        writer.WriteOctetString(digest);
        writer.PopSequence();
    }
}
