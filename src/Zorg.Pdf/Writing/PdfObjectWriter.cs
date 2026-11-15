using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Signing;

namespace Zorg.Pdf.Writing;

/// <summary>
/// Writes a PDF file front to back: header, indirect objects, cross-reference table and trailer. Objects are built in
/// a pooled in-memory buffer and flushed to the output asynchronously, so a document can stream to a response body
/// while later pages are still being laid out.
/// </summary>
/// <remarks>
/// With object streams (PDF 1.5), objects other than streams are gathered into compressed object streams of up to
/// <see cref="ObjectsPerStream"/> objects, and the file ends with a compressed cross-reference stream instead of a
/// table. Structure elements, page objects and font dictionaries are most of a tagged file's objects, so this is
/// most of its size.
/// </remarks>
/// <remarks>
/// <para>
/// The output is deterministic: the same calls give the same bytes. The file identifier is a hash of everything
/// written before the trailer, not a clock or random value.
/// </para>
/// <para>
/// Values are written with single spaces between tokens and no spaces after opening delimiters, on one line per
/// object. Not thread-safe.
/// </para>
/// </remarks>
internal sealed class PdfObjectWriter : IDisposable
{
    /// <summary>The buffer is flushed to the output once this much has built up.</summary>
    internal const int FlushThreshold = 64 * 1024;

    /// <summary>Objects per object stream: big enough to compress well, small enough for a reader to unpack one quickly.</summary>
    internal const int ObjectsPerStream = 200;

    /// <summary>The offset entry of an object that sits in an object stream; its place is in <see cref="_inStreams"/>.</summary>
    private const long InObjectStream = -2;

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The caller owns the output stream.")]
    private readonly Stream _output;
    private readonly ArrayBufferWriter<byte> _main = new(FlushThreshold + 4096);
    private readonly List<long> _offsets = [-1]; // Object 0 is the head of the free list. -1: allocated, not written yet.
    private readonly bool _objectStreams;
    private readonly ArrayBufferWriter<byte> _objects = new(); // The object stream being filled.
    private readonly List<(int Number, int Offset)> _pending = []; // Its objects and their offsets in it.
    private readonly Dictionary<int, (int Stream, int Index)> _inStreams = [];
    private ArrayBufferWriter<byte> _buffer; // Where values go: the file, or the object stream being filled.
    private bool _openInObjectStream;
    private IncrementalHash? _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _flushed;
    private int _hashedInBuffer;
    private bool _needsSpace;
    private int _depth;
    private PdfReference _open;
    private bool _openIsStream;
    private bool _finished;
    private bool _writingTrailer;
    private readonly bool _synchronous;
    private (PdfReference Reference, PdfSignatureOptions Options)? _signature;
    private IncrementalHash? _documentHash; // The signature's digest of the file, while signing.
    private byte[]? _id;

    /// <param name="output">Where the file goes. Not closed.</param>
    /// <param name="version">The header version.</param>
    /// <param name="synchronous">
    /// Writes with <see cref="Stream.Write(ReadOnlySpan{byte})"/>, so every returned <see cref="ValueTask"/> has already
    /// completed. Used by the synchronous render API, which must never block on a pending task.
    /// </param>
    /// <param name="objectStreams">Gathers objects into object streams and ends with a cross-reference stream (PDF 1.5).</param>
    public PdfObjectWriter(Stream output, PdfVersion version, bool synchronous = false, bool objectStreams = false)
    {
        _buffer = _main;
        _objectStreams = objectStreams;
        _synchronous = synchronous;
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable.", nameof(output));
        }

        _output = output;
        PdfFormat.WriteAscii(_buffer, version == PdfVersion.Pdf20 ? "%PDF-2.0\n"u8 : "%PDF-1.7\n"u8);

