using System;
using System.Formats.Asn1;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Signing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>
/// PAdES B-T: an RFC 3161 timestamp on the signature. A fake timestamp authority issues real tokens with
/// <see cref="SignedCms"/>, and <see cref="Rfc3161TimestampToken"/> checks what ends up in the file.
/// </summary>
public sealed class TimestampTests
{
    private static readonly DateTimeOffset StampTime = new(2026, 11, 15, 9, 30, 5, TimeSpan.Zero);
    private static readonly Lazy<X509Certificate2> Authority = new(CreateAuthority);

    private static RenderOptions Options(ITimestampClient timestamp, IPdfSigner? signer = null)
    {
        signer ??= PdfSigners.FromCertificate(SignatureTests.Pki.Value.Rsa);
        return PdfInspect.Options() with { Signature = new PdfSignatureOptions(signer) { Timestamp = timestamp } };
    }

    [Theory]
    [InlineData("rsa")]
    [InlineData("ecdsa")]
    public async Task TheTokenCoversTheSignatureValue(string key)
    {
        var tsa = new FakeAuthority();
        IPdfSigner signer = PdfSigners.FromCertificate(key == "rsa" ? SignatureTests.Pki.Value.Rsa : SignatureTests.Pki.Value.Ec);
        (byte[] file, _) = await RenderTests.RenderAsync(SignatureTests.Document(), Options(Client(tsa), signer));

        SignerInfo signerInfo = SignatureTests.Verify(file).SignerInfos[0];
        CryptographicAttributeObject attribute = Assert.Single(signerInfo.UnsignedAttributes.Cast<CryptographicAttributeObject>());
        Assert.Equal("1.2.840.113549.1.9.16.2.14", attribute.Oid.Value); // id-aa-signatureTimeStampToken
        Assert.True(Rfc3161TimestampToken.TryDecode(attribute.Values[0].RawData, out Rfc3161TimestampToken? token, out _));
        Assert.True(token!.VerifySignatureForSignerInfo(signerInfo, out X509Certificate2? stamper));
        Assert.Equal(Authority.Value.Thumbprint, stamper!.Thumbprint);
        Assert.Equal(StampTime, token.TokenInfo.Timestamp);
        Assert.Equal(1, tsa.Requests);
    }

    [Fact]
    public async Task TheRequestIsRfc3161()
    {
        var tsa = new FakeAuthority();
        byte[] digest = SHA256.HashData("signature"u8);
        byte[] token = await Client(tsa).GetTokenAsync(digest, HashAlgorithmName.SHA256, TestContext.Current.CancellationToken);

        Assert.Equal("application/timestamp-query", tsa.ContentType);
        Assert.True(Rfc3161TimestampRequest.TryDecode(tsa.LastRequest, out Rfc3161TimestampRequest? request, out _));
        Assert.True(request!.RequestSignerCertificate);
        Assert.True(request.GetNonce().HasValue);
        Assert.Equal(digest, request.GetMessageHash().ToArray());
        Assert.True(Rfc3161TimestampToken.TryDecode(token, out Rfc3161TimestampToken? decoded, out _));
        Assert.True(decoded!.VerifySignatureForHash(digest, HashAlgorithmName.SHA256, out _));
    }

    [Theory]
    [InlineData("refused", "refused the request")]
    [InlineData("nonce", "nonce differs")]
    [InlineData("digest", "different digest")]
    [InlineData("http", "answered HTTP 503")]
    [InlineData("garbage", "isn't RFC 3161")]
    [InlineData("huge", "more than")]
    [InlineData("empty", "sent no token")]
    [InlineData("badsig", "signature doesn't verify")]
    [InlineData("nocert", "doesn't carry the certificate")]
    [InlineData("trickle", "didn't answer within 1 seconds")]
    [InlineData("unreachable", "couldn't be reached")]
    public async Task ABadAnswerStopsTheRenderAndSaysWhy(string fault, string message)
    {
        var tsa = new FakeAuthority { Fault = fault };
        PdfException error = await Assert.ThrowsAsync<PdfException>(async () => await RenderTests.RenderAsync(SignatureTests.Document(), Options(Client(tsa))));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Contains("tsa.test", error.Message, StringComparison.Ordinal);
    }

    private static Rfc3161TimestampClient Client(FakeAuthority tsa) => new(new Uri("https://tsa.test/stamp"), new HttpClient(tsa)) { Timeout = TimeSpan.FromSeconds(1) };

