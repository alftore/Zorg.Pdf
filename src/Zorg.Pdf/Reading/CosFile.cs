using System;
using System.Collections.Generic;
using Zorg.Pdf.Validation;

namespace Zorg.Pdf.Reading;

/// <summary>
/// The objects of an existing PDF file. Objects are found through the cross-reference tables or streams, following
/// incremental updates (<c>/Prev</c>, newest first) and object streams, and parsed on first use. When that data is
/// damaged, objects are found by scanning the file for <c>N G obj</c>, with a diagnostic. Every loop, count, nesting
/// level and decoded byte is bounded by <see cref="PdfReadLimits"/>; every failure is a <see cref="PdfReadException"/>.
/// Not thread-safe: one reader per caller.
/// </summary>
internal sealed class CosFile
{
    private readonly ReadOnlyMemory<byte> _file;
    private readonly int _base;
    private readonly PdfReadLimits _limits;
    private readonly List<PdfDiagnostic> _diagnostics;
    private readonly Dictionary<int, XrefEntry> _xref = [];
    private readonly Dictionary<int, CosValue> _objects = [];
    private readonly Dictionary<int, ObjectStream> _objectStreams = [];
    private readonly HashSet<int> _resolving = [];
    private Dictionary<int, int>? _scanned;
    private bool _noteLength;
    private bool _noteDamaged;
    private long _decoded;

    public CosFile(ReadOnlyMemory<byte> file, PdfReadLimits limits, List<PdfDiagnostic> diagnostics)
    {
        if (file.Length > limits.MaxFileBytes)
        {
            throw new PdfReadException($"The file is {file.Length:N0} bytes, over the limit of {limits.MaxFileBytes:N0} (PdfReadLimits.MaxFileBytes).");
        }

        _file = file;
        _limits = limits;
        _diagnostics = diagnostics;
        ReadOnlySpan<byte> data = file.Span;
        _base = data[..Math.Min(data.Length, 1024)].IndexOf("%PDF-"u8);
        if (_base < 0)
        {
            throw new PdfReadException("This isn't a PDF file: there's no %PDF- header at its start.");
        }

        Version = HeaderVersion(data[_base..]);
        CosDictionary? trailer = null;
        try
        {
            trailer = ReadCrossReferences();
            if (Resolve(trailer["Root"]) is not CosDictionary)
            {
                trailer = null;
            }
        }
        catch (PdfReadException)
        {
            trailer = null;
        }

        if (trailer is null)
        {
            StartXref = null;
            _xref.Clear();
            _objects.Clear();
            _objectStreams.Clear();
            Note(DiagnosticCodes.ReadRecovered, DiagnosticSeverity.Warning, "The file's cross-reference data is damaged or missing: its objects were found by scanning the file, as viewers do. Check that nothing is missing.");
            trailer = Recover();
        }

        Trailer = trailer;
        IsEncrypted = Trailer["Encrypt"] is not null and not CosNull;
        if (IsEncrypted)
        {
            // Opened with the empty user password, or refused. Objects read so far were read encrypted: read them again.
            _encryptObject = Trailer["Encrypt"] is CosReference r ? r.Number : -1;
            if (Resolve(Trailer["Encrypt"]) is not CosDictionary encrypt)
            {
                throw PdfReadException.Malformed("The encryption dictionary is missing.");
            }

            _security = CosSecurity.Open(encrypt, CosSecurity.FirstId(Trailer, Resolve), Resolve);
            _objects.Clear();
            _objectStreams.Clear();
            string restricted = _security.Restrictions() is { } denied ? $" Its author doesn't allow {denied}; respect that in what you do with it." : "";
            Note(DiagnosticCodes.ReadDecrypted, DiagnosticSeverity.Info, $"The file is encrypted ({_security.Cipher}) but opens without a password, so it was decrypted to read it.{restricted}");
        }
    }

    private readonly int _encryptObject = -1;
    private readonly CosSecurity? _security;

    /// <summary>The last startxref offset, from the header; null when the file was recovered by scanning.</summary>
    public long? StartXref { get; private set; }

    /// <summary>True when the last cross-reference section is a stream (PDF 1.5), false for a table.</summary>
    public bool LastSectionIsStream { get; private set; }

    /// <summary>Where the %PDF- header starts: offsets in the file count from here.</summary>
    public int HeaderOffset => _base;