        // A comment with four bytes above 127 tells transfer tools the file is binary (ISO 32000-1, 7.5.2).
        PdfFormat.WriteAscii(_buffer, [(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
    }

    /// <summary>The offset of an object number that belongs to the file being updated, not to this fragment.</summary>
    private const long External = -3;

    private readonly int _firstFragmentNumber; // 0, or the first number of a fragment's objects.

    /// <summary>
    /// A fragment: objects numbered from <paramref name="firstNumber"/> on, with no header or trailer, written
    /// synchronously, for an incremental update to append (<see cref="FinishFragment"/> says where each one is).
    /// </summary>
    internal PdfObjectWriter(Stream output, int firstNumber)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstNumber, 1);
        _output = output;
        _buffer = _main;
        _synchronous = true;
        _firstFragmentNumber = firstNumber;
        _hash?.Dispose();
        _hash = null;
        while (_offsets.Count < firstNumber)
        {
            _offsets.Add(External);
        }
    }

    /// <summary>Flushes a fragment and returns each of its objects with its offset from the fragment's start.</summary>
    internal IReadOnlyList<(int Number, long Offset)> FinishFragment()
    {
        if (_firstFragmentNumber == 0 || _open.IsValid)
        {
            throw new InvalidOperationException("FinishFragment needs a fragment writer with no object open.");
        }

        CheckAllWritten(except: 0);
        FlushSync();
        _output.Flush();
        _finished = true;
        var objects = new List<(int, long)>();
        for (int i = _firstFragmentNumber; i < _offsets.Count; i++)
        {
            objects.Add((i, _offsets[i]));
        }

        return objects;
    }

    /// <summary>The offset in the file of the next byte to be written.</summary>
    public long Position => _flushed + _main.WrittenCount;

    /// <summary>How many object numbers have been allocated so far.</summary>
    public int ObjectCount => _offsets.Count - 1;

    /// <summary>Reserves the next object number. Objects can be written in any order once reserved.</summary>
    public PdfReference Allocate()
    {
        EnsureNotFinished();
        _offsets.Add(-1);
        return new PdfReference(_offsets.Count - 1);
    }

    /// <summary>
    /// Signs the file as it's finished: <paramref name="reference"/> becomes the signature dictionary, the last object
    /// before the cross-reference section, with a <c>/ByteRange</c> covering every byte but its <c>/Contents</c>.
    /// Must be called before anything is flushed.
    /// </summary>
    public void Sign(PdfReference reference, PdfSignatureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_flushed > 0 || _hashedInBuffer > 0 || _signature is not null)
        {
            throw new InvalidOperationException("Sign must be called once, before anything is flushed.");
        }