    [Fact]
    public async Task TheSyncRenderRefusesATimestampBeforeWritingAnything()
    {
        using var output = new System.IO.MemoryStream();
        RenderOptions options = Options(Client(new FakeAuthority()));
        ArgumentException error = Assert.Throws<ArgumentException>(() => SignatureTests.Document().Render(output, options));
        Assert.Contains("RenderAsync", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AnEcdsaSignerThatReturnsDerIsAccepted()
    {
        (byte[] file, _) = await RenderTests.RenderAsync(SignatureTests.Document(), PdfInspect.Options() with { Signature = new PdfSignatureOptions(new DerSigner(SignatureTests.Pki.Value.Ec)) });
        SignatureTests.Verify(file);
    }

    /// <summary>An ECDSA key that returns DER signatures, as AWS KMS does.</summary>
    private sealed class DerSigner(X509Certificate2 certificate) : IPdfSigner
    {
        public X509Certificate2 Certificate => certificate;

        public System.Collections.Generic.IReadOnlyList<X509Certificate2> Chain => [];

        public PdfSignatureAlgorithm Algorithm => PdfSignatureAlgorithm.Ecdsa;

        public HashAlgorithmName DigestAlgorithm => HashAlgorithmName.SHA256;

        public ValueTask<byte[]> SignDigestAsync(ReadOnlyMemory<byte> digest, CancellationToken cancellationToken)
        {
            using ECDsa key = certificate.GetECDsaPrivateKey()!;
            return ValueTask.FromResult(key.SignHash(digest.Span, DSASignatureFormat.Rfc3279DerSequence));
        }
    }

    private static X509Certificate2 CreateAuthority()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Zorg.Pdf Test TSA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.8")], critical: true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>A body that sends a byte and then waits, as a stalled server does.</summary>
    private sealed class Trickle : System.IO.Stream
    {
        private bool _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                buffer.Span[0] = 0x30;
                return 1;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A timestamp authority in memory: answers requests with signed tokens, or with the fault it's told to make.</summary>
    private sealed class FakeAuthority : HttpMessageHandler
    {
        public string? Fault { get; init; }

        public int Requests { get; private set; }

        public byte[] LastRequest { get; private set; } = [];

        public string? ContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            ContentType = request.Content!.Headers.ContentType?.MediaType;
            LastRequest = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return Fault switch
            {
                "http" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                "garbage" => Answer([1, 2, 3]),
                "unreachable" => throw new HttpRequestException("No such host is known."),
                "trickle" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Trickle()) },
                "huge" => Answer(new byte[Rfc3161TimestampClient.MaxResponseBytes + 1]),
                _ => Answer(Response()),
            };
        }

        private static HttpResponseMessage Answer(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

        private byte[] Response()
        {
            AsnReader request = new AsnReader(LastRequest, AsnEncodingRules.DER).ReadSequence();
            request.ReadInteger();
            ReadOnlyMemory<byte> imprint = request.ReadEncodedValue();
            byte[] nonce = request.ReadIntegerBytes().ToArray();
            if (Fault == "nonce")
            {
                nonce[^1] ^= 1;
            }

            if (Fault == "digest")
            {
                byte[] changed = imprint.ToArray();
                changed[^1] ^= 1;
                imprint = changed;
            }

            var info = new AsnWriter(AsnEncodingRules.DER);
            info.PushSequence();
            info.WriteInteger(1);
            info.WriteObjectIdentifier("1.3.6.1.4.1.99999.1");
            info.WriteEncodedValue(imprint.Span);
            info.WriteInteger(42);
            info.WriteGeneralizedTime(StampTime);
            info.WriteInteger(nonce);
            info.PopSequence();

            var cms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()), detached: false);
            var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, Authority.Value) { IncludeOption = X509IncludeOption.EndCertOnly };
            signer.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47", SigningCertificateV2(Authority.Value)));
            cms.ComputeSignature(signer);

            var response = new AsnWriter(AsnEncodingRules.DER);
            response.PushSequence();
            response.PushSequence();
            response.WriteInteger(Fault == "refused" ? 2 : 0);
            response.PopSequence();
            if (Fault != "empty")
            {
                byte[] token = cms.Encode();
                if (Fault == "badsig")
                {
                    token[^1] ^= 1; // The last bytes are the signature value.
                }

                if (Fault == "nocert")
                {
                    cms.RemoveCertificate(Authority.Value);
                    token = cms.Encode();
                }

                response.WriteEncodedValue(token);
            }

            response.PopSequence();
            return response.Encode();
        }

        private static byte[] SigningCertificateV2(X509Certificate2 certificate)
        {
            var writer = new AsnWriter(AsnEncodingRules.DER);
            writer.PushSequence();
            writer.PushSequence();
            writer.PushSequence();
            writer.WriteOctetString(SHA256.HashData(certificate.RawData));
            writer.PopSequence();
            writer.PopSequence();
            writer.PopSequence();
            return writer.Encode();
        }
    }
}