    /// <summary>The trailer, merged over incremental updates (newest entries win).</summary>
    public CosDictionary Trailer { get; }

    /// <summary>The version in the header, such as "1.7".</summary>
    public string Version { get; }

    /// <summary>True when the trailer names an encryption dictionary. Such a file is read only if it opens without a password, and is then decrypted as it's read.</summary>
    public bool IsEncrypted { get; }

    /// <summary>The catalog (the document's root).</summary>
    public CosDictionary Catalog => Resolve(Trailer["Root"]) as CosDictionary ?? throw PdfReadException.Malformed("The file has no catalog.");

    /// <summary>The highest object number in use, plus one.</summary>
    public int Size
    {
        get
        {
            int size = 0;
            foreach (int n in _xref.Keys)
            {
                size = Math.Max(size, n + 1);
            }

            return size;
        }
    }

    /// <summary>A value with references followed; a missing object is null, as the format says.</summary>
    public CosValue Resolve(CosValue? value)
    {
        for (int hops = 0; value is CosReference reference; hops++)
        {
            if (hops > 32)
            {
                throw PdfReadException.Malformed("References lead in a circle.");
            }

            value = Object(reference.Number);
        }

        return value ?? CosNull.Value;
    }

    /// <summary>A stream's data with its filters undone, counted against the total decode budget.</summary>
    public byte[] Decode(CosStream stream)
    {
        long left = _limits.MaxDecodedBytes - _decoded;
        byte[] data = CosFilters.Decode(stream, Resolve, Math.Min(_limits.MaxStreamBytes, Math.Max(0, left)), out bool damaged);
        _decoded += data.LongLength;
        if (damaged && !_noteDamaged)
        {
            _noteDamaged = true;
            Note(DiagnosticCodes.ReadDamagedStream, DiagnosticSeverity.Warning, "A compressed stream is damaged: what could be read of it was used.");
        }

        return data;
    }

    /// <summary>An object by number.</summary>
    public CosValue Object(int number)
    {
        if (_objects.TryGetValue(number, out CosValue? cached))
        {
            return cached;
        }

        if (!_xref.TryGetValue(number, out XrefEntry entry))
        {
            return CosNull.Value;
        }

        if (_objects.Count >= _limits.MaxObjects)
        {
            throw new PdfReadException($"The file has more than {_limits.MaxObjects:N0} objects (PdfReadLimits.MaxObjects).");
        }

        if (!_resolving.Add(number))
        {
            throw PdfReadException.Malformed($"Object {number} refers to itself while it's read.");
        }

        try
        {
            CosValue value;
            if (entry.Stream >= 0)
            {
                value = FromObjectStream(entry.Stream, entry.Index, number);
            }
            else if (TryDirect(number, entry.Offset, out CosValue? direct))
            {
                value = direct;
            }
            else if (Scanned().TryGetValue(number, out int found) && found != entry.Offset && TryDirect(number, found, out direct))
            {
                Note(DiagnosticCodes.ReadRecovered, DiagnosticSeverity.Warning, $"Object {number} isn't where the cross-reference data says: it was found by scanning the file.");
                value = direct;
            }
            else
            {
                value = CosNull.Value;
            }

            _objects[number] = value;
            return value;
        }
        finally
        {
            _resolving.Remove(number);
        }
    }

    /// <summary>Every object number the cross-reference data lists.</summary>
    public IEnumerable<int> ObjectNumbers => _xref.Keys;

    private static string HeaderVersion(ReadOnlySpan<byte> header)
    {
        // "%PDF-1.7": the digits and point after the dash.
        int end = 5;
        while (end < Math.Min(header.Length, 12) && (char.IsAsciiDigit((char)header[end]) || header[end] == '.'))
        {
            end++;
        }

        return end > 5 ? System.Text.Encoding.ASCII.GetString(header[5..end]) : "1.0";
    }

    private void Note(string code, DiagnosticSeverity severity, string message)
    {
        if (!_diagnostics.Exists(d => d.Code == code && d.Message == message))
        {
            _diagnostics.Add(new PdfDiagnostic(code, severity, message, "file"));
        }
    }

