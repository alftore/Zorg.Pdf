using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Pdf.Writing;

/// <summary>
/// Writes a PDF file front to back: header, indirect objects, cross-reference table and trailer. Objects are built in
/// a pooled in-memory buffer and flushed to the output asynchronously, so a document can stream to a response body
/// while later pages are still being laid out.
/// </summary>
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

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The caller owns the output stream.")]
    private readonly Stream _output;
    private readonly ArrayBufferWriter<byte> _buffer = new(FlushThreshold + 4096);
    private readonly List<long> _offsets = [-1]; // Object 0 is the head of the free list.
    private IncrementalHash? _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _flushed;
    private int _hashedInBuffer;
    private bool _needsSpace;
    private int _depth;
    private PdfReference _open;
    private bool _openIsStream;
    private bool _finished;
    private bool _writingTrailer;

    public PdfObjectWriter(Stream output, PdfVersion version)
    {
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

    /// <summary>The offset in the file of the next byte to be written.</summary>
    public long Position => _flushed + _buffer.WrittenCount;

    /// <summary>How many object numbers have been allocated so far.</summary>
    public int ObjectCount => _offsets.Count - 1;

    /// <summary>Reserves the next object number. Objects can be written in any order once reserved.</summary>
    public PdfReference Allocate()
    {
        EnsureNotFinished();
        _offsets.Add(-1);
        return new PdfReference(_offsets.Count - 1);
    }

    /// <summary>Starts an indirect object. Write exactly one value, then call <see cref="EndObject"/>.</summary>
    public void BeginObject(PdfReference reference)
    {
        StartObject(reference);
        _openIsStream = false;
    }

    public void EndObject()
    {
        if (!_open.IsValid || _openIsStream || _depth != 0)
        {
            throw new InvalidOperationException("EndObject without a matching BeginObject, or with an open dictionary or array.");
        }

        PdfFormat.WriteAscii(_buffer, "\nendobj\n"u8);
        _open = default;
        _needsSpace = false;
    }

    /// <summary>
    /// Starts a stream object and opens its dictionary. Write any entries other than <c>/Length</c> and
    /// <c>/Filter</c>, then call <see cref="EndStreamObject"/> with the data.
    /// </summary>
    public void BeginStreamObject(PdfReference reference)
    {
        StartObject(reference);
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
        _buffer.WrittenCount >= FlushThreshold ? FlushAsync(cancellationToken) : ValueTask.CompletedTask;

    /// <summary>
    /// Writes the cross-reference table and trailer, and flushes everything. Every allocated object must have been
    /// written. The output stream is flushed but not closed.
    /// </summary>
    public async ValueTask FinishAsync(PdfReference root, PdfReference info = default, CancellationToken cancellationToken = default)
    {
        EnsureNotFinished();
        if (_open.IsValid)
        {
            throw new InvalidOperationException($"Object {_open.Number} is still open.");
        }

        for (int i = 1; i < _offsets.Count; i++)
        {
            if (_offsets[i] < 0)
            {
                throw new InvalidOperationException($"Object {i} was allocated but never written.");
            }
        }

        long xref = Position;
        WriteCrossReferenceTable();

        // The identifier hashes every byte so far, so two different documents never share one.
        HashPending();
        byte[] id = _hash!.GetHashAndReset().AsSpan(0, 16).ToArray();
        _hash.Dispose();
        _hash = null;

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

        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _hash?.Dispose();

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

    private void StartObject(PdfReference reference)
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

        if (_offsets[reference.Number] >= 0)
        {
            throw new InvalidOperationException($"Object {reference.Number} has already been written.");
        }

        _offsets[reference.Number] = Position;
        _open = reference;
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

    private async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        HashPending();
        await _output.WriteAsync(_buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        _flushed += _buffer.WrittenCount;
        _buffer.ResetWrittenCount();
        _hashedInBuffer = 0;
    }

    private void HashPending()
    {
        if (_hash is not null && _buffer.WrittenCount > _hashedInBuffer)
        {
            _hash.AppendData(_buffer.WrittenSpan[_hashedInBuffer..]);
        }

        _hashedInBuffer = _buffer.WrittenCount;
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
