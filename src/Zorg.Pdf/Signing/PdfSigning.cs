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
using Zorg.Pdf.Fonts;
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

        Rendering.RenderEngine.CheckSignature(options, existingFile: true);
        options = options with { SigningTime = options.SigningTime ?? DateTimeOffset.Now };
        limits ??= PdfReadLimits.Default;

        var read = new List<PdfDiagnostic>();
        var cos = new CosFile(file, limits, read);
        if (cos.IsEncrypted)
        {
            throw new PdfException("The file is encrypted. Signing encrypted files isn't supported yet: sign it before encrypting, or decrypt it first.");
        }

        if (cos.StartXref is not { } startXref || read.Exists(d => d.Code == Validation.DiagnosticCodes.ReadRecovered))
        {
            throw new PdfException("The file's cross-reference data is damaged, so it can't take an incremental update without breaking. Save it again with the program that made it, then sign that.");
        }

        if (cos.Trailer["Root"] is not CosReference root)
        {
            throw new PdfException("The file's catalog isn't an indirect object, so it can't be updated.");
        }

        var update = new IncrementalUpdate(file, cos.HeaderOffset);
        long claimed = Math.Max(cos.Size, cos.Trailer["Size"]?.AsInteger ?? 0);
        if (claimed > limits.MaxObjects)
        {
            // The update numbers its objects after the file's; a claimed size past the limit would also overflow them.
            throw new PdfException($"The file numbers its objects up to {claimed:N0}, more than PdfReadLimits.MaxObjects ({limits.MaxObjects:N0}), so an update can't number its own after them.");
        }

        int next = (int)Math.Max(claimed, 1);
        var signature = new CosReference(next++, 0);
        CosDictionary catalog = cos.Catalog;
        (bool locked, bool noChanges) = Restrictions(cos, catalog);
        if (noChanges)
        {
            throw new PdfException("The file is certified with no changes allowed (DocMDP, P 1): signing it would break its certification.");
        }
        var acroForm = cos.Resolve(catalog["AcroForm"]) as CosDictionary;
        List<CosValue> fields = acroForm is not null && cos.Resolve(acroForm["Fields"]) is CosArray existingFields ? [.. existingFields.Items] : [];

        if (FindField(cos, fields, options.FieldName, "", 0) is { } found)
        {
            // An empty field made for this: give it the signature as its value. Its appearance stays as it was.
            if (FieldType(cos, found.Field) != "Sig")
            {
                throw new PdfException($"The field \"{options.FieldName}\" isn't a signature field. Choose another FieldName.");
            }

            if (cos.Resolve(found.Field["V"]) is not CosNull) // A /V to a missing object is empty.
            {
                throw new PdfException($"The field \"{options.FieldName}\" is signed already. Choose another FieldName: a new field is added for it.");
            }

            CosDictionary field = With(found.Field, ("V", signature));
            if (WidgetOf(cos, found.Reference, field) is { } widget && Size(cos, widget.Widget) is { } size
                && await AppearanceAsync(options, size.Width, size.Height, next, update, cancellationToken).ConfigureAwait(false) is { } drawn)
            {
                // Who signed and when, drawn into the field, as a field signed while rendering shows them.
                next = drawn.Next;
                var appearance = new CosDictionary(new Dictionary<string, CosValue>(StringComparer.Ordinal) { ["N"] = drawn.Form });
                if (widget.Reference.Number == found.Reference.Number)
                {
                    field = With(field, ("AP", appearance));
                }
                else
                {
                    update.WriteObject(widget.Reference, With(widget.Widget, ("AP", appearance)));
                }
            }

            update.WriteObject(found.Reference, field);
        }
        else
        {
            if (locked)
            {
                throw new PdfException($"The file is certified or locked so that only its existing signature fields may be signed, and it has no empty field named \"{options.FieldName}\".");
            }

            if (options.FieldName.Contains('.', StringComparison.Ordinal))
            {
                throw new PdfException($"The file has no field named \"{options.FieldName}\", and a new field's name can't contain a period.");
            }

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

    /// <summary>
    /// What an earlier certification or lock allows (ISO 32000-1, 12.8.2.2 and 12.8.2.4): no changes at all (DocMDP
    /// P 1), or only filling in existing fields and signing them (DocMDP P 2 or 3, or a FieldMDP lock on all fields).
    /// </summary>
    private static (bool Locked, bool NoChanges) Restrictions(CosFile cos, CosDictionary catalog)
    {
        bool locked = false, noChanges = false;
        var signatures = new List<CosDictionary>();
        if (cos.Resolve(catalog["Perms"]) is CosDictionary perms && cos.Resolve(perms["DocMDP"]) is CosDictionary certification)
        {
            signatures.Add(certification);
        }

        if (cos.Resolve(catalog["AcroForm"]) is CosDictionary form && cos.Resolve(form["Fields"]) is CosArray fields)
        {
            foreach (CosValue field in fields.Items.Take(10_000))
            {
                if (cos.Resolve(field) is CosDictionary dictionary && cos.Resolve(dictionary["V"]) is CosDictionary signed)
                {
                    signatures.Add(signed);
                }
            }
        }

        foreach (CosDictionary signature in signatures)
        {
            if (cos.Resolve(signature["Reference"]) is not CosArray references)
            {
                continue;
            }

            foreach (CosValue item in references.Items.Take(100))
            {
                if (cos.Resolve(item) is not CosDictionary reference || cos.Resolve(reference["TransformParams"]) is not CosDictionary parameters)
                {
                    continue;
                }

                string? method = (cos.Resolve(reference["TransformMethod"]) as CosName)?.Value;
                long permission = cos.Resolve(parameters["P"]).AsInteger ?? 2;
                if (method == "DocMDP")
                {
                    noChanges |= permission == 1;
                    locked = true;
                }
                else if (method == "FieldMDP" && ((cos.Resolve(parameters["Action"]) as CosName)?.Value == "All" || permission == 1))
                {
                    noChanges |= permission == 1;
                    locked = true;
                }
            }
        }

        return (locked, noChanges);
    }

    /// <summary>The field's widget: the field itself when they're one dictionary, else its first kid widget.</summary>
    private static (CosReference Reference, CosDictionary Widget)? WidgetOf(CosFile cos, CosReference reference, CosDictionary field)
    {
        if (field["Rect"] is not null)
        {
            return (reference, field);
        }

        if (cos.Resolve(field["Kids"]) is CosArray kids)
        {
            foreach (CosValue kid in kids.Items)
            {
                if (kid is CosReference kidReference && cos.Resolve(kidReference) is CosDictionary widget && widget["Rect"] is not null)
                {
                    return (kidReference, widget);
                }
            }
        }

        return null;
    }

    /// <summary>A widget's width and height from its /Rect, if it's big enough to draw in.</summary>
    private static (double Width, double Height)? Size(CosFile cos, CosDictionary widget)
    {
        if (cos.Resolve(widget["Rect"]) is not CosArray { Items.Count: 4 } rect || rect.Items.Select(i => cos.Resolve(i).AsNumber).ToArray() is not [{ } x0, { } y0, { } x1, { } y1])
        {
            return null;
        }

        double width = Math.Abs(x1 - x0), height = Math.Abs(y1 - y0);
        return width >= 20 && height >= 10 && width <= 2000 && height <= 2000 ? (width, height) : null;
    }

    /// <summary>Families tried, in order, when no appearance font is given: the zorg-pdf tool's defaults.</summary>
    private static readonly string[] SystemFamilies = ["Arial", "Liberation Sans", "DejaVu Sans", "Noto Sans", "Segoe UI"];

    /// <summary>
    /// Draws the signer's details at the field's size with the normal renderer, then appends that page as a form
    /// XObject (marked content removed, its font subset embedded) to the update. Null when no font can be found.
    /// </summary>
    private static async Task<(CosReference Form, int Next)?> AppearanceAsync(PdfSignatureOptions options, double width, double height, int next, IncrementalUpdate update, CancellationToken cancellationToken)
    {
        FontCollection? fonts = null;
        string family = "Signature";
        if (options.AppearanceFont is { } font)
        {
            fonts = FontCollection.Empty.Add(new FontFamily(family) { Regular = font });
        }
        else
        {
            IFontResolver system = FontResolvers.System();
            foreach (string name in SystemFamilies)
            {
                if (await system.ResolveAsync(new FontRequest(name, FontWeight.Normal, FontStyle.Normal), cancellationToken).ConfigureAwait(false) is { } installed)
                {
                    fonts = FontCollection.Empty.Add(new FontFamily(family) { Regular = installed });
                    break;
                }
            }
        }

        if (fonts is null)
        {
            return null;
        }

        var text = new TextStyle { Size = Length.Pt(10), Color = Color.Black }; // Shrunk to fit when the box is small.
        var drawing = new PdfDocument("Signature")
        {
            Page = new PageSettings { Size = new PageSize(Length.Pt(width), Length.Pt(height)), Margin = Thickness.All(Length.Zero) },
            Content =
            [
                new TextBox([.. Rendering.RenderEngine.SignedLines(options).Select(line => (Block)new Paragraph([new TextRun(line) { Style = text }]))])
                {
                    X = Length.Zero,
                    Y = Length.Zero,
                    Width = Length.Pt(width),
                    Height = Length.Pt(height),
                    Padding = Thickness.All(Length.Pt(4)),
                    BorderColor = Color.Gray(0x99),
                    BorderWidth = Length.Pt(0.75),
                    Overflow = TextOverflow.ShrinkToFit,
                },
            ],
        };
        using var rendered = new MemoryStream();
        await drawing.RenderAsync(rendered, new RenderOptions { Fonts = fonts.WithDefault(family) }, cancellationToken).ConfigureAwait(false);

        var source = new CosFile(rendered.ToArray(), PdfReadLimits.Default, []);
        CosPage page = CosPages.Read(source, PdfReadLimits.Default, [])[0];
        byte[] content = CosContent.Read(source, page, out _);
        using var fragment = new MemoryStream();
        using var writer = new PdfObjectWriter(fragment, next);
        var importer = new CosImporter(source, writer, PdfReadLimits.Default.MaxObjects);
        PdfReference form = writer.Allocate();
        writer.BeginStreamObject(form);
        writer.Name("Type"u8);
        writer.Name("XObject"u8);
        writer.Name("Subtype"u8);
        writer.Name("Form"u8);
        writer.Name("BBox"u8);
        writer.BeginArray();
        writer.Integer(0);
        writer.Integer(0);
        writer.Real(width);
        writer.Real(height);
        writer.EndArray();
        writer.Name("Resources"u8);
        importer.Write(page.Resources ?? new CosDictionary([]));
        writer.EndStreamObject(content, compress: true);
        importer.Flush();
        IReadOnlyList<(int Number, long Offset)> objects = writer.FinishFragment();
        update.AppendFragment(fragment.ToArray(), objects);
        return (new CosReference(form.Number, 0), objects.Max(o => o.Number) + 1);
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
            if (value is not CosReference reference || cos.Resolve(reference) is not CosDictionary field)
            {
                continue;
            }

            if (field["T"] is not CosString title)
            {
                // No partial name: its kids' names continue the parent's (ISO 32000-1, 12.7.3.2).
                if (cos.Resolve(field["Kids"]) is CosArray unnamed && FindField(cos, unnamed.Items, name, prefix, depth + 1) is { } inside)
                {
                    return inside;
                }

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
        if (reference.Generation is < 0 or > 65535)
        {
            throw new PdfException($"Object {reference.Number} has generation {reference.Generation}, outside 0 to 65535, so it can't be updated.");
        }

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
                    if (at > uint.MaxValue)
                    {
                        throw new PdfException("The file is over 4 GB, beyond what this update's cross-reference stream records.");
                    }

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

    /// <summary>Appends objects written elsewhere (a <see cref="PdfObjectWriter"/> fragment), given their offsets from its start.</summary>
    public void AppendFragment(ReadOnlySpan<byte> bytes, IReadOnlyList<(int Number, long Offset)> objects)
    {
        long start = Offset;
        _buffer.Write(bytes);
        foreach ((int number, long offset) in objects)
        {
            _objects[number] = (start + offset, 0);
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
                // As read: no rounding, and no exponent, which PDF numbers don't have.
                Ascii(real.Value.ToString("0.##########", CultureInfo.InvariantCulture));
                break;
            case CosName { Value.Length: 0 }:
                Ascii("/"); // The empty name is valid PDF (ISO 32000-1, 7.3.5).
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
                    if (key.Length == 0)
                    {
                        Ascii("/");
                    }
                    else
                    {
                        PdfFormat.WriteName(_buffer, Encoding.Latin1.GetBytes(key));
                    }

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
