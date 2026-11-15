using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Reading;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Signing;

/// <summary>
/// Signs PDFs that already exist, from any producer, by an incremental update: the original bytes are kept as they
/// are and the signature is appended, so earlier signatures stay valid and a second or third signature can follow.
/// </summary>
/// <example>
/// <code>
/// await using FileStream input = File.OpenRead("contract.pdf");
/// await using FileStream output = File.Create("contract-signed.pdf");
/// await PdfSigning.SignAsync(input, output, new PdfSignatureOptions(PdfSigners.FromCertificate(certificate)) { FieldName = "customer" }, cancellationToken: cancellationToken);
/// </code>
/// </example>
public static class PdfSigning
{
    /// <summary>
    /// Signs a PDF read from a stream (to its end, up to <see cref="PdfReadLimits.MaxFileBytes"/>) and writes the signed
    /// file to <paramref name="output"/>. An empty signature field named <see cref="PdfSignatureOptions.FieldName"/> is
    /// signed; without one, an invisible field is added on the first page.
    /// </summary>
    /// <param name="input">The PDF to sign.</param>
    /// <param name="output">Where the signed PDF goes: the original bytes, then the update.</param>
    /// <param name="options">The signer, field name, reason and the rest; <see cref="PdfSignatureOptions.Timestamp"/> makes it B-T.</param>
    /// <param name="limits">Limits on reading the input. Null: <see cref="PdfReadLimits.Default"/>.</param>
    /// <param name="cancellationToken">Cancels reading, signing and writing.</param>
    /// <exception cref="PdfReadException">The input isn't a PDF that can be read.</exception>
    /// <exception cref="PdfException">The file can't be signed as it is: encrypted, damaged cross-reference data, or the field is signed already.</exception>
    public static async Task SignAsync(Stream input, Stream output, PdfSignatureOptions options, PdfReadLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        byte[] file = await PdfFileInfo.ReadAllAsync(input, (limits ?? PdfReadLimits.Default).MaxFileBytes, cancellationToken).ConfigureAwait(false);
        await SignAsync(file, output, options, limits, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Signs a PDF in memory; see the stream overload.</summary>
    /// <param name="file">The PDF to sign.</param>
    /// <param name="output">Where the signed PDF goes.</param>
    /// <param name="options">The signer and the rest.</param>
    /// <param name="limits">Limits on reading the input. Null: <see cref="PdfReadLimits.Default"/>.</param>
    /// <param name="cancellationToken">Cancels signing and writing.</param>
    internal static async Task SignAsync(ReadOnlyMemory<byte> file, Stream output, PdfSignatureOptions options, PdfReadLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable.", nameof(output));
        }

        Rendering.RenderEngine.CheckSignature(options);
        options = options with { SigningTime = options.SigningTime ?? DateTimeOffset.Now };
        limits ??= PdfReadLimits.Default;

        var cos = new CosFile(file, limits, []);
        if (cos.IsEncrypted)
        {
            throw new PdfException("The file is encrypted. Signing encrypted files isn't supported yet: sign it before encrypting, or decrypt it first.");
        }

        if (cos.StartXref is not { } startXref)
        {
            throw new PdfException("The file's cross-reference data is damaged, so it can't take an incremental update without breaking. Save it again with the program that made it, then sign that.");
        }

        if (cos.Trailer["Root"] is not CosReference root)
        {
            throw new PdfException("The file's catalog isn't an indirect object, so it can't be updated.");
        }

        var update = new IncrementalUpdate(file, cos.HeaderOffset);
        int next = Math.Max(cos.Size, (int)Math.Clamp(cos.Trailer["Size"]?.AsInteger ?? 0, 0, int.MaxValue));
        var signature = new CosReference(next++, 0);
        CosDictionary catalog = cos.Catalog;
        var acroForm = cos.Resolve(catalog["AcroForm"]) as CosDictionary;
        List<CosValue> fields = acroForm is not null && cos.Resolve(acroForm["Fields"]) is CosArray existingFields ? [.. existingFields.Items] : [];

        if (FindField(cos, fields, options.FieldName, "", 0) is { } found)
        {
            // An empty field made for this: give it the signature as its value. Its appearance stays as it was.
            if (FieldType(cos, found.Field) != "Sig")
            {
                throw new PdfException($"The field \"{options.FieldName}\" isn't a signature field. Choose another FieldName.");
            }

            if (found.Field["V"] is { } value && value is not CosNull)
            {
                throw new PdfException($"The field \"{options.FieldName}\" is signed already. Choose another FieldName: a new field is added for it.");
            }

            update.WriteObject(found.Reference, With(found.Field, ("V", signature)));
        }
        else
        {
            if (FirstPage(cos) is not { } page)
            {
                throw new PdfException("The file has no pages to put a signature field on.");
            }

            var widget = new CosReference(next++, 0);
            List<CosValue> annotations = cos.Resolve(page.Page["Annots"]) is CosArray existing ? [.. existing.Items] : [];
            annotations.Add(widget);
            update.WriteObject(page.Reference, With(page.Page, ("Annots", new CosArray(annotations))));
            update.WriteObject(widget, new CosDictionary(new Dictionary<string, CosValue>(StringComparer.Ordinal)
            {
                ["Type"] = new CosName("Annot"),
                ["Subtype"] = new CosName("Widget"),
                ["FT"] = new CosName("Sig"),
                ["T"] = new CosString(TextBytes(options.FieldName, out bool hex), hex),
                ["V"] = signature,
                ["Rect"] = new CosArray([new CosInteger(0), new CosInteger(0), new CosInteger(0), new CosInteger(0)]),
                ["F"] = new CosInteger(132),
                ["P"] = page.Reference,
            }));
            fields.Add(widget);
        }

        // SigFlags 3: signatures exist, and changes must be appended (ISO 32000-1, 12.7.2).
        var form = new Dictionary<string, CosValue>(acroForm?.Entries ?? [], StringComparer.Ordinal)
        {
            ["Fields"] = new CosArray(fields),
            ["SigFlags"] = new CosInteger(3),
        };
        if (catalog["AcroForm"] is CosReference formReference)
        {
            update.WriteObject(formReference, new CosDictionary(form));
        }
        else
        {
            update.WriteObject(root, With(catalog, ("AcroForm", new CosDictionary(form))));
        }

        (int byteRange, int contentsStart, int contentsEnd) = update.WriteSignature(signature, options);
        update.WriteCrossReferences(cos.Trailer, startXref, cos.LastSectionIsStream, ref next, FileId(file.Span));

        Span<byte> tail = update.Written;
        long length = file.Length + tail.Length;
        long[] ranges = [file.Length + contentsStart, file.Length + contentsEnd, length - (file.Length + contentsEnd)];
        IncrementalUpdate.FillByteRange(tail.Slice(byteRange), ranges);

        using var hash = IncrementalHash.CreateHash(options.Signer.DigestAlgorithm);
        hash.AppendData(file.Span);
        hash.AppendData(tail[..contentsStart]);
        hash.AppendData(tail[contentsEnd..]);
        byte[] cms = await CmsSignature.CreateAsync(options.Signer, hash.GetHashAndReset(), options.Timestamp, cancellationToken).ConfigureAwait(false);
        if (cms.Length > options.ReservedSize)
        {
            throw new PdfException($"The signature is {cms.Length:N0} bytes, more than PdfSignatureOptions.ReservedSize ({options.ReservedSize:N0}). Set ReservedSize to at least {cms.Length}.");
        }

        IncrementalUpdate.FillHex(update.Written.Slice(contentsStart + 1), cms);
        await output.WriteAsync(file, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(update.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A field by its full name ("parent.child"), walking /Kids; fields without a /T are widgets, not fields.</summary>
    private static (CosReference Reference, CosDictionary Field)? FindField(CosFile cos, List<CosValue> fields, string name, string prefix, int depth)
    {
        if (depth > 32)
        {
            return null;
        }

        foreach (CosValue value in fields)
        {
            if (value is not CosReference reference || cos.Resolve(reference) is not CosDictionary field || field["T"] is not CosString title)
            {
                continue;
            }

            string full = prefix + CosPages.Text(title);
            if (full == name)
            {
                return (reference, field);
            }

            if (name.StartsWith(full + ".", StringComparison.Ordinal) && cos.Resolve(field["Kids"]) is CosArray kids)
            {
                if (FindField(cos, kids.Items, name, full + ".", depth + 1) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>A field's type, inherited from its parents if it doesn't have its own.</summary>
    private static string? FieldType(CosFile cos, CosDictionary field)
    {
        for (int depth = 0; depth < 32 && field is not null; depth++)
        {
            if (field["FT"] is CosName type)
            {
                return type.Value;
            }

            field = (cos.Resolve(field["Parent"]) as CosDictionary)!;
        }

        return null;
    }

    /// <summary>The first page and its reference, down the page tree's first kids.</summary>
    private static (CosReference Reference, CosDictionary Page)? FirstPage(CosFile cos)
    {
        CosValue? node = cos.Catalog["Pages"];
        for (int depth = 0; depth < 64; depth++)
        {
            if (node is not CosReference reference || cos.Resolve(reference) is not CosDictionary dictionary)
            {
                return null;
            }

            if (dictionary["Type"] is CosName { Value: "Page" } || dictionary["Kids"] is null)
            {
                return (reference, dictionary);
            }

            node = cos.Resolve(dictionary["Kids"]) is CosArray { Items.Count: > 0 } kids ? kids.Items[0] : null;
        }

        return null;
    }

    private static CosDictionary With(CosDictionary dictionary, (string Key, CosValue Value) entry) =>
        new(new Dictionary<string, CosValue>(dictionary.Entries, StringComparer.Ordinal) { [entry.Key] = entry.Value });

    /// <summary>A text string's bytes: ASCII as it is, anything else UTF-16BE with a byte order mark (written as hex).</summary>
    internal static byte[] TextBytes(string text, out bool hex)
    {
        hex = text.Any(c => c is < ' ' or > '~');
        return hex ? [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)] : Encoding.ASCII.GetBytes(text);
    }

    /// <summary>The update's file identifier: a hash of the original file, so the same input gives the same identifier.</summary>
    private static byte[] FileId(ReadOnlySpan<byte> file) => SHA256.HashData(file)[..16];
}

/// <summary>
/// An incremental update (ISO 32000-1, 7.5.6) in memory: changed and new objects, the signature dictionary with its
/// placeholders, then a cross-reference section of the same kind as the file's last one, chained with /Prev.
/// </summary>
internal sealed class IncrementalUpdate
{
    /// <summary>The width of each byte range number: up to 9,999,999,999 bytes.</summary>
    private const int ByteRangeField = 10;
    private const int ByteRangeLength = 3 + (3 * (ByteRangeField + 1));

    private readonly ArrayBufferWriter<byte> _buffer = new(64 * 1024);
    private readonly long _start; // Where the update starts, counted from the header.
    private readonly SortedDictionary<int, (long Offset, int Generation)> _objects = [];

    public IncrementalUpdate(ReadOnlyMemory<byte> file, int headerOffset)
    {
        _start = file.Length - headerOffset;
        if (file.Length > 0 && file.Span[^1] is not ((byte)'\n' or (byte)'\r'))
        {
            Ascii("\n");
        }
    }

    public Span<byte> Written => MemoryMarshal.AsMemory(_buffer.WrittenMemory).Span;

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.WrittenMemory;

    private long Offset => _start + _buffer.WrittenCount;

    public void WriteObject(CosReference reference, CosValue value)
    {
        _objects[reference.Number] = (Offset, reference.Generation);
        Ascii(string.Create(CultureInfo.InvariantCulture, $"{reference.Number} {reference.Generation} obj\n"));
        Value(value);
        Ascii("\nendobj\n");
    }

    /// <summary>The signature dictionary, last of the objects. Returns where the byte range and the contents' hex string are in the update.</summary>
    public (int ByteRange, int ContentsStart, int ContentsEnd) WriteSignature(CosReference reference, PdfSignatureOptions options)
    {
        _objects[reference.Number] = (Offset, 0);
        Ascii(string.Create(CultureInfo.InvariantCulture, $"{reference.Number} 0 obj\n<</Type /Sig /Filter /Adobe.PPKLite /SubFilter /ETSI.CAdES.detached"));
        TextEntry("Name", options.Name);
        TextEntry("M", Rendering.DocumentWriter.PdfDate(options.SigningTime!.Value));
        TextEntry("Reason", options.Reason);
        TextEntry("Location", options.Location);
        TextEntry("ContactInfo", options.ContactInfo);
        Ascii(" /ByteRange ");
        int byteRange = _buffer.WrittenCount;
        Span<byte> blanks = _buffer.GetSpan(ByteRangeLength)[..ByteRangeLength];
        blanks.Fill((byte)' ');
        "[0"u8.CopyTo(blanks);
        blanks[^1] = (byte)']';
        _buffer.Advance(ByteRangeLength);
        Ascii(" /Contents ");
        int contentsStart = _buffer.WrittenCount;
        int holeLength = 2 + (2 * options.ReservedSize);
        Span<byte> hole = _buffer.GetSpan(holeLength)[..holeLength];
        hole.Fill((byte)'0');
        hole[0] = (byte)'<';
        hole[^1] = (byte)'>';
        _buffer.Advance(holeLength);
        int contentsEnd = _buffer.WrittenCount;
        Ascii(">>\nendobj\n");
        return (byteRange, contentsStart, contentsEnd);
    }

    /// <summary>A cross-reference table or stream for the update's objects, and the trailer, chained to the file's last section.</summary>
    public void WriteCrossReferences(CosDictionary trailer, long previous, bool stream, ref int next, byte[] id)
    {
        byte[] first = trailer["ID"] is CosArray { Items: [CosString original, ..] } ? original.Value : id;
        var entries = new Dictionary<string, CosValue>(StringComparer.Ordinal)
        {
            ["Root"] = trailer["Root"]!,
            ["Prev"] = new CosInteger(previous),
            ["ID"] = new CosArray([new CosString(first, hex: true), new CosString(id, hex: true)]),
        };
        if (trailer["Info"] is CosReference info)
        {
            entries["Info"] = info;
        }

        if (stream)
        {
            int self = next++;
            long offset = Offset;
            _objects[self] = (offset, 0);
            var rows = new List<byte>();
            var index = new List<CosValue>();
            foreach ((int start, int count) in Runs())
            {
                index.Add(new CosInteger(start));
                index.Add(new CosInteger(count));
                for (int n = start; n < start + count; n++)
                {
                    (long at, int generation) = _objects[n];
                    rows.AddRange([1, (byte)(at >> 24), (byte)(at >> 16), (byte)(at >> 8), (byte)at, (byte)(generation >> 8), (byte)generation]);
                }
            }

            entries["Type"] = new CosName("XRef");
            entries["Size"] = new CosInteger(next);
            entries["W"] = new CosArray([new CosInteger(1), new CosInteger(4), new CosInteger(2)]);
            entries["Index"] = new CosArray(index);
            entries["Length"] = new CosInteger(rows.Count);
            Ascii(string.Create(CultureInfo.InvariantCulture, $"{self} 0 obj\n"));
            Value(new CosDictionary(entries));
            Ascii("\nstream\n");
            _buffer.Write(rows.ToArray());
            Ascii("\nendstream\nendobj\n");
            Ascii(string.Create(CultureInfo.InvariantCulture, $"startxref\n{offset}\n%%EOF\n"));
            return;
        }

        long xref = Offset;
        Ascii("xref\n");
        foreach ((int start, int count) in Runs())
        {
            Ascii(string.Create(CultureInfo.InvariantCulture, $"{start} {count}\n"));
            for (int n = start; n < start + count; n++)
            {
                (long at, int generation) = _objects[n];
                Ascii(string.Create(CultureInfo.InvariantCulture, $"{at:D10} {generation:D5} n\r\n"));
            }
        }

        entries["Size"] = new CosInteger(next);
        Ascii("trailer\n");
        Value(new CosDictionary(entries));
        Ascii(string.Create(CultureInfo.InvariantCulture, $"\nstartxref\n{xref}\n%%EOF\n"));
    }

    public static void FillByteRange(Span<byte> byteRange, long[] ranges)
    {
        for (int i = 0; i < ranges.Length; i++)
        {
            _ = ranges[i].TryFormat(byteRange.Slice(3 + (i * (ByteRangeField + 1)), ByteRangeField), out _, provider: CultureInfo.InvariantCulture);
        }
    }

    public static void FillHex(Span<byte> hole, byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            hole[2 * i] = (byte)"0123456789ABCDEF"[bytes[i] >> 4];
            hole[(2 * i) + 1] = (byte)"0123456789ABCDEF"[bytes[i] & 0xF];
        }
    }

    /// <summary>Consecutive object numbers, as cross-reference subsections.</summary>
    private IEnumerable<(int Start, int Count)> Runs()
    {
        int start = -1, count = 0;
        foreach (int n in _objects.Keys)
        {
            if (start >= 0 && n == start + count)
            {
                count++;
                continue;
            }

            if (start >= 0)
            {
                yield return (start, count);
            }

            (start, count) = (n, 1);
        }

        if (start >= 0)
        {
            yield return (start, count);
        }
    }

    private void TextEntry(string key, string? value)
    {
        if (value is not null)
        {
            Ascii($" /{key} ");
            byte[] bytes = PdfSigning.TextBytes(value, out bool hex);
            Value(new CosString(bytes, hex));
        }
    }

    /// <summary>Writes a value as the reader read it: references unchanged, names and strings byte for byte.</summary>
    private void Value(CosValue value, int depth = 0)
    {
        if (depth > 64)
        {
            throw new PdfException("An object in the file is nested too deeply to rewrite.");
        }

        switch (value)
        {
            case CosNull:
                Ascii("null");
                break;
            case CosBoolean boolean:
                Ascii(boolean.Value ? "true" : "false");
                break;
            case CosInteger integer:
                PdfFormat.WriteInteger(_buffer, integer.Value);
                break;
            case CosReal real:
                PdfFormat.WriteReal(_buffer, real.Value);
                break;
            case CosName name:
                PdfFormat.WriteName(_buffer, Encoding.Latin1.GetBytes(name.Value));
                break;
            case CosString text when text.Hex:
                PdfFormat.WriteHexString(_buffer, text.Value);
                break;
            case CosString text:
                PdfFormat.WriteLiteralString(_buffer, text.Value);
                break;
            case CosReference reference:
                Ascii(string.Create(CultureInfo.InvariantCulture, $"{reference.Number} {reference.Generation} R"));
                break;
            case CosArray array:
                Ascii("[");
                for (int i = 0; i < array.Items.Count; i++)
                {
                    if (i > 0)
                    {
                        Ascii(" ");
                    }

                    Value(array.Items[i], depth + 1);
                }

                Ascii("]");
                break;
            case CosDictionary dictionary:
                Ascii("<<");
                foreach ((string key, CosValue item) in dictionary.Entries)
                {
                    PdfFormat.WriteName(_buffer, Encoding.Latin1.GetBytes(key));
                    Ascii(" ");
                    Value(item, depth + 1);
                    Ascii(" ");
                }

                Ascii(">>");
                break;
            default:
                throw new PdfException("An object to update holds a stream or an operator, which can't be rewritten in place.");
        }
    }

    private void Ascii(string text) => _buffer.Write(Encoding.ASCII.GetBytes(text));
}