        _signature = (reference, options);
        _documentHash = IncrementalHash.CreateHash(options.Signer.DigestAlgorithm);
    }

    /// <summary>The signature dictionary <see cref="Sign"/> was given, or the default reference when unsigned.</summary>
    public PdfReference SignatureReference => _signature?.Reference ?? default;

    /// <summary>Starts an indirect object. Write exactly one value, then call <see cref="EndObject"/>.</summary>
    public void BeginObject(PdfReference reference)
    {
        StartObject(reference, stream: false);
        _openIsStream = false;
    }

    public void EndObject()
    {
        if (!_open.IsValid || _openIsStream || _depth != 0)
        {
            throw new InvalidOperationException("EndObject without a matching BeginObject, or with an open dictionary or array.");
        }

        _open = default;
        _needsSpace = false;
        if (_openInObjectStream)
        {
            PdfFormat.WriteAscii(_buffer, "\n"u8);
            (_buffer, _openInObjectStream) = (_main, false);
            if (_pending.Count >= ObjectsPerStream)
            {
                WriteObjectStream();
            }

            return;
        }

        PdfFormat.WriteAscii(_buffer, "\nendobj\n"u8);
    }

    /// <summary>
    /// Starts a stream object and opens its dictionary. Write any entries other than <c>/Length</c> and
    /// <c>/Filter</c>, then call <see cref="EndStreamObject"/> with the data.
    /// </summary>
    public void BeginStreamObject(PdfReference reference)
    {
        StartObject(reference, stream: true);
        _openIsStream = true;
        BeginDictionary();
    }

    /// <summary>Closes the stream dictionary, writes the data (compressed with Flate when asked) and ends the object.</summary>
    public void EndStreamObject(ReadOnlySpan<byte> data, bool compress)
    {
        if (!_open.IsValid || !_openIsStream || _depth != 1)
        {
            throw new InvalidOperationException("EndStreamObject without a matching BeginStreamObject, or with a nested value open.");
        }

        byte[]? compressed = null;
        int length = data.Length;
        if (compress)
        {
            compressed = Deflate(data, out length);
            Name("Filter"u8);
            Name("FlateDecode"u8);
        }

        Name("Length"u8);
        Integer(length);
        EndDictionary();
        PdfFormat.WriteAscii(_buffer, "\nstream\n"u8);
        _buffer.Write(compressed is null ? data : compressed.AsSpan(0, length));
        PdfFormat.WriteAscii(_buffer, "\nendstream\nendobj\n"u8);
        if (compressed is not null)
        {
            ArrayPool<byte>.Shared.Return(compressed);
        }

        _open = default;
        _openIsStream = false;
        _needsSpace = false;
    }

    public void BeginDictionary()
    {
        Separate();
        PdfFormat.WriteAscii(_buffer, "<<"u8);
        _depth++;
        _needsSpace = false;
    }

    public void EndDictionary()
    {
        CloseNested();
        PdfFormat.WriteAscii(_buffer, ">>"u8);
        _needsSpace = true;
    }

    public void BeginArray()
    {
        Separate();
        PdfFormat.WriteAscii(_buffer, "["u8);
        _depth++;
        _needsSpace = false;
    }

    public void EndArray()
    {
        CloseNested();
        PdfFormat.WriteAscii(_buffer, "]"u8);
        _needsSpace = true;
    }

    public void Name(ReadOnlySpan<byte> name)
    {
        Separate();
        PdfFormat.WriteName(_buffer, name);
        _needsSpace = true;
    }

    public void Integer(long value)
    {
        Separate();
        PdfFormat.WriteInteger(_buffer, value);
        _needsSpace = true;
    }

    public void Real(double value)
    {
        Separate();
        PdfFormat.WriteReal(_buffer, value);
        _needsSpace = true;
    }

    public void Boolean(bool value)
    {
        Separate();
        PdfFormat.WriteAscii(_buffer, value ? "true"u8 : "false"u8);
        _needsSpace = true;
    }

    public void Null()
    {
        Separate();
        PdfFormat.WriteAscii(_buffer, "null"u8);
        _needsSpace = true;
    }

    public void Reference(PdfReference reference)
    {
        if (!reference.IsValid || reference.Number >= _offsets.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(reference), reference.Number, "The object number wasn't allocated by this writer.");
        }

        Separate();
        PdfFormat.WriteInteger(_buffer, reference.Number);
        PdfFormat.WriteAscii(_buffer, " 0 R"u8);
        _needsSpace = true;
    }

    public void LiteralString(ReadOnlySpan<byte> bytes)
    {
        Separate();
        PdfFormat.WriteLiteralString(_buffer, bytes);
        _needsSpace = true;
    }

    public void HexString(ReadOnlySpan<byte> bytes)
    {
        Separate();
        PdfFormat.WriteHexString(_buffer, bytes);
        _needsSpace = true;
    }

    /// <summary>
    /// Writes a text string (ISO 32000-1, 7.9.2.2): plain ASCII as a literal string, anything else as UTF-16BE with a
    /// byte order mark, which every reader supports.
    /// </summary>
    public void TextString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        bool ascii = true;
        foreach (char c in text)
        {
            if (c is < ' ' or > '~')
            {
                ascii = false;
                break;
            }
        }

        if (ascii)
        {
            Span<byte> bytes = text.Length <= 256 ? stackalloc byte[text.Length] : new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                bytes[i] = (byte)text[i];
            }

            LiteralString(bytes);
            return;
        }

        byte[] utf16 = new byte[2 + (text.Length * 2)];
        utf16[0] = 0xFE;
        utf16[1] = 0xFF;
        for (int i = 0; i < text.Length; i++)
        {
            utf16[2 + (i * 2)] = (byte)(text[i] >> 8);
            utf16[3 + (i * 2)] = (byte)text[i];
        }

        HexString(utf16);
    }

    /// <summary>Flushes the buffer to the output once it has passed <see cref="FlushThreshold"/>. Call it between objects.</summary>
    public ValueTask FlushIfNeededAsync(CancellationToken cancellationToken = default) =>
        _main.WrittenCount >= FlushThreshold ? FlushAsync(cancellationToken) : ValueTask.CompletedTask;

    /// <summary>
    /// Writes the cross-reference table and trailer, and flushes everything. Every allocated object must have been
    /// written. The output stream is flushed but not closed.
    /// </summary>
    public ValueTask FinishAsync(PdfReference root, PdfReference info = default, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_signature is not null)
        {
            return FinishSignedAsync(root, info, cancellationToken);
        }

        WriteTrailer(root, info);
        if (_synchronous)
        {
            FinishSync();
            return ValueTask.CompletedTask;
        }

        return FinishCoreAsync(cancellationToken);
    }

    private void FinishSync()
    {
        FlushSync();
        _output.Flush();
    }

    /// <summary>True once <see cref="FinishAsync"/> has written the trailer; the file size is then <see cref="Position"/>.</summary>
    public bool IsFinished => _finished;

    private async ValueTask FinishCoreAsync(CancellationToken cancellationToken)
    {
        await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void WriteTrailer(PdfReference root, PdfReference info)
    {
        EnsureNotFinished();
        if (_open.IsValid)
        {
            throw new InvalidOperationException($"Object {_open.Number} is still open.");
        }

        if (_objectStreams)
        {
            WriteObjectStream();
        }

        CheckAllWritten(except: 0);
        WriteCrossReferenceAndTrailer(root, info);
    }

    private void CheckAllWritten(int except)
    {
        for (int i = 1; i < _offsets.Count; i++)
        {
            if (_offsets[i] == -1 && i != except) // External numbers belong to the file a fragment is appended to.
            {
                throw new InvalidOperationException($"Object {i} was allocated but never written.");
            }
        }
    }

    private void WriteCrossReferenceAndTrailer(PdfReference root, PdfReference info)
    {
        if (_objectStreams)
        {
            WriteCrossReferenceStream(root, info);
            return;
        }

        long xref = Position;
        WriteCrossReferenceTable();
        byte[] id = TakeId();

        _writingTrailer = true;
        PdfFormat.WriteAscii(_buffer, "trailer\n"u8);
        BeginDictionary();
        Name("Size"u8);
        Integer(_offsets.Count);
        Name("Root"u8);
        Reference(root);
        if (info.IsValid)
        {
            Name("Info"u8);
            Reference(info);
        }

        Name("ID"u8);
        BeginArray();
        HexString(id);
        HexString(id);
        EndArray();
        EndDictionary();
        PdfFormat.WriteAscii(_buffer, "\nstartxref\n"u8);
        PdfFormat.WriteInteger(_buffer, xref);
        PdfFormat.WriteAscii(_buffer, "\n%%EOF\n"u8);
        _finished = true;
    }

    /// <summary>
    /// The file identifier: a hash of every byte so far, so two different documents never share one. A signed
    /// file's covers everything before the signature dictionary.
    /// </summary>
    private byte[] TakeId()
    {
        if (_id is null)
        {
            HashPending();
            _id = _hash!.GetHashAndReset().AsSpan(0, 16).ToArray();
            _hash.Dispose();
            _hash = null;
        }

        return _id;
    }

    /// <summary>The width of each byte range number: up to 9,999,999,999 bytes.</summary>
    private const int ByteRangeField = 10;

    /// <summary>The byte range's text: "[0 " and three blank fields, each followed by a space or the closing bracket.</summary>
    private const int ByteRangeLength = 3 + (3 * (ByteRangeField + 1));

    /// <summary>
    /// Finishes a signed file. Everything is written as usual up to the signature dictionary; from there to the end
    /// (the dictionary, a hole for the signature, the cross-reference section and trailer) stays in the buffer, so the
    /// byte range can be filled in, the rest hashed and signed, and the signature put in the hole before the flush.
    /// </summary>
    private async ValueTask FinishSignedAsync(PdfReference root, PdfReference info, CancellationToken cancellationToken)
    {
        (PdfReference reference, PdfSignatureOptions options) = _signature!.Value;
        EnsureNotFinished();
        if (_open.IsValid)
        {
            throw new InvalidOperationException($"Object {_open.Number} is still open.");
        }

        if (_objectStreams)
        {
            WriteObjectStream();
        }

        CheckAllWritten(except: reference.Number);
        TakeId(); // Also brings the document hash up to the signature dictionary.
        using IncrementalHash documentHash = _documentHash!;
        _documentHash = null;
        int hashedTo = _hashedInBuffer;

        (int byteRange, long contentsStart, long contentsEnd) = WriteSignatureDictionary(reference, options);
        WriteCrossReferenceAndTrailer(root, info);

        // Everything from the signature dictionary on is still in the buffer: fill in the byte range, then hash.
        Span<byte> written = MemoryMarshal.AsMemory(_main.WrittenMemory).Span;
        long[] ranges = [contentsStart, contentsEnd, Position - contentsEnd];
        for (int i = 0; i < ranges.Length; i++)
        {
            if (!ranges[i].TryFormat(written.Slice(byteRange + 3 + (i * (ByteRangeField + 1)), ByteRangeField), out _, provider: System.Globalization.CultureInfo.InvariantCulture))
            {
                throw new PdfException("The file is over 9,999,999,999 bytes, too big for a signature's byte range.");
            }
        }

        int holeStart = (int)(contentsStart - _flushed), holeEnd = (int)(contentsEnd - _flushed);
        documentHash.AppendData(written[hashedTo..holeStart]);
        documentHash.AppendData(written[holeEnd..]);
        byte[] digest = documentHash.GetHashAndReset();

        ValueTask<byte[]> signing = CmsSignature.CreateAsync(options.Signer, digest, options.Timestamp, cancellationToken);
        if (_synchronous && !signing.IsCompleted)
        {
            throw new PdfException("The signer or timestamp client didn't finish synchronously. Render with RenderAsync when the key or the timestamp authority is remote.");
        }

        byte[] cms = await signing.ConfigureAwait(false);
        if (cms.Length > options.ReservedSize)
        {
            throw new PdfException($"The signature is {cms.Length:N0} bytes, more than PdfSignatureOptions.ReservedSize ({options.ReservedSize:N0}). Set ReservedSize to at least {cms.Length}.");
        }

        Span<byte> hole = MemoryMarshal.AsMemory(_main.WrittenMemory).Span[(holeStart + 1)..];
        for (int i = 0; i < cms.Length; i++)
        {
            hole[2 * i] = (byte)"0123456789ABCDEF"[cms[i] >> 4];
            hole[(2 * i) + 1] = (byte)"0123456789ABCDEF"[cms[i] & 0xF];
        }
        if (_synchronous)
        {
            FinishSync();
            return;
        }

        await FinishCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the signature dictionary (ISO 32000-1, 12.8.1) outside any object stream. The byte range is left as
    /// fixed-width blanks and the contents as zeros, both filled in when the rest of the file is known. Returns where
    /// the byte range starts in the buffer, and where the contents' hex string starts and ends in the file.
    /// </summary>
    private (int ByteRange, long ContentsStart, long ContentsEnd) WriteSignatureDictionary(PdfReference reference, PdfSignatureOptions options)
    {
        StartObject(reference, stream: true); // Not in an object stream: its bytes must be addressable.
        _openIsStream = false;
        BeginDictionary();
        Name("Type"u8);
        Name("Sig"u8);
        Name("Filter"u8);
        Name("Adobe.PPKLite"u8);
        Name("SubFilter"u8);
        Name("ETSI.CAdES.detached"u8);
        TextEntry("Name"u8, options.Name);
        Name("M"u8);
        TextString(Rendering.DocumentWriter.PdfDate(options.SigningTime ?? DateTimeOffset.Now));
        TextEntry("Reason"u8, options.Reason);
        TextEntry("Location"u8, options.Location);
        TextEntry("ContactInfo"u8, options.ContactInfo);

        Name("ByteRange"u8);
        Separate();
        int byteRange = _main.WrittenCount;
        Span<byte> blanks = _main.GetSpan(ByteRangeLength)[..ByteRangeLength];
        blanks.Fill((byte)' ');
        "[0"u8.CopyTo(blanks);
        blanks[^1] = (byte)']';
        _main.Advance(ByteRangeLength);
        _needsSpace = true;

        Name("Contents"u8);
        Separate();
        long contentsStart = Position;
        int holeLength = 2 + (2 * options.ReservedSize);
        Span<byte> hole = _main.GetSpan(holeLength)[..holeLength];
        hole.Fill((byte)'0');
        hole[0] = (byte)'<';
        hole[^1] = (byte)'>';
        _main.Advance(holeLength);
        long contentsEnd = Position;
        _needsSpace = true;
        EndDictionary();
        EndObject();
        return (byteRange, contentsStart, contentsEnd);
    }

    private void TextEntry(ReadOnlySpan<byte> key, string? value)
    {
        if (value is not null)
        {
            Name(key);
            TextString(value);
        }
    }

    public void Dispose()
    {
        _hash?.Dispose();
        _documentHash?.Dispose();
    }

    private void WriteCrossReferenceTable()
    {
        PdfFormat.WriteAscii(_buffer, "xref\n0 "u8);
        PdfFormat.WriteInteger(_buffer, _offsets.Count);
        PdfFormat.WriteAscii(_buffer, "\n"u8);

        // Every entry is exactly 20 bytes, ending in CR LF (ISO 32000-1, 7.5.4).
        PdfFormat.WriteAscii(_buffer, "0000000000 65535 f\r\n"u8);
        for (int i = 1; i < _offsets.Count; i++)
        {
            Span<byte> entry = _buffer.GetSpan(20);
            long offset = _offsets[i];
            for (int digit = 9; digit >= 0; digit--)
            {
                entry[digit] = (byte)('0' + (offset % 10));
                offset /= 10;
            }

            " 00000 n\r\n"u8.CopyTo(entry[10..]);
            _buffer.Advance(20);
        }
    }

    private void StartObject(PdfReference reference, bool stream)
    {
        EnsureNotFinished();
        if (_open.IsValid)
        {
            throw new InvalidOperationException($"Object {_open.Number} is still open.");
        }

        if (!reference.IsValid || reference.Number >= _offsets.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(reference), reference.Number, "The object number wasn't allocated by this writer.");
        }

        if (_offsets[reference.Number] != -1)
        {
            throw new InvalidOperationException($"Object {reference.Number} has already been written.");
        }

        _open = reference;
        if (_objectStreams && !stream)
        {
            // Into the object stream: no "obj" header; the stream's own header says where each object starts.
            _offsets[reference.Number] = InObjectStream;
            _pending.Add((reference.Number, _objects.WrittenCount));
            (_buffer, _openInObjectStream) = (_objects, true);
            _needsSpace = false;
            return;
        }

        _offsets[reference.Number] = Position;
        PdfFormat.WriteInteger(_buffer, reference.Number);
        PdfFormat.WriteAscii(_buffer, " 0 obj\n"u8);
        _needsSpace = false;
    }

    private void Separate()
    {
        if (!_open.IsValid && _depth == 0 && !_writingTrailer)
        {
            throw new InvalidOperationException("Values can only be written inside an object.");
        }

        if (_needsSpace)
        {
            _buffer.GetSpan(1)[0] = (byte)' ';
            _buffer.Advance(1);
        }
    }

    private void CloseNested()
    {
        if (_depth == 0)
        {
            throw new InvalidOperationException("There is no open dictionary or array to close.");
        }

        _depth--;
    }

    private void EnsureNotFinished()
    {
        if (_finished)
        {
            throw new InvalidOperationException("The document has already been finished.");
        }
    }

    private ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (!_synchronous)
        {
            return FlushCoreAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        FlushSync();
        return ValueTask.CompletedTask;
    }

    private async ValueTask FlushCoreAsync(CancellationToken cancellationToken)
    {
        HashPending();
        await _output.WriteAsync(_main.WrittenMemory, cancellationToken).ConfigureAwait(false);
        Flushed();
    }

    private void FlushSync()
    {
        HashPending();
        _output.Write(_main.WrittenSpan);
        Flushed();
    }

    private void Flushed()
    {
        _flushed += _main.WrittenCount;
        _main.ResetWrittenCount();
        _hashedInBuffer = 0;
    }

    private void HashPending()
    {
        if (_main.WrittenCount > _hashedInBuffer)
        {
            _hash?.AppendData(_main.WrittenSpan[_hashedInBuffer..]);
            _documentHash?.AppendData(_main.WrittenSpan[_hashedInBuffer..]);
        }

        _hashedInBuffer = _main.WrittenCount;
    }

    /// <summary>Writes the pending objects as one compressed object stream (ISO 32000-1, 7.5.7).</summary>
    private void WriteObjectStream()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var data = new ArrayBufferWriter<byte>(_objects.WrittenCount + (_pending.Count * 12));
        foreach ((int number, int offset) in _pending)
        {
            PdfFormat.WriteInteger(data, number);
            PdfFormat.WriteAscii(data, " "u8);
            PdfFormat.WriteInteger(data, offset);
            PdfFormat.WriteAscii(data, " "u8);
        }

        int first = data.WrittenCount;
        data.Write(_objects.WrittenSpan);
        PdfReference stream = Allocate();
        for (int i = 0; i < _pending.Count; i++)
        {
            _inStreams[_pending[i].Number] = (stream.Number, i);
        }

        int count = _pending.Count;
        _pending.Clear();
        _objects.ResetWrittenCount();
        BeginStreamObject(stream);
        Name("Type"u8);
        Name("ObjStm"u8);
        Name("N"u8);
        Integer(count);
        Name("First"u8);
        Integer(first);
        EndStreamObject(data.WrittenSpan, compress: true);
    }

    /// <summary>
    /// Ends the file with a cross-reference stream (ISO 32000-1, 7.5.8) that carries the trailer entries: rows of
    /// type, offset or object stream, and index, PNG Up-predicted so the Flate data stays small.
    /// </summary>
    private void WriteCrossReferenceStream(PdfReference root, PdfReference info)
    {
        PdfReference self = Allocate();
        long xref = Position; // Where the stream object starts: nothing is written between here and it.
        byte[] id = TakeId();

        int offsetBytes = 1;
        while (offsetBytes < 8 && xref >= 1L << (8 * offsetBytes))
        {
            offsetBytes++;
        }

        int columns = 1 + offsetBytes + 2;
        byte[] rows = new byte[_offsets.Count * (columns + 1)];
        byte[] previous = new byte[columns];
        byte[] row = new byte[columns];
        for (int i = 0; i < _offsets.Count; i++)
        {
            (int type, long field2, int field3) = i == 0 ? (0, 0L, 65535)
                : i == self.Number ? (1, xref, 0)
                : _offsets[i] == InObjectStream ? (2, _inStreams[i].Stream, _inStreams[i].Index)
                : (1, _offsets[i], 0);
            row[0] = (byte)type;
            for (int b = 0; b < offsetBytes; b++)
            {
                row[offsetBytes - b] = (byte)(field2 >> (8 * b));
            }

            row[columns - 2] = (byte)(field3 >> 8);
            row[columns - 1] = (byte)field3;
            int at = i * (columns + 1);
            rows[at] = 2; // PNG Up.
            for (int c = 0; c < columns; c++)
            {
                rows[at + 1 + c] = (byte)(row[c] - previous[c]);
            }

            (previous, row) = (row, previous);
        }

        BeginStreamObject(self);
        Name("Type"u8);
        Name("XRef"u8);
        Name("Size"u8);
        Integer(_offsets.Count);
        Name("W"u8);
        BeginArray();
        Integer(1);
        Integer(offsetBytes);
        Integer(2);
        EndArray();
        Name("Root"u8);
        Reference(root);
        if (info.IsValid)
        {
            Name("Info"u8);
            Reference(info);
        }

        Name("ID"u8);
        BeginArray();
        HexString(id);
        HexString(id);
        EndArray();
        Name("DecodeParms"u8);
        BeginDictionary();
        Name("Predictor"u8);
        Integer(12);
        Name("Columns"u8);
        Integer(columns);
        EndDictionary();
        EndStreamObject(rows, compress: true);
        PdfFormat.WriteAscii(_buffer, "startxref\n"u8);
        PdfFormat.WriteInteger(_buffer, xref);
        PdfFormat.WriteAscii(_buffer, "\n%%EOF\n"u8);
        _finished = true;
    }

    private static byte[] Deflate(ReadOnlySpan<byte> data, out int length)
    {
        using var compressed = new MemoryStream((data.Length / 2) + 64);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        length = (int)compressed.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        compressed.Position = 0;
        compressed.ReadExactly(rented, 0, length);
        return rented;
    }
}
