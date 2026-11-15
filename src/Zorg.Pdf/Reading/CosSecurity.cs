using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Zorg.Pdf.Reading;

/// <summary>
/// The standard security handler (ISO 32000-1 7.6.3, ISO 32000-2 7.6.4) for files that open without a password:
/// the empty user password is checked, the file key derived from it, and strings and streams decrypted as objects are
/// read. RC4 (40 to 128 bits), AES-128 (AESV2) and AES-256 (AESV3, revisions 5 and 6). A file that needs a password,
/// or uses another security handler, is refused.
/// </summary>
[SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "MD5 and RC4 are what these files were encrypted with (ISO 32000-1, 7.6.3); reading them needs the same algorithms.")]
internal sealed class CosSecurity
{
    /// <summary>The padding the standard handler puts after a password (ISO 32000-1, 7.6.3.3).</summary>
    private static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private enum Method
    {
        Identity,
        Rc4,
        AesV2,
        AesV3,
    }

    private readonly byte[] _key;
    private readonly Method _strings;
    private readonly Method _streams;
    private readonly bool _encryptMetadata;

    private CosSecurity(byte[] key, Method strings, Method streams, bool encryptMetadata, int permissions, int revision) =>
        (_key, _strings, _streams, _encryptMetadata, Permissions, Revision) = (key, strings, streams, encryptMetadata, permissions, revision);

    /// <summary>The /P flags: what the file's author allows.</summary>
    public int Permissions { get; }

    /// <summary>The handler's revision, 2 to 6.</summary>
    public int Revision { get; }

