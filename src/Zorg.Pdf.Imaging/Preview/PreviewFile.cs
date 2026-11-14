using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Zorg.Pdf.Imaging.Preview;

/// <summary>A stream object: its dictionary and its raw data.</summary>
internal sealed record PdfStream(PdfDictionary Dictionary, ReadOnlyMemory<byte> Raw);

/// <summary>
/// The objects of a PDF file written by Zorg.Pdf: found through its cross-reference table or stream (object streams
/// included), parsed on first use, with every stream decoded under <see cref="PdfPreviewLimits"/>.
/// </summary>
internal sealed class PreviewFile
{
    private readonly ReadOnlyMemory<byte> _file;
    private readonly PdfPreviewLimits _limits;
    private readonly Dictionary<int, (int Offset, int Stream, int Index)> _xref = [];
    private readonly Dictionary<int, PdfValue> _objects = [];
    private readonly Dictionary<int, PdfStream> _streams = [];
    private readonly Dictionary<int, (byte[] Data, int First, int[] Offsets, int[] Numbers)> _objectStreams = [];
    private readonly HashSet<int> _resolving = [];

    public PreviewFile(ReadOnlyMemory<byte> file, PdfPreviewLimits limits)
    {
        if (file.Length > limits.MaxFileBytes)
        {
            throw new PdfPreviewException($"The file is {file.Length:N0} bytes, over the limit of {limits.MaxFileBytes:N0}.");
        }

        _file = file;
        _limits = limits;
        ReadOnlySpan<byte> data = file.Span;
        if (data.Length < 16 || !data.StartsWith("%PDF-"u8))
        {
            throw PdfPreviewException.Malformed("It doesn't start with %PDF-.");
        }

        int startxref = data[Math.Max(0, data.Length - 1024)..].LastIndexOf("startxref"u8);
        if (startxref < 0)
        {
            throw PdfPreviewException.Malformed("There's no startxref.");
        }

        var lexer = new PdfLexer(data, Math.Max(0, data.Length - 1024) + startxref + "startxref".Length);
        int offset = (lexer.Read() ?? PdfNull.Value).Integer;
        if (offset <= 0 || offset >= data.Length)
        {
            throw PdfPreviewException.Malformed("startxref points outside the file.");
        }

        Trailer = data[offset..].StartsWith("xref"u8) ? ReadTable(offset) : ReadXrefStream(offset);
        if (Trailer["Prev"] is not null)
        {
            throw new PdfPreviewException("The file has been updated incrementally, which Zorg.Pdf never does: previews read only files Zorg.Pdf wrote.");
        }

        if (Trailer["Encrypt"] is not null)
        {
            throw new PdfPreviewException("The file is encrypted, which Zorg.Pdf never does: previews read only files Zorg.Pdf wrote.");
        }
    }

    public PdfDictionary Trailer { get; }

    /// <summary>A value with references followed.</summary>
    public PdfValue Resolve(PdfValue? value)
    {
        for (int hops = 0; value is PdfRef reference; hops++)
        {
            if (hops > 32)
            {
                throw PdfPreviewException.Malformed("References lead in a circle.");
            }

            value = Object(reference.ObjectNumber);
        }

        return value ?? PdfNull.Value;
    }

    public PdfDictionary? Dictionary(PdfValue? value) => Resolve(value) as PdfDictionary;

    /// <summary>The stream a value refers to, or null.</summary>
    public PdfStream? Stream(PdfValue? value)
    {
        if (value is not PdfRef reference)
        {
            return null;
        }

        _ = Object(reference.ObjectNumber);
        return _streams.TryGetValue(reference.ObjectNumber, out PdfStream? stream) ? stream : null;
    }

    /// <summary>A stream's data with its filters undone: Flate (with a PNG predictor), or none; DCT data is returned as it is.</summary>
    public byte[] Decode(PdfStream stream, bool allowDct = false)
    {
        PdfValue? filter = Resolve(stream.Dictionary["Filter"]);
        string? name = filter switch
        {
            PdfName n => n.Value,
            PdfArray { Items.Count: 1 } a when Resolve(a.Items[0]) is PdfName n => n.Value,
            PdfNull => null,
            _ => throw new PdfPreviewException("A stream has more than one filter, which Zorg.Pdf never writes."),
        };
        switch (name)
        {
            case null:
                return stream.Raw.ToArray();
            case "DCTDecode" when allowDct:
                return stream.Raw.ToArray();
            case "FlateDecode":
                byte[] inflated = Inflate(stream.Raw);
                return Dictionary(stream.Dictionary["DecodeParms"]) is { } parms && Resolve(parms["Predictor"]) is PdfNumber { Value: >= 10 }
                    ? Unpredict(inflated, parms)
                    : inflated;
            default:
                throw new PdfPreviewException($"A stream uses the {name} filter, which Zorg.Pdf never writes.");
        }
    }