    /// <summary>Reads the cross-reference sections from the last one back through <c>/Prev</c>; the newest entry for an object wins.</summary>
    private CosDictionary ReadCrossReferences()
    {
        ReadOnlySpan<byte> data = _file.Span;
        int tail = Math.Max(0, data.Length - 4096);
        int at = data[tail..].LastIndexOf("startxref"u8);
        if (at < 0)
        {
            throw PdfReadException.Malformed("There's no startxref.");
        }

        var lexer = new CosLexer(data, tail + at + "startxref".Length);
        long offset = lexer.TryInteger() ?? throw PdfReadException.Malformed("startxref has no offset.");
        StartXref = offset;
        LastSectionIsStream = offset >= 0 && _base + offset < data.Length && !data[(int)(_base + offset)..].TrimStart(" \t\r\n\f\0"u8).StartsWith("xref"u8);
        var merged = new Dictionary<string, CosValue>(StringComparer.Ordinal);
        var visited = new HashSet<long>();
        var free = new HashSet<int>();
        for (long? next = offset; next is { } position;)
        {
            if (!visited.Add(position) || visited.Count > 1000)
            {
                break; // A loop in /Prev, or absurdly many updates: what's read so far stands.
            }

            CosDictionary trailer = Section(position, free);
            foreach ((string key, CosValue value) in trailer.Entries)
            {
                if (key is not ("Prev" or "XRefStm" or "Type" or "W" or "Index" or "Length" or "Filter" or "DecodeParms"))
                {
                    merged.TryAdd(key, value);
                }
            }

            if (trailer["XRefStm"]?.AsInteger is { } hybrid)
            {
                // A hybrid file: the table lists objects for old readers, the stream adds the ones in object streams.
                _ = Section(hybrid, free);
            }

            next = trailer["Prev"]?.AsInteger;
        }

        return new CosDictionary(merged);
    }

    /// <summary>One cross-reference table or stream at an offset from the header.</summary>
    private CosDictionary Section(long offset, HashSet<int> free)
    {
        long position = _base + offset;
        ReadOnlySpan<byte> data = _file.Span;
        if (offset < 0 || position >= data.Length)
        {
            throw PdfReadException.Malformed("A cross-reference offset points outside the file.");
        }

        int start = (int)position;
        var probe = new CosLexer(data, start);
        probe.SkipSpace();
        return data[probe.Position..].StartsWith("xref"u8) ? Table(probe.Position + 4, free) : Stream(probe.Position, free);
    }

    private CosDictionary Table(int position, HashSet<int> free)
    {
        var lexer = new CosLexer(_file.Span, position);
        int entries = 0;
        while (true)
        {
            if (lexer.TryKeyword("trailer"))
            {
                return lexer.Read() as CosDictionary ?? throw PdfReadException.Malformed("The trailer isn't a dictionary.");
            }

            long first = lexer.TryInteger() ?? throw PdfReadException.Malformed("A cross-reference table is damaged.");
            long count = lexer.TryInteger() ?? throw PdfReadException.Malformed("A cross-reference table is damaged.");
            if (first < 0 || count < 0 || first + count > int.MaxValue || (entries += (int)Math.Min(count, int.MaxValue)) > _limits.MaxObjects)
            {
                throw PdfReadException.Malformed("A cross-reference section is out of range.");
            }

            for (int i = 0; i < count; i++)
            {
                long entryOffset = lexer.TryInteger() ?? throw PdfReadException.Malformed("A cross-reference entry is damaged.");
                _ = lexer.TryInteger();
                lexer.SkipSpace();
                bool used = lexer.TryKeyword("n");
                if (!used && !lexer.TryKeyword("f"))
                {
                    throw PdfReadException.Malformed("A cross-reference entry is damaged.");
                }

                int number = (int)(first + i);
                if (used && entryOffset > 0)
                {
                    Add(number, new XrefEntry((int)Math.Min(entryOffset + _base, int.MaxValue), -1, 0), free);
                }
                else if (!used)
                {
                    free.Add(number);
                }
            }
        }
    }

