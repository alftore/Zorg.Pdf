using System;
using System.Formats.Asn1;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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

    /// <inheritdoc />
    public async ValueTask<byte[]> GetTokenAsync(ReadOnlyMemory<byte> digest, HashAlgorithmName digestAlgorithm, CancellationToken cancellationToken)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(8);
        nonce[0] = (byte)((nonce[0] & 0x7F) | 0x01); // Positive and without leading zeros, so it round-trips unchanged.
        using var content = new ByteArrayContent(Rfc3161.Request(digest.Span, digestAlgorithm, nonce));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
        using var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = content };
        using HttpResponseMessage response = await (httpClient ?? Shared.Value).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PdfException($"The timestamp authority {_url.Host} answered HTTP {(int)response.StatusCode}.");
        }

        byte[] body = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
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