    private byte[] Inflate(ReadOnlyMemory<byte> raw)
    {
        using var zlib = new ZLibStream(new ReadOnlyMemoryStream(raw), CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        try
        {
            while ((read = zlib.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > _limits.MaxStreamBytes)
                {
                    throw new PdfPreviewException($"A stream inflates to more than {_limits.MaxStreamBytes:N0} bytes (PdfPreviewLimits.MaxStreamBytes).");
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw PdfPreviewException.Malformed($"A Flate stream is damaged: {ex.Message}");
        }

        return output.ToArray();
    }

    /// <summary>Undoes PNG predictors (types 10–15): each row starts with its own filter byte.</summary>
    private byte[] Unpredict(byte[] data, PdfDictionary parms)
    {
        int colors = Resolve(parms["Colors"]) is PdfNumber c ? c.Integer : 1;
        int bits = Resolve(parms["BitsPerComponent"]) is PdfNumber b ? b.Integer : 8;
        int columns = Resolve(parms["Columns"]) is PdfNumber w ? w.Integer : 1;
        if (colors is < 1 or > 4 || bits is not (1 or 2 or 4 or 8 or 16) || columns < 1 || (long)columns * colors * bits > int.MaxValue / 2)
        {
            throw PdfPreviewException.Malformed("A predictor's parameters are out of range.");
        }

        int bpp = Math.Max(1, colors * bits / 8);
        int stride = ((columns * colors * bits) + 7) / 8;
        if (data.Length % (stride + 1) != 0)
        {
            throw PdfPreviewException.Malformed("Predicted data isn't a whole number of rows.");
        }

        int rows = data.Length / (stride + 1);
        byte[] output = new byte[rows * stride];
        for (int row = 0; row < rows; row++)
        {
            int type = data[row * (stride + 1)];
            ReadOnlySpan<byte> input = data.AsSpan((row * (stride + 1)) + 1, stride);
            Span<byte> current = output.AsSpan(row * stride, stride);
            ReadOnlySpan<byte> above = row > 0 ? output.AsSpan((row - 1) * stride, stride) : new byte[stride];
            for (int i = 0; i < stride; i++)
            {
                int left = i >= bpp ? current[i - bpp] : 0;
                int up = above[i];
                int upLeft = i >= bpp ? above[i - bpp] : 0;
                current[i] = (byte)(input[i] + type switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => throw PdfPreviewException.Malformed("A row has an unknown PNG filter."),
                });
            }
        }

        return output;

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }

    /// <summary>An object by number; a stream object is returned as its dictionary and kept in <see cref="_streams"/>.</summary>
    private PdfValue Object(int number)
    {
        if (_objects.TryGetValue(number, out PdfValue? cached))
        {
            return cached;
        }

        if (!_xref.TryGetValue(number, out (int Offset, int Stream, int Index) entry))
        {
            return PdfNull.Value;
        }

        if (!_resolving.Add(number) || _objects.Count > _limits.MaxObjects)
        {
            throw PdfPreviewException.Malformed(_objects.Count > _limits.MaxObjects ? "The file has too many objects." : "An object refers to itself.");
        }

        try
        {
            PdfValue value = entry.Stream >= 0 ? FromObjectStream(entry.Stream, entry.Index, number) : Direct(number, entry.Offset);
            _objects[number] = value;
            return value;
        }
        finally
        {
            _resolving.Remove(number);
        }
    }

    private PdfValue Direct(int number, int offset)
    {
        ReadOnlySpan<byte> data = _file.Span;
        var lexer = new PdfLexer(data, offset);
        if (lexer.Read(operators: true) is not PdfNumber n || n.Integer != number || lexer.Read(operators: true) is not PdfNumber || !lexer.TryKeyword("obj"))
        {
            throw PdfPreviewException.Malformed($"Object {number} isn't where the cross-reference data says.");
        }

        PdfValue value = lexer.Read() ?? throw PdfPreviewException.Malformed($"Object {number} is empty.");
        if (value is PdfDictionary dictionary && lexer.TryKeyword("stream"))
        {
            int start = lexer.Position;
            if (start < data.Length && data[start] == '\r')
            {
                start++;
            }

            if (start < data.Length && data[start] == '\n')
            {
                start++;
            }

            int length = Resolve(dictionary["Length"]).Integer;
            if (length < 0 || start + length > data.Length)
            {
                throw PdfPreviewException.Malformed($"Object {number}'s stream runs past the end of the file.");
            }

            _streams[number] = new PdfStream(dictionary, _file.Slice(start, length));
        }

        return value;
    }

    private PdfValue FromObjectStream(int streamNumber, int index, int number)
    {
        if (!_objectStreams.TryGetValue(streamNumber, out (byte[] Data, int First, int[] Offsets, int[] Numbers) parsed))
        {
            _ = Object(streamNumber);
            PdfStream stream = _streams.TryGetValue(streamNumber, out PdfStream? s) ? s : throw PdfPreviewException.Malformed($"Object stream {streamNumber} isn't a stream.");
            byte[] data = Decode(stream);
            int count = Resolve(stream.Dictionary["N"]).Integer, first = Resolve(stream.Dictionary["First"]).Integer;
            if (count < 0 || count > _limits.MaxObjects || first < 0 || first > data.Length)
            {
                throw PdfPreviewException.Malformed($"Object stream {streamNumber}'s header is out of range.");
            }

            var lexer = new PdfLexer(data);
            int[] numbers = new int[count], offsets = new int[count];
            for (int i = 0; i < count; i++)
            {
                numbers[i] = (lexer.Read(operators: true) ?? PdfNull.Value).Integer;
                offsets[i] = (lexer.Read(operators: true) ?? PdfNull.Value).Integer;
            }

            parsed = (data, first, offsets, numbers);
            _objectStreams[streamNumber] = parsed;
        }

        if (index < 0 || index >= parsed.Numbers.Length || parsed.Numbers[index] != number || parsed.First + parsed.Offsets[index] >= parsed.Data.Length)
        {
            throw PdfPreviewException.Malformed($"Object {number} isn't in object stream {streamNumber} where the cross-reference data says.");
        }

        var reader = new PdfLexer(parsed.Data, parsed.First + parsed.Offsets[index]);
        return reader.Read() ?? throw PdfPreviewException.Malformed($"Object {number} is empty.");
    }

    private PdfDictionary ReadTable(int offset)
    {
        var lexer = new PdfLexer(_file.Span, offset + 4);
        while (true)
        {
            if (lexer.TryKeyword("trailer"))
            {
                return lexer.Read() as PdfDictionary ?? throw PdfPreviewException.Malformed("The trailer isn't a dictionary.");
            }

            int first = (lexer.Read(operators: true) ?? PdfNull.Value).Integer;
            int count = (lexer.Read(operators: true) ?? PdfNull.Value).Integer;
            if (first < 0 || count < 0 || count > _limits.MaxObjects)
            {
                throw PdfPreviewException.Malformed("A cross-reference section is out of range.");
            }

            for (int i = 0; i < count; i++)
            {
                int entryOffset = (lexer.Read(operators: true) ?? PdfNull.Value).Integer;
                _ = lexer.Read(operators: true);
                bool used = lexer.Read(operators: true) is PdfOperator { Name: "n" };
                if (used)
                {
                    _xref[first + i] = (entryOffset, -1, 0);
                }
            }
        }
    }

    private PdfDictionary ReadXrefStream(int offset)
    {
        var lexer = new PdfLexer(_file.Span, offset);
        int number = (lexer.Read(operators: true) ?? PdfNull.Value).Integer;
        PdfValue value = Direct(number, offset);
        if (value is not PdfDictionary dictionary || !_streams.TryGetValue(number, out PdfStream? stream) || Resolve(dictionary["Type"]) is not PdfName { Value: "XRef" })
        {
            throw PdfPreviewException.Malformed("startxref doesn't point at a cross-reference table or stream.");
        }

        byte[] rows = Decode(stream);
        if (Resolve(dictionary["W"]) is not PdfArray { Items.Count: 3 } w)
        {
            throw PdfPreviewException.Malformed("The cross-reference stream has no /W.");
        }

        int[] widths = [Resolve(w.Items[0]).Integer, Resolve(w.Items[1]).Integer, Resolve(w.Items[2]).Integer];
        int columns = widths[0] + widths[1] + widths[2];
        if (widths[0] is < 0 or > 4 || widths[1] is < 0 or > 4 || widths[2] is < 0 or > 4 || columns == 0)
        {
            throw PdfPreviewException.Malformed("The cross-reference stream's /W is out of range.");
        }

        var sections = new List<(int First, int Count)>();
        if (Resolve(dictionary["Index"]) is PdfArray index)
        {
            for (int i = 0; i + 1 < index.Items.Count; i += 2)
            {
                sections.Add((Resolve(index.Items[i]).Integer, Resolve(index.Items[i + 1]).Integer));
            }
        }
        else
        {
            sections.Add((0, Resolve(dictionary["Size"]).Integer));
        }

        int row = 0;
        foreach ((int first, int count) in sections)
        {
            for (int i = 0; i < count; i++, row++)
            {
                if ((row + 1) * columns > rows.Length)
                {
                    throw PdfPreviewException.Malformed("The cross-reference stream is shorter than its /Size.");
                }

                ReadOnlySpan<byte> entry = rows.AsSpan(row * columns, columns);
                long type = widths[0] == 0 ? 1 : Field(entry[..widths[0]]);
                long second = Field(entry.Slice(widths[0], widths[1]));
                long third = Field(entry.Slice(widths[0] + widths[1], widths[2]));
                if (type == 1 && second < _file.Length)
                {
                    _xref[first + i] = ((int)second, -1, 0);
                }
                else if (type == 2 && second < int.MaxValue && third < int.MaxValue)
                {
                    _xref[first + i] = (0, (int)second, (int)third);
                }
            }
        }

        _objects[number] = dictionary;
        return dictionary;

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

    /// <summary>A read-only stream over memory, for ZLibStream.</summary>
    private sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> memory) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => memory.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(buffer.Length, memory.Length - _position);
            memory.Span.Slice(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
