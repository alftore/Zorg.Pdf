using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Reading;

namespace Zorg.Pdf.Tests.Reading;

/// <summary>
/// Encrypted files that open without a password: RC4 (revisions 2 and 3), AES-128 (4) and AES-256 (6), written here
/// by a small encryptor, the forward direction of the standard handler. The Linux gate checks the same against qpdf.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "The PDF standard handler's own algorithms, to make test files.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5390:Do not hard-code encryption key", Justification = "Fixed keys make reproducible test files.")]
public sealed class EncryptionTests
{
    private const string Content = "BT /F1 12 Tf 72 720 Td (Hemmelig innhold) Tj ET";

    [Theory]
    [InlineData(2, 40, "RC4 40-bit")]
    [InlineData(3, 128, "RC4 128-bit")]
    [InlineData(4, 128, "AES-128")]
    [InlineData(6, 256, "AES-256")]
    public void FilesThatOpenWithoutAPasswordAreDecrypted(int revision, int bits, string cipher)
    {
        byte[] file = Encrypted(revision, bits, permissions: -4); // Everything allowed.

        PdfFileInfo info = PdfFileInfo.Read(file);
        var cos = new CosFile(file, PdfReadLimits.Default, []);
        var page = CosPages.Read(cos, PdfReadLimits.Default, [])[0];

        Assert.Equal(("Kontrakt 42", "nb", true, 1), (info.Title, info.Language, info.IsEncrypted, info.PageCount));
        Assert.Equal(Content, Encoding.Latin1.GetString(cos.Decode((CosStream)cos.Resolve(page.Dictionary["Contents"]))));
        PdfDiagnostic note = Assert.Single(info.Diagnostics);
        Assert.Equal(("ZPDF-P005", DiagnosticSeverity.Info), (note.Code, note.Severity));
        Assert.Contains(cipher, note.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("doesn't allow", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RestrictionsAreNamedAndPasswordsAndOtherHandlersRefused()
    {
        PdfFileInfo restricted = PdfFileInfo.Read(Encrypted(4, 128, permissions: unchecked((int)0xFFFFF0C0)));
        Assert.Contains("doesn't allow printing, changing, copying text", restricted.Diagnostics[0].Message, StringComparison.Ordinal);

        Assert.Contains("needs a password", Assert.Throws<PdfReadException>(() => PdfFileInfo.Read(Encrypted(4, 128, -4, password: "hemmelig"))).Message, StringComparison.Ordinal);
        Assert.Contains("needs a password", Assert.Throws<PdfReadException>(() => PdfFileInfo.Read(Encrypted(6, 256, -4, password: "hemmelig"))).Message, StringComparison.Ordinal);
        byte[] other = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(Encrypted(4, 128, -4)).Replace("/Filter /Standard", "/Filter /Acme____", StringComparison.Ordinal));
        Assert.Contains("Acme____ security handler", Assert.Throws<PdfReadException>(() => PdfFileInfo.Read(other)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EncryptedPagesCanBeMergedIntoAPlainFile()
    {
        using var output = new MemoryStream();
        PdfFileResult result = await PdfFiles.MergeAsync([new PdfPageSource(Encrypted(6, 256, -4)), new PdfPageSource(Encrypted(2, 40, -4))], output, cancellationToken: TestContext.Current.CancellationToken);

        PdfFileInfo merged = PdfFileInfo.Read(output.ToArray());
        Assert.Equal((2, false, "Kontrakt 42"), (merged.PageCount, merged.IsEncrypted, merged.Title));
        Assert.Contains(result.Diagnostics, d => d.Code == "ZPDF-P005");
        Assert.Contains("(Hemmelig innhold) Tj", Encoding.Latin1.GetString(DecodedContents(output.ToArray())), StringComparison.Ordinal);
    }

    private static byte[] DecodedContents(byte[] file)
    {
        var cos = new CosFile(file, PdfReadLimits.Default, []);
        return cos.Decode((CosStream)cos.Resolve(CosPages.Read(cos, PdfReadLimits.Default, [])[0].Dictionary["Contents"]));
    }

    /// <summary>A one-page PDF encrypted with the standard handler: the Info title, the catalog's language and the content stream encrypted.</summary>
    private static byte[] Encrypted(int revision, int bits, int permissions, string password = "")
    {
        byte[] id = Encoding.ASCII.GetBytes("0123456789abcdef");
        byte[] pw = Encoding.UTF8.GetBytes(password);
        byte[] o = new byte[32];
        new Random(revision).NextBytes(o);
        byte[] key, u, ue = [];
        if (revision == 6)
        {
            key = new byte[32];
            new Random(7).NextBytes(key);
            byte[] validation = "vsalt123"u8.ToArray(), keySalt = "ksalt456"u8.ToArray();
            u = [.. CosSecurity.Hash(6, pw, validation, []), .. validation, .. keySalt];
            using Aes aes = Aes.Create();
            aes.Key = CosSecurity.Hash(6, pw, keySalt, []);
            ue = aes.EncryptCbc(key, new byte[16], PaddingMode.None);
        }
        else
        {
            byte[] padded = [.. pw, .. CosSecurity.PaddedEmptyPassword];
            key = FileKey(padded[..32], o, permissions, id, bits / 8, revision);
            u = revision == 2
                ? CosSecurity.Rc4(key, CosSecurity.PaddedEmptyPassword)
                : [.. UserKey3(key, id), .. new byte[16]];
        }

        bool aesStreams = revision >= 4;
        byte[] Encrypt(byte[] data, int number)
        {
            if (revision == 6)
            {
                return AesEncrypt(key, data);
            }

            byte[] objectKey = MD5.HashData([.. key, (byte)number, 0, 0, 0, 0, .. aesStreams ? "sAlT"u8.ToArray() : []])[..Math.Min(key.Length + 5, 16)];
            return aesStreams ? AesEncrypt(objectKey, data) : CosSecurity.Rc4(objectKey, data);
        }

        string Hex(byte[] b) => "<" + Convert.ToHexString(b) + ">";
        string encrypt = revision switch
        {
            2 => $"<< /Filter /Standard /V 1 /R 2 /O {Hex(o)} /U {Hex(u)} /P {permissions} >>",
            3 => $"<< /Filter /Standard /V 2 /R 3 /Length {bits} /O {Hex(o)} /U {Hex(u)} /P {permissions} >>",
            4 => $"<< /Filter /Standard /V 4 /R 4 /Length 128 /CF << /StdCF << /CFM /AESV2 /AuthEvent /DocOpen /Length 16 >> >> /StmF /StdCF /StrF /StdCF /O {Hex(o)} /U {Hex(u)} /P {permissions} >>",
            _ => $"<< /Filter /Standard /V 5 /R 6 /Length 256 /CF << /StdCF << /CFM /AESV3 /AuthEvent /DocOpen /Length 32 >> >> /StmF /StdCF /StrF /StdCF /O {Hex(o)} /U {Hex(u)} /OE {Hex(new byte[32])} /UE {Hex(ue)} /Perms {Hex(new byte[16])} /P {permissions} >>",
        };
        byte[] content = Encrypt(Encoding.Latin1.GetBytes(Content), 4);
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R /Lang {Hex(Encrypt("nb"u8.ToArray(), 1))} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n" + Encoding.Latin1.GetString(content) + "\nendstream",
            $"<< /Title {Hex(Encrypt("Kontrakt 42"u8.ToArray(), 5))} >>",
            encrypt,
        };
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(text.Length);
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = text.Length;
        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 5 0 R /Encrypt 6 0 R /ID [{Hex(id)} {Hex(id)}] >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    /// <summary>Algorithm 2, written out again from the standard rather than calling the reader's.</summary>
    private static byte[] FileKey(byte[] paddedPassword, byte[] o, int p, byte[] id, int length, int revision)
    {
        byte[] hash = MD5.HashData([.. paddedPassword, .. o, (byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24), .. id]);
        for (int i = 0; revision >= 3 && i < 50; i++)
        {
            hash = MD5.HashData(hash.AsSpan(0, length));
        }

        return hash[..length];
    }

    /// <summary>Algorithm 5: the first 16 bytes of /U for revisions 3 and 4.</summary>
    private static byte[] UserKey3(byte[] key, byte[] id)
    {
        byte[] value = CosSecurity.Rc4(key, MD5.HashData([.. CosSecurity.PaddedEmptyPassword, .. id]));
        for (int i = 1; i <= 19; i++)
        {
            byte[] round = Array.ConvertAll(key, b => (byte)(b ^ i));
            value = CosSecurity.Rc4(round, value);
        }

        return value;
    }

    private static byte[] AesEncrypt(byte[] key, byte[] data)
    {
        using Aes aes = Aes.Create();
        aes.Key = key;
        byte[] iv = new byte[16];
        new Random(data.Length).NextBytes(iv);
        return [.. iv, .. aes.EncryptCbc(data, iv, PaddingMode.PKCS7)];
    }
}