    private CosDictionary Stream(int position, HashSet<int> free)
    {
        var lexer = new CosLexer(_file.Span, position);
        long number = lexer.TryInteger() ?? throw PdfReadException.Malformed("startxref doesn't point at a cross-reference table or stream.");
        if (!TryDirect((int)Math.Clamp(number, 0, int.MaxValue), position, out CosValue? value) || value is not CosStream stream || Resolve(stream.Dictionary["Type"]) is not CosName { Value: "XRef" })
        {
            throw PdfReadException.Malformed("startxref doesn't point at a cross-reference table or stream.");
        }

        // Cross-reference streams are never encrypted, so they decode before encryption is known.
        byte[] rows = CosFilters.Decode(stream, Resolve, _limits.MaxStreamBytes, out _);
        if (Resolve(stream.Dictionary["W"]) is not CosArray { Items.Count: 3 } w)
        {
            throw PdfReadException.Malformed("The cross-reference stream has no /W.");
        }

        int[] widths = new int[3];
        for (int i = 0; i < 3; i++)
        {
            widths[i] = (int)Math.Clamp(Resolve(w.Items[i]).AsInteger ?? -1, -1, 9);
            if (widths[i] is < 0 or > 8)
            {
                throw PdfReadException.Malformed("The cross-reference stream's /W is out of range.");
            }
        }

        int columns = widths[0] + widths[1] + widths[2];
        if (columns == 0)
        {
            throw PdfReadException.Malformed("The cross-reference stream's /W is out of range.");
        }

        var sections = new List<(long First, long Count)>();
        if (Resolve(stream.Dictionary["Index"]) is CosArray index)
        {
            for (int i = 0; i + 1 < index.Items.Count; i += 2)
            {
                sections.Add((Resolve(index.Items[i]).AsInteger ?? 0, Resolve(index.Items[i + 1]).AsInteger ?? 0));
            }
        }
        else
        {
            sections.Add((0, Resolve(stream.Dictionary["Size"]).AsInteger ?? 0));
        }

        int row = 0;
        foreach ((long first, long count) in sections)
        {
            if (first < 0 || count < 0 || first + count > int.MaxValue)
            {
                throw PdfReadException.Malformed("A cross-reference stream section is out of range.");
            }

            for (long i = 0; i < count && (row + 1) * columns <= rows.Length; i++, row++)
            {
                ReadOnlySpan<byte> entry = rows.AsSpan(row * columns, columns);
                long type = widths[0] == 0 ? 1 : Field(entry[..widths[0]]);
                long second = Field(entry.Slice(widths[0], widths[1]));
                long third = Field(entry.Slice(widths[0] + widths[1], widths[2]));
                int objectNumber = (int)(first + i);
                if (type == 1 && second > 0 && second + _base < _file.Length)
                {
                    Add(objectNumber, new XrefEntry((int)(second + _base), -1, 0), free);
                }
                else if (type == 2 && second is > 0 and < int.MaxValue && third is >= 0 and < int.MaxValue)
                {
                    Add(objectNumber, new XrefEntry(0, (int)second, (int)third), free);
                }
                else if (type == 0)
                {
                    free.Add(objectNumber);
                }
            }
        }

        return stream.Dictionary;

        static long Field(ReadOnlySpan<byte> bytes)
        {
            long v = 0;
            foreach (byte b in bytes)
            {
                v = (v << 8) | b;
            }

            return v;
        }
    }

    private void Add(int number, XrefEntry entry, HashSet<int> free)
    {
        if (!free.Contains(number) && _xref.Count < _limits.MaxObjects)
        {
            _xref.TryAdd(number, entry);
        }
    }

    /// <summary>Reads <c>N G obj value [stream … endstream]</c> at an offset; false when no object N is there.</summary>
    private bool TryDirect(int number, int offset, out CosValue value)
    {
        value = CosNull.Value;
        ReadOnlySpan<byte> data = _file.Span;
        if (offset < 0 || offset >= data.Length)
        {
            return false;
        }

        var lexer = new CosLexer(data, offset);
        if (lexer.TryInteger() != number || lexer.TryInteger() is not { } generation || !lexer.TryKeyword("obj"))
        {
            return false;
        }

        CosValue parsed = lexer.Read() ?? CosNull.Value;
        if (parsed is CosDictionary dictionary && lexer.TryKeyword("stream"))
        {
            value = ReadStream(dictionary, lexer.Position);
        }
        else
        {
            value = parsed is CosOperator ? CosNull.Value : parsed;
        }

        if (_security is not null && number != _encryptObject)
        {
            value = _security.Decrypt(value, number, (int)Math.Clamp(generation, 0, 65535));
        }

        return true;
    }

