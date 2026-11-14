using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Tests.Writing;

public sealed class PdfObjectWriterTests
{
    [Fact]
    public async Task WritesObjectsXrefAndTrailer()
    {
        byte[] file = await WriteAsync(writer =>
        {
            PdfReference root = writer.Allocate();
            PdfReference child = writer.Allocate();

            // Out of order on purpose: the table must still be indexed by object number.
            writer.BeginObject(child);
            writer.BeginArray();
            writer.Integer(1);
            writer.Real(2.5);
            writer.Boolean(true);
            writer.Null();
            writer.LiteralString("x"u8);
            writer.EndArray();
            writer.EndObject();

            writer.BeginObject(root);
            writer.BeginDictionary();
            writer.Name("Type"u8);
            writer.Name("Catalog"u8);
            writer.Name("Kid"u8);
            writer.Reference(child);
            writer.EndDictionary();
            writer.EndObject();
            return root;
        });

        PdfFile.CheckStructure(file);
        string text = PdfFile.Text(file);
        Assert.Contains("2 0 obj\n[1 2.5 true null (x)]\nendobj\n", text, StringComparison.Ordinal);
        Assert.Contains("1 0 obj\n<</Type /Catalog /Kid 2 0 R>>\nendobj\n", text, StringComparison.Ordinal);
        Assert.Contains("/Root 1 0 R /ID [<", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameInputGivesTheSameBytes()
    {
        byte[] first = await WriteAsync(OneObject("same"));
        byte[] second = await WriteAsync(OneObject("same"));
        byte[] other = await WriteAsync(OneObject("different"));

        Assert.Equal(first, second);
        Assert.NotEqual(Id(first), Id(other));
    }

    [Fact]
    public async Task StreamsAreCompressedWithTheirLength()
    {
        byte[] data = new byte[5000];
        Array.Fill(data, (byte)'A');
        byte[] file = await WriteAsync(writer =>
        {
            PdfReference root = writer.Allocate();
            writer.BeginStreamObject(root);
            writer.Name("Type"u8);
            writer.Name("Test"u8);
            writer.EndStreamObject(data, compress: true);
            return root;
        });

        PdfFile.CheckStructure(file);
        string text = PdfFile.Text(file);
        const string Marker = "/Filter /FlateDecode /Length ";
        int at = text.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(at > 0);
        int length = int.Parse(text.AsSpan(at + Marker.Length, text.IndexOf('>', at) - (at + Marker.Length)), System.Globalization.CultureInfo.InvariantCulture);
        int start = text.IndexOf("stream\n", at, StringComparison.Ordinal) + "stream\n".Length;
        Assert.Equal("\nendstream", text.Substring(start + length, 10));

        using var zlib = new ZLibStream(new MemoryStream(file, start, length), CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        await zlib.CopyToAsync(inflated, TestContext.Current.CancellationToken);
        Assert.Equal(data, inflated.ToArray());
    }

    [Fact]
    public async Task FlushesToTheOutputBeforeTheEnd()
    {
        var output = new CountingStream();
        using var writer = new PdfObjectWriter(output, PdfVersion.Pdf17);
        PdfReference root = writer.Allocate();
        for (int i = 0; i < 200; i++)
        {
            PdfReference stream = writer.Allocate();
            writer.BeginStreamObject(stream);
            writer.EndStreamObject(new byte[1000], compress: false);
            await writer.FlushIfNeededAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(output.Writes >= 2, $"Expected the writer to flush while writing, but it wrote {output.Writes} times.");
        writer.BeginObject(root);
        writer.Null();
        writer.EndObject();
        await writer.FinishAsync(root, cancellationToken: TestContext.Current.CancellationToken);
        PdfFile.CheckStructure(output.ToArray());
    }

    [Fact]
    public async Task NonAsciiTextStringsAreUtf16WithByteOrderMark()
    {
        byte[] file = await WriteAsync(writer =>
        {
            PdfReference root = writer.Allocate();
            writer.BeginObject(root);
            writer.BeginArray();
            writer.TextString("Plain");
            writer.TextString("Blåbær");
            writer.EndArray();
            writer.EndObject();
            return root;
        });

        Assert.Contains("[(Plain) <FEFF0042006C00E5006200E60072>]", PdfFile.Text(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAnAllocatedObjectThatWasNeverWritten()
    {
        using var writer = new PdfObjectWriter(new MemoryStream(), PdfVersion.Pdf17);
        PdfReference root = writer.Allocate();
        writer.Allocate();
        writer.BeginObject(root);
        writer.Null();
        writer.EndObject();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.FinishAsync(root, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("Object 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesMisuse()
    {
        using var writer = new PdfObjectWriter(new MemoryStream(), PdfVersion.Pdf17);
        PdfReference a = writer.Allocate();

        Assert.Throws<InvalidOperationException>(() => writer.Integer(1)); // Outside an object.
        writer.BeginObject(a);
        Assert.Throws<InvalidOperationException>(() => writer.BeginObject(a)); // Still open.
        writer.BeginDictionary();
        Assert.Throws<InvalidOperationException>(writer.EndObject); // Dictionary open.
        writer.EndDictionary();
        writer.EndObject();
        Assert.Throws<InvalidOperationException>(() => writer.BeginObject(a)); // Written twice.
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.BeginObject(new PdfReference(99)));
    }

    [Fact]
    public async Task HonoursCancellation()
    {
        using var writer = new PdfObjectWriter(new MemoryStream(), PdfVersion.Pdf17);
        PdfReference root = writer.Allocate();
        writer.BeginObject(root);
        writer.Null();
        writer.EndObject();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => writer.FinishAsync(root, cancellationToken: new CancellationToken(canceled: true)).AsTask());
    }

    private static Func<PdfObjectWriter, PdfReference> OneObject(string text) => writer =>
    {
        PdfReference root = writer.Allocate();
        writer.BeginObject(root);
        writer.TextString(text);
        writer.EndObject();
        return root;
    };

    private static string Id(byte[] file)
    {
        string text = PdfFile.Text(file);
        int at = text.IndexOf("/ID [<", StringComparison.Ordinal) + 6;
        return text.Substring(at, 32);
    }

    [Fact]
    public async Task ObjectStreamsHoldEveryObjectButStreamsAndAnXrefStreamFindsThem()
    {
        const int Count = 450; // Three object streams: 200, 200, and the rest with the catalog.
        Func<PdfObjectWriter, PdfReference> body = writer =>
        {
            PdfReference root = writer.Allocate();
            PdfReference content = writer.Allocate();
            writer.BeginStreamObject(content);
            writer.EndStreamObject("BT ET"u8, compress: true);
            for (int i = 0; i < Count; i++)
            {
                PdfReference item = writer.Allocate();
                writer.BeginObject(item);
                writer.BeginDictionary();
                writer.Name("Index"u8);
                writer.Integer(i);
                writer.EndDictionary();
                writer.EndObject();
            }

            writer.BeginObject(root);
            writer.BeginDictionary();
            writer.Name("Type"u8);
            writer.Name("Catalog"u8);
            writer.Name("Content"u8);
            writer.Reference(content);
            writer.EndDictionary();
            writer.EndObject();
            return root;
        };
        byte[] file = await WriteAsync(body, objectStreams: true);

        PdfFile.CheckStructure(file);
        string raw = PdfFile.Text(file);
        Assert.DoesNotContain("\nxref\n", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("trailer", raw, StringComparison.Ordinal);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Count(raw, "/Type /ObjStm"));

        var objects = PdfReader.Read(file);
        Assert.Equal("<</Type /Catalog /Content 2 0 R>>", objects[1].Value);
        Assert.Equal(Count, objects.Values.Count(o => o.Value.StartsWith("<</Index ", StringComparison.Ordinal))); // Object streams get numbers in between.
        Assert.Equal("BT ET", objects[2].DecodedText); // Streams stay outside object streams.
        Assert.Matches(@"/Type /XRef /Size \d+ /W \[1 \d 2\] /Root 1 0 R /ID \[<[0-9A-F]{32}> <[0-9A-F]{32}>\]", raw);
        Assert.Equal(file, await WriteAsync(body, objectStreams: true));
    }

    private static async Task<byte[]> WriteAsync(Func<PdfObjectWriter, PdfReference> body, bool objectStreams = false)
    {
        using var output = new MemoryStream();
        using (var writer = new PdfObjectWriter(output, PdfVersion.Pdf17, objectStreams: objectStreams))
        {
            PdfReference root = body(writer);
            await writer.FinishAsync(root, cancellationToken: TestContext.Current.CancellationToken);
        }

        return output.ToArray();
    }

    private sealed class CountingStream : MemoryStream
    {
        public int Writes { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
