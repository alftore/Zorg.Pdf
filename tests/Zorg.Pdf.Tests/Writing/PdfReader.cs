using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Zorg.Pdf.Tests.Writing;

/// <summary>One object of a file as text: its value (a stream's dictionary), and a stream's data raw and decoded.</summary>
internal sealed record PdfObjectView(int Number, string Value, byte[]? Raw, byte[]? Decoded)
{
    public bool IsStream => Raw is not null;

    /// <summary>The decoded stream data as Latin-1 text, for content streams and CMaps.</summary>
    public string DecodedText => Decoded is null ? "" : Encoding.Latin1.GetString(Decoded);
}

/// <summary>
/// A small reader for the files this library writes, for tests: the cross-reference table or stream, object streams,
/// and Flate streams with or without the PNG predictor. It follows the file's own cross-reference data, so a test
/// that reads an object through it also checks the object is where the file says. It's strict where the writer is
/// regular and is no general PDF parser; qpdf and veraPDF check files properly in the Linux gate.
/// </summary>
internal static partial class PdfReader
{
    public static SortedDictionary<int, PdfObjectView> Read(byte[] file)
    {
        string text = Encoding.Latin1.GetString(file);
        Assert.StartsWith("%PDF-1.", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        int startxref = text.LastIndexOf("startxref\n", StringComparison.Ordinal);
        Assert.True(startxref > 0, "No startxref.");
        int numberStart = startxref + "startxref\n".Length;
        int xref = int.Parse(text.AsSpan(numberStart, text.IndexOf('\n', numberStart) - numberStart), CultureInfo.InvariantCulture);

        var objects = new SortedDictionary<int, PdfObjectView>();
        var inStreams = new List<(int Number, int Stream, int Index)>();
        if (text.AsSpan(xref).StartsWith("xref\n", StringComparison.Ordinal))
        {
            foreach ((int number, long offset) in Table(text, xref))
            {
                objects[number] = Direct(file, text, number, (int)offset);
            }
        }
        else
        {
            Match header = ObjectHeader().Match(text, xref);
            Assert.True(header.Success && header.Index == xref, "startxref doesn't point at an object.");
            PdfObjectView stream = Direct(file, text, int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture), xref);
            Assert.Contains("/Type /XRef", stream.Value, StringComparison.Ordinal);
            int size = Int(stream.Value, "Size");
            Match w = Regex.Match(stream.Value, @"/W \[(\d+) (\d+) (\d+)\]");
            int[] widths = [.. Enumerable.Range(1, 3).Select(g => int.Parse(w.Groups[g].Value, CultureInfo.InvariantCulture))];
            byte[] rows = stream.Decoded!;
            int columns = widths[0] + widths[1] + widths[2];
            Assert.Equal(size * columns, rows.Length);
            for (int i = 0; i < size; i++)
            {
                ReadOnlySpan<byte> row = rows.AsSpan(i * columns, columns);
                long type = Field(row[..widths[0]]), second = Field(row.Slice(widths[0], widths[1])), third = Field(row[(widths[0] + widths[1])..]);
                switch (type)
                {
                    case 0:
                        Assert.Equal(0, i); // Only object 0 is free in a file this library writes.
                        break;
                    case 1:
                        objects[i] = Direct(file, text, i, (int)second);
                        break;
                    case 2:
                        inStreams.Add((i, (int)second, (int)third));
                        break;
                    default:
                        Assert.Fail($"Cross-reference entry {i} has type {type}.");
                        break;
                }
            }

            foreach ((int number, int streamNumber, int index) in inStreams)
            {
                objects[number] = FromObjectStream(objects[streamNumber], number, index);
            }
        }

        return objects;
    }

    /// <summary>Every object as text, in object number order, with streams decoded: for searching a file in tests.</summary>
    public static string Flatten(byte[] file)
    {
        var text = new StringBuilder();
        foreach (PdfObjectView o in Read(file).Values)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n{o.Number} 0 obj\n{o.Value}");
            if (o.IsStream && !o.Value.Contains("/Type /ObjStm", StringComparison.Ordinal) && !o.Value.Contains("/Type /XRef", StringComparison.Ordinal)) // Their objects are listed on their own.
            {
                text.Append("\nstream\n").Append(o.DecodedText).Append("\nendstream");
            }

            text.Append("\nendobj\n");
        }