    /// <summary>A stream's data: <c>/Length</c> bytes after the EOL, or up to <c>endstream</c> when the length is wrong.</summary>
    private CosStream ReadStream(CosDictionary dictionary, int afterKeyword)
    {
        ReadOnlySpan<byte> data = _file.Span;
        int start = afterKeyword;
        if (start < data.Length && data[start] == '\r')
        {
            start++;
        }

        if (start < data.Length && data[start] == '\n')
        {
            start++;
        }

        long declared = -1;
        try
        {
            declared = Resolve(dictionary["Length"]).AsInteger ?? -1;
        }
        catch (PdfReadException)
        {
            // A /Length that can't be read is found by searching instead.
        }

        if (declared >= 0 && start + declared <= data.Length && EndsAt(data, (int)(start + declared)))
        {
            return new CosStream(dictionary, _file.Slice(start, (int)declared));
        }

        int end = data[start..].IndexOf("endstream"u8);
        if (end < 0)
        {
            throw PdfReadException.Malformed("A stream has no end.");
        }

        int length = end;
        while (length > 0 && data[start + length - 1] is (byte)'\n' or (byte)'\r')
        {
            length--;
        }

        if (!_noteLength)
        {
            _noteLength = true;
            Note(DiagnosticCodes.ReadStreamLength, DiagnosticSeverity.Info, "A stream's /Length is wrong: its end was found by searching for endstream.");
        }

        return new CosStream(dictionary, _file.Slice(start, length));

        static bool EndsAt(ReadOnlySpan<byte> data, int position)
        {
            while (position < data.Length && CosLexer.IsSpace(data[position]))
            {
                position++;
            }

            return data[position..].StartsWith("endstream"u8);
        }
    }

    private CosValue FromObjectStream(int streamNumber, int index, int number)
    {
        if (!_objectStreams.TryGetValue(streamNumber, out ObjectStream? parsed))
        {
            if (Object(streamNumber) is not CosStream stream)
            {
                return CosNull.Value;
            }

            parsed = ParseObjectStream(stream);
            _objectStreams[streamNumber] = parsed;
        }

        // The index is a hint: if it's wrong, look the number up in the stream's own list.
        int at = index >= 0 && index < parsed.Numbers.Length && parsed.Numbers[index] == number ? index : Array.IndexOf(parsed.Numbers, number);
        if (at < 0 || parsed.First + parsed.Offsets[at] >= parsed.Data.Length)
        {
            return CosNull.Value;
        }

        var reader = new CosLexer(parsed.Data, parsed.First + parsed.Offsets[at]);
        return reader.Read() ?? CosNull.Value;
    }

    private ObjectStream ParseObjectStream(CosStream stream)
    {
        byte[] data = Decode(stream);
        long count = Resolve(stream.Dictionary["N"]).AsInteger ?? -1, first = Resolve(stream.Dictionary["First"]).AsInteger ?? -1;
        if (count < 0 || count > _limits.MaxObjects || first < 0 || first > data.Length)
        {
            throw PdfReadException.Malformed("An object stream's header is out of range.");
        }

        var lexer = new CosLexer(data);
        int[] numbers = new int[count], offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            numbers[i] = (int)Math.Clamp(lexer.TryInteger() ?? -1, -1, int.MaxValue);
            offsets[i] = (int)Math.Clamp(lexer.TryInteger() ?? int.MaxValue, 0, int.MaxValue / 2);
        }