    /// <summary>The cipher, for messages: "RC4 40-bit", "AES-128", "AES-256".</summary>
    public string Cipher => _streams switch
    {
        Method.AesV3 => "AES-256",
        Method.AesV2 => "AES-128",
        Method.Rc4 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"RC4 {_key.Length * 8}-bit"),
        _ => "no cipher",
    };

    /// <summary>Opens the handler with the empty user password, or throws when the file needs one.</summary>
    public static CosSecurity Open(CosDictionary encrypt, byte[] firstId, Func<CosValue?, CosValue> resolve)
    {
        if (resolve(encrypt["Filter"]) is not CosName { Value: "Standard" } filter)
        {
            string name = resolve(encrypt["Filter"]) is CosName other ? other.Value : "an unnamed";
            throw new PdfReadException($"The file is encrypted with the {name} security handler (certificates or a vendor's), which can't be opened here.");
        }

        _ = filter;
        int v = (int)(resolve(encrypt["V"]).AsInteger ?? 0);
        int r = (int)(resolve(encrypt["R"]).AsInteger ?? 0);
        int p = unchecked((int)(resolve(encrypt["P"]).AsInteger ?? -1));
        bool encryptMetadata = resolve(encrypt["EncryptMetadata"]) is not CosBoolean { Value: false };
        byte[] o = Bytes(resolve(encrypt["O"])), u = Bytes(resolve(encrypt["U"]));
        if (r is < 2 or > 6 || o.Length < 32 || u.Length < 32)
        {
            throw new PdfReadException($"The file's encryption (revision {r}) isn't one the standard handler defines.");
        }

        (Method strings, Method streams) = v switch
        {
            1 or 2 => (Method.Rc4, Method.Rc4),
            4 or 5 => (CryptFilter(encrypt, "StrF", resolve), CryptFilter(encrypt, "StmF", resolve)),
            _ => throw new PdfReadException($"The file's encryption (version {v}) isn't supported."),
        };

        byte[] key;
        if (r >= 5)
        {
            // AES-256: the user key checks the password; UE, decrypted with a key from it, is the file key.
            byte[] ue = Bytes(resolve(encrypt["UE"]));
            if (ue.Length < 32 || !Hash(r, [], u.AsSpan(32, 8), []).AsSpan().SequenceEqual(u.AsSpan(0, 32)))
            {
                throw NeedsPassword();
            }

            byte[] intermediate = Hash(r, [], u.AsSpan(40, 8), []);
            using Aes aes = Aes.Create();
            aes.Key = intermediate;
            key = aes.DecryptCbc(ue.AsSpan(0, 32), new byte[16], PaddingMode.None);
        }
        else
        {
            int length = v == 1 ? 5 : (int)Math.Clamp((resolve(encrypt["Length"]).AsInteger ?? 40) / 8, 5, 16);
            if (strings == Method.AesV2 || streams == Method.AesV2)
            {
                length = 16;
            }

            key = FileKey(o, p, firstId, length, r, encryptMetadata);
            if (!UserPasswordMatches(key, u, firstId, r))
            {
                throw NeedsPassword();
            }
        }

        return new CosSecurity(key, strings, streams, encryptMetadata, p, r);
    }

    /// <summary>A value read from object <paramref name="number"/>, its strings and stream data decrypted.</summary>
    public CosValue Decrypt(CosValue value, int number, int generation, int depth = 0)
    {
        if (depth > CosLexer.MaxDepth)
        {
            return value;
        }

        switch (value)
        {
            case CosString s:
                return new CosString(Apply(_strings, s.Value, number, generation), s.Hex);
            case CosArray a:
                var items = new List<CosValue>(a.Items.Count);
                foreach (CosValue item in a.Items)
                {
                    items.Add(Decrypt(item, number, generation, depth + 1));
                }

                return new CosArray(items);
            case CosDictionary d:
                // A signature's /Contents is never encrypted (ISO 32000-1, 7.6.1).
                bool signature = d["Type"] is CosName { Value: "Sig" or "DocTimeStamp" };
                var entries = new Dictionary<string, CosValue>(d.Entries.Count, StringComparer.Ordinal);
                foreach ((string key, CosValue item) in d.Entries)
                {
                    entries[key] = signature && key == "Contents" ? item : Decrypt(item, number, generation, depth + 1);
                }

                return new CosDictionary(entries);
            case CosStream stream:
                var dictionary = (CosDictionary)Decrypt(stream.Dictionary, number, generation, depth + 1);
                bool clear = dictionary["Type"] is CosName { Value: "XRef" }
                    || (!_encryptMetadata && dictionary["Type"] is CosName { Value: "Metadata" })
                    || IdentityCryptFilter(dictionary);
                return clear ? new CosStream(dictionary, stream.Raw) : new CosStream(dictionary, Apply(_streams, stream.Raw.ToArray(), number, generation));
            default:
                return value;
        }
    }

    private static bool IdentityCryptFilter(CosDictionary dictionary) =>
        dictionary["Filter"] is CosName { Value: "Crypt" } || (dictionary["Filter"] is CosArray { Items.Count: > 0 } filters && filters.Items[0] is CosName { Value: "Crypt" });

    private byte[] Apply(Method method, byte[] data, int number, int generation)
    {
        if (method == Method.Identity || data.Length == 0)
        {
            return data;
        }

        byte[] key = method == Method.AesV3 ? _key : ObjectKey(number, generation, method == Method.AesV2);
        if (method == Method.Rc4)
        {
            return Rc4(key, data);
        }

        if (data.Length < 32 || data.Length % 16 != 0)
        {
            return data.Length < 16 ? [] : AesNoPadding(key, data);
        }

        using Aes aes = Aes.Create();
        aes.Key = key;
        try
        {
            return aes.DecryptCbc(data.AsSpan(16), data.AsSpan(0, 16), PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            return aes.DecryptCbc(data.AsSpan(16), data.AsSpan(0, 16), PaddingMode.None); // Bad padding: keep what decrypts.
        }
    }

    private static byte[] AesNoPadding(byte[] key, byte[] data)
    {
        int whole = (data.Length - 16) / 16 * 16;
        if (whole == 0)
        {
            return [];
        }

        using Aes aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(data.AsSpan(16, whole), data.AsSpan(0, 16), PaddingMode.None);
    }

    /// <summary>Algorithm 1: the object's key from the file key, its number and generation, and "sAlT" for AES.</summary>
    private byte[] ObjectKey(int number, int generation, bool aes)
    {
        byte[] input = new byte[_key.Length + 5 + (aes ? 4 : 0)];
        _key.CopyTo(input, 0);
        input[_key.Length] = (byte)number;
        input[_key.Length + 1] = (byte)(number >> 8);
        input[_key.Length + 2] = (byte)(number >> 16);
        input[_key.Length + 3] = (byte)generation;
        input[_key.Length + 4] = (byte)(generation >> 8);
        if (aes)
        {
            "sAlT"u8.CopyTo(input.AsSpan(_key.Length + 5));
        }

        return MD5.HashData(input)[..Math.Min(_key.Length + 5, 16)];
    }

    /// <summary>Algorithm 2: the file key from the (empty) user password, /O, /P and the file's first ID.</summary>
    internal static byte[] FileKey(byte[] o, int p, byte[] firstId, int length, int r, bool encryptMetadata)
    {
        var input = new List<byte>(Padding);
        input.AddRange(o.AsSpan(0, 32));
        input.AddRange([(byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24)]);
        input.AddRange(firstId);
        if (r >= 4 && !encryptMetadata)
        {
            input.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
        }

        byte[] hash = MD5.HashData([.. input]);
        if (r >= 3)
        {
            for (int i = 0; i < 50; i++)
            {
                hash = MD5.HashData(hash.AsSpan(0, length));
            }
        }

        return hash[..length];
    }

    /// <summary>Algorithms 4 and 5: /U as the empty password would make it.</summary>
    internal static bool UserPasswordMatches(byte[] key, byte[] u, byte[] firstId, int r)
    {
        if (r == 2)
        {
            return Rc4(key, Padding).AsSpan().SequenceEqual(u.AsSpan(0, 32));
        }

        byte[] check = Rc4(key, MD5.HashData([.. Padding, .. firstId]));
        for (int i = 1; i <= 19; i++)
        {
            byte[] round = new byte[key.Length];
            for (int k = 0; k < key.Length; k++)
            {
                round[k] = (byte)(key[k] ^ i);
            }

            check = Rc4(round, check);
        }

        return check.AsSpan().SequenceEqual(u.AsSpan(0, 16));
    }

    /// <summary>The AES-256 password hash: SHA-256 for revision 5, Algorithm 2.B (ISO 32000-2, 7.6.4.3.4) for 6.</summary>
    internal static byte[] Hash(int r, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> userKey)
    {
        byte[] k = SHA256.HashData([.. password, .. salt, .. userKey]);
        if (r == 5)
        {
            return k;
        }

        using Aes aes = Aes.Create();
        for (int round = 0; ; round++)
        {
            byte[] one = [.. password, .. k, .. userKey];
            byte[] k1 = new byte[one.Length * 64];
            for (int i = 0; i < 64; i++)
            {
                one.CopyTo(k1, i * one.Length);
            }

            aes.Key = k[..16];
            byte[] e = aes.EncryptCbc(k1, k.AsSpan(16, 16), PaddingMode.None);
            int sum = 0;
            for (int i = 0; i < 16; i++)
            {
                sum += e[i];
            }

            k = (sum % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };
            if (round >= 63 && e[^1] <= round - 31)
            {
                return k[..32];
            }
        }
    }

    internal static byte[] Rc4(byte[] key, byte[] data)
    {
        Span<byte> s = stackalloc byte[256];
        for (int i = 0; i < 256; i++)
        {
            s[i] = (byte)i;
        }

        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        byte[] output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }

        return output;
    }

    private static Method CryptFilter(CosDictionary encrypt, string which, Func<CosValue?, CosValue> resolve)
    {
        string name = resolve(encrypt[which]) is CosName n ? n.Value : "Identity";
        if (name == "Identity")
        {
            return Method.Identity;
        }

        CosDictionary? filter = resolve(encrypt["CF"]) is CosDictionary filters ? resolve(filters[name]) as CosDictionary : null;
        return (filter is null ? null : resolve(filter["CFM"])) switch
        {
            CosName { Value: "V2" } => Method.Rc4,
            CosName { Value: "AESV2" } => Method.AesV2,
            CosName { Value: "AESV3" } => Method.AesV3,
            CosName { Value: "None" } => Method.Identity,
            _ => throw new PdfReadException($"The file's crypt filter {name} isn't one the standard handler defines."),
        };
    }

    private static byte[] Bytes(CosValue value) => value is CosString s ? s.Value : [];

    private static PdfReadException NeedsPassword() =>
        new("The file needs a password to open. Only files that open without one can be read; save it again without the password.");

    /// <summary>What the file's /P flags forbid, in words, or null when nothing is.</summary>
    public string? Restrictions()
    {
        var denied = new List<string>();
        if ((Permissions & (1 << 2)) == 0)
        {
            denied.Add("printing");
        }

        if ((Permissions & (1 << 3)) == 0)
        {
            denied.Add("changing");
        }

        if ((Permissions & (1 << 4)) == 0)
        {
            denied.Add("copying text");
        }

        if (Revision >= 3 && (Permissions & (1 << 10)) == 0)
        {
            denied.Add("assembling (inserting, removing or rotating pages)");
        }

        return denied.Count == 0 ? null : string.Join(", ", denied);
    }

    /// <summary>The first element of the trailer's /ID, or empty.</summary>
    public static byte[] FirstId(CosDictionary trailer, Func<CosValue?, CosValue> resolve) =>
        resolve(trailer["ID"]) is CosArray { Items.Count: > 0 } id && resolve(id.Items[0]) is CosString first ? first.Value : [];

    /// <summary>For tests: the padded empty password the standard handler starts from.</summary>
    internal static byte[] PaddedEmptyPassword => (byte[])Padding.Clone();
}