        return text.ToString();
    }

    private static IEnumerable<(int Number, long Offset)> Table(string text, int xref)
    {
        int lineEnd = text.IndexOf('\n', xref + 5);
        int count = int.Parse(text.AsSpan(xref + 7, lineEnd - (xref + 7)), CultureInfo.InvariantCulture);
        int entries = lineEnd + 1;
        Assert.Equal("0000000000 65535 f\r\n", text.Substring(entries, 20));
        for (int i = 1; i < count; i++)
        {
            string entry = text.Substring(entries + (i * 20), 20);
            Assert.EndsWith(" 00000 n\r\n", entry, StringComparison.Ordinal);
            yield return (i, long.Parse(entry.AsSpan(0, 10), CultureInfo.InvariantCulture));
        }
    }

    private static PdfObjectView Direct(byte[] file, string text, int number, int offset)
    {
        string header = $"{number} 0 obj\n";
        Assert.Equal(header, text.Substring(offset, header.Length));
        int start = offset + header.Length;
        int end = text.IndexOf("\nendobj\n", start, StringComparison.Ordinal);
        int stream = text.IndexOf("\nstream\n", start, StringComparison.Ordinal);
        if (stream < 0 || stream > end)
        {
            return new PdfObjectView(number, text[start..end], null, null);
        }

        string dictionary = text[start..stream];
        int length = Int(dictionary, "Length");
        int data = stream + "\nstream\n".Length;
        Assert.Equal("\nendstream\nendobj\n", text.Substring(data + length, "\nendstream\nendobj\n".Length));
        byte[] raw = file.AsSpan(data, length).ToArray();
        return new PdfObjectView(number, dictionary, raw, Decode(dictionary, raw));
    }

    private static PdfObjectView FromObjectStream(PdfObjectView stream, int number, int index)
    {
        Assert.Contains("/Type /ObjStm", stream.Value, StringComparison.Ordinal);
        int count = Int(stream.Value, "N"), first = Int(stream.Value, "First");
        string data = stream.DecodedText;
        string[] header = data[..first].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(count * 2, header.Length);
        Assert.Equal(number, int.Parse(header[index * 2], CultureInfo.InvariantCulture));
        int start = first + int.Parse(header[(index * 2) + 1], CultureInfo.InvariantCulture);
        int end = index + 1 < count ? first + int.Parse(header[(index * 2) + 3], CultureInfo.InvariantCulture) : data.Length;
        return new PdfObjectView(number, data[start..end].TrimEnd('\n'), null, null);
    }

    /// <summary>Inflates Flate data and undoes a PNG predictor; anything else stays as it is.</summary>
    private static byte[] Decode(string dictionary, byte[] raw)
    {
        if (!dictionary.Contains("/Filter /FlateDecode", StringComparison.Ordinal))
        {
            return raw;
        }

        using var zlib = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        zlib.CopyTo(inflated);
        byte[] data = inflated.ToArray();
        Match predictor = Regex.Match(dictionary, @"/DecodeParms <</Predictor (\d+) (?:/Colors (\d+) /BitsPerComponent (\d+) )?/Columns (\d+)>>");
        if (!predictor.Success || predictor.Groups[2].Success)
        {
            return data; // No predictor, or an image's: images are checked by the image tests.
        }

        int columns = int.Parse(predictor.Groups[4].Value, CultureInfo.InvariantCulture);
        Assert.Equal(0, data.Length % (columns + 1));
        byte[] output = new byte[data.Length / (columns + 1) * columns];
        for (int row = 0; row < data.Length / (columns + 1); row++)
        {
            Assert.Equal(2, data[row * (columns + 1)]); // PNG Up, as the writer uses.
            for (int c = 0; c < columns; c++)
            {
                byte above = row > 0 ? output[((row - 1) * columns) + c] : (byte)0;
                output[(row * columns) + c] = (byte)(data[(row * (columns + 1)) + 1 + c] + above);
            }
        }

        return output;
    }

    private static int Int(string dictionary, string key) =>
        int.Parse(Regex.Match(dictionary, $@"/{key} (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);

    private static long Field(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        foreach (byte b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    [GeneratedRegex(@"(\d+) 0 obj\n")]
    private static partial Regex ObjectHeader();
}