        return new ObjectStream(data, (int)first, offsets, numbers);
    }

    /// <summary>Object numbers and offsets found by scanning for <c>N G obj</c>; the last of a number wins, as in an update.</summary>
    private Dictionary<int, int> Scanned()
    {
        if (_scanned is not null)
        {
            return _scanned;
        }

        _scanned = [];
        ReadOnlySpan<byte> data = _file.Span;
        for (int i = data.IndexOf("obj"u8); i >= 0 && i < data.Length;)
        {
            if (i > 0 && (i + 3 == data.Length || CosLexer.IsSpace(data[i + 3]) || CosLexer.IsDelimiter(data[i + 3])) && ObjectHeader(data, i) is { } header)
            {
                if (_scanned.Count >= _limits.MaxObjects)
                {
                    break;
                }

                _scanned[header.Number] = header.Offset;
            }

            int next = data[(i + 3)..].IndexOf("obj"u8);
            i = next < 0 ? -1 : i + 3 + next;
        }

        return _scanned;

        static (int Number, int Offset)? ObjectHeader(ReadOnlySpan<byte> data, int obj)
        {
            // Back over "N G " before "obj".
            int p = obj - 1;
            int spaces = p;
            while (p >= 0 && CosLexer.IsSpace(data[p]))
            {
                p--;
            }

            if (p == spaces)
            {
                return null;
            }

            int genEnd = p;
            while (p >= 0 && char.IsAsciiDigit((char)data[p]) && genEnd - p < 10)
            {
                p--;
            }

            if (p == genEnd || p < 0 || !CosLexer.IsSpace(data[p]))
            {
                return null;
            }

            while (p >= 0 && CosLexer.IsSpace(data[p]))
            {
                p--;
            }

            int numEnd = p;
            while (p >= 0 && char.IsAsciiDigit((char)data[p]) && numEnd - p < 10)
            {
                p--;
            }

            if (p == numEnd || (p >= 0 && !CosLexer.IsSpace(data[p]) && !CosLexer.IsDelimiter(data[p])))
            {
                return null;
            }

            return int.TryParse(data[(p + 1)..(numEnd + 1)], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number)
                ? (number, p + 1)
                : null;
        }
    }

    /// <summary>Rebuilds the cross-reference data by scanning, and finds a trailer or the catalog.</summary>
    private CosDictionary Recover()
    {
        foreach ((int number, int offset) in Scanned())
        {
            _xref[number] = new XrefEntry(offset, -1, 0);
        }

        // Objects in object streams: add those not also stored directly.
        var objectStreams = new List<int>();
        foreach (int number in Scanned().Keys)
        {
            if (Looks(number, "ObjStm"u8))
            {
                objectStreams.Add(number);
            }
        }

        foreach (int streamNumber in objectStreams)
        {
            try
            {
                if (Object(streamNumber) is CosStream stream)
                {
                    ObjectStream parsed = ParseObjectStream(stream);
                    _objectStreams[streamNumber] = parsed;
                    for (int i = 0; i < parsed.Numbers.Length; i++)
                    {
                        if (parsed.Numbers[i] >= 0 && !_xref.ContainsKey(parsed.Numbers[i]))
                        {
                            _xref[parsed.Numbers[i]] = new XrefEntry(0, streamNumber, i);
                        }
                    }
                }
            }
            catch (PdfReadException)
            {
                // A damaged object stream: its objects stay missing.
            }
        }

        // The last trailer dictionary that names a catalog, else a cross-reference stream's, else any catalog.
        ReadOnlySpan<byte> data = _file.Span;
        for (int at = data.LastIndexOf("trailer"u8); at >= 0; at = data[..at].LastIndexOf("trailer"u8))
        {
            try
            {
                var lexer = new CosLexer(data, at + 7);
                if (lexer.Read() is CosDictionary trailer && Resolve(trailer["Root"]) is CosDictionary)
                {
                    return trailer;
                }
            }
            catch (PdfReadException)
            {
                // Try the one before.
            }
        }

        int? catalog = null;
        foreach (int number in Scanned().Keys)
        {
            try
            {
                if (Looks(number, "XRef"u8) && Object(number) is CosStream xref && Resolve(xref.Dictionary["Root"]) is CosDictionary)
                {
                    return xref.Dictionary;
                }

                if (Looks(number, "Catalog"u8) && Object(number) is CosDictionary { } candidate && Resolve(candidate["Type"]) is CosName { Value: "Catalog" })
                {
                    catalog = catalog is null ? number : Math.Max(catalog.Value, number);
                }
            }
            catch (PdfReadException)
            {
                // Skip a damaged object.
            }
        }

        return catalog is { } root
            ? new CosDictionary(new Dictionary<string, CosValue>(StringComparer.Ordinal) { ["Root"] = new CosReference(root, 0) })
            : throw new PdfReadException("The file is too damaged to read: no catalog was found.");
    }

    /// <summary>Whether the first bytes of a scanned object mention a word, to avoid parsing every object.</summary>
    private bool Looks(int number, ReadOnlySpan<byte> word)
    {
        ReadOnlySpan<byte> data = _file.Span;
        int offset = Scanned()[number];
        return data.Slice(offset, Math.Min(400, data.Length - offset)).IndexOf(word) >= 0;
    }

    private readonly record struct XrefEntry(int Offset, int Stream, int Index);

    private sealed record ObjectStream(byte[] Data, int First, int[] Offsets, int[] Numbers);
}
