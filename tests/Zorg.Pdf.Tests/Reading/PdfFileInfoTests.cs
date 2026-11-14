using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Reading;
using Zorg.Pdf.Tests.Rendering;

namespace Zorg.Pdf.Tests.Reading;

/// <summary>Reading existing PDFs: our own, hand-written ones with the forms other writers use, damaged ones and hostile ones.</summary>
public sealed class PdfFileInfoTests
{
    [Fact]
    public async Task OurOwnFilesReadBackWithPagesAndMetadata()
    {
        byte[] file = await Render(3);

        PdfFileInfo info = PdfFileInfo.Read(file);

        Assert.Empty(info.Diagnostics);
        Assert.Equal(3, info.PageCount);
        Assert.All(info.Pages, p => Assert.Equal((595.28, 841.89, 0), (Math.Round(p.Width, 2), Math.Round(p.Height, 2), p.Rotation)));
        Assert.Equal(("Read back", "nb", true, false), (info.Title, info.Language, info.IsTagged, info.IsEncrypted));
        Assert.StartsWith("Zorg.Pdf ", info.Producer, StringComparison.Ordinal);
        Assert.Equal(3, (await PdfFileInfo.ReadAsync(new MemoryStream(file), cancellationToken: TestContext.Current.CancellationToken)).PageCount);
    }

    [Fact]
    public void AClassicFileWithAnIncrementalUpdateReadsTheNewestObjects()
    {
        var pdf = new TestPdf();
        pdf.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>");
        pdf.Add(3, "<< /Type /Page /Parent 2 0 R >>");
        pdf.Add(4, "<< /Title (Old) >>");
        pdf.Section("/Root 1 0 R /Info 4 0 R");
        pdf.Add(2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 /MediaBox [0 0 612 792] >>");
        pdf.Add(5, "<< /Type /Page /Parent 2 0 R /Rotate 90 /CropBox [10 10 310 410] >>");
        pdf.Add(4, "<< /Title <FEFF00480069> /Author (Kari \\(chair\\)) >>");
        pdf.Section("/Root 1 0 R /Info 4 0 R");

        PdfFileInfo info = PdfFileInfo.Read(pdf.Bytes());

        Assert.Empty(info.Diagnostics);
        Assert.Equal(("Hi", "Kari (chair)", false), (info.Title, info.Author, info.IsTagged));
        Assert.Equal([new PdfPageInfo(1, 612, 792, 0), new PdfPageInfo(2, 400, 300, 90)], info.Pages);
    }

    [Fact]
    public void JunkBeforeTheHeaderAndDocEncodedTextAreRead()
    {
        var pdf = new TestPdf("GARBAGE FROM A MAIL GATEWAY\r\n");
        pdf.Add(1, "<< /Type /Catalog /Pages 2 0 R /Lang (en-GB) >>");
        pdf.Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Add(3, "<< /Type /Page /MediaBox [0 0 200 100] >>");
        pdf.Add(4, "<< /Title (Caf\\351 \\215quoted\\216 \\223le) >>"); // PDFDocEncoding: 0x8D and 0x8E are quotes, 0x93 the fi ligature.
        pdf.Section("/Root 1 0 R /Info 4 0 R");

        PdfFileInfo info = PdfFileInfo.Read(pdf.Bytes());

        Assert.Empty(info.Diagnostics);
        Assert.Equal(("Café “quoted” ﬁle", "en-GB"), (info.Title, info.Language));
        Assert.Equal(new PdfPageInfo(1, 200, 100, 0), Assert.Single(info.Pages));
    }

    [Fact]
    public void ADamagedCrossReferenceIsRebuiltByScanning()
    {
        var pdf = new TestPdf();
        pdf.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Add(3, "<< /Type /Page /MediaBox [0 0 300 300] >>");
        pdf.Section("/Root 1 0 R");
        string text = Encoding.Latin1.GetString(pdf.Bytes());
        byte[] shifted = Encoding.Latin1.GetBytes(text.Replace("1 0 obj", "% inserted by an editor\n1 0 obj", StringComparison.Ordinal));
        byte[] noXref = Encoding.Latin1.GetBytes(text[..text.IndexOf("xref", StringComparison.Ordinal)] + "trailer << /Root 1 0 R >>\n%%EOF");

        PdfFileInfo moved = PdfFileInfo.Read(shifted);
        PdfFileInfo missing = PdfFileInfo.Read(noXref);

        Assert.Equal(1, moved.PageCount);
        Assert.Equal("ZPDF-P001", moved.Diagnostics[0].Code);
        Assert.Equal(1, missing.PageCount);
        Assert.Equal("ZPDF-P001", missing.Diagnostics[0].Code);
    }

    [Fact]
    public void AWrongStreamLengthIsFoundBySearching()
    {
        var pdf = new TestPdf();
        pdf.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Add(3, "<< /Type /Page /MediaBox [0 0 300 300] /Contents 4 0 R >>");
        pdf.Add(4, "<< /Length 999 >>\nstream\nBT ET\nendstream");
        pdf.Section("/Root 1 0 R");
        var file = new CosFile(pdf.Bytes(), PdfReadLimits.Default, []);

        var stream = Assert.IsType<CosStream>(file.Object(4));

        Assert.Equal("BT ET", Encoding.ASCII.GetString(stream.Raw.Span));
    }

    [Fact]
    public void ObjectStreamsAndCrossReferenceStreamsWithPredictorsAreRead()
    {
        // What qpdf and Acrobat write: objects packed in an object stream, a PNG-predicted cross-reference stream.
        string[] bodies = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /MediaBox [0 0 100 50] >>"];
        string objects = string.Join(" ", bodies);
        string header = $"1 0 2 {bodies[0].Length + 1} 3 {bodies[0].Length + bodies[1].Length + 2} ";
        byte[] packed = Deflate(Encoding.ASCII.GetBytes(header + objects));
        var head = new StringBuilder("%PDF-1.5\n");
        int objStm = head.Length;
        head.Append(System.Globalization.CultureInfo.InvariantCulture, $"4 0 obj\n<< /Type /ObjStm /N 3 /First {header.Length} /Filter /FlateDecode /Length {packed.Length} >>\nstream\n");
        byte[] before = Encoding.Latin1.GetBytes(head.ToString());
        byte[] after = Encoding.Latin1.GetBytes("\nendstream\nendobj\n");
        int xrefAt = before.Length + packed.Length + after.Length;
        // Rows of 1 + 2 + 1 bytes for objects 0 to 5, each with PNG filter "Up" (2) before it.
        byte[][] rows = [[0, 0, 0, 0], [2, 0, 4, 0], [2, 0, 4, 1], [2, 0, 4, 2], [1, (byte)(objStm >> 8), (byte)objStm, 0], [1, (byte)(xrefAt >> 8), (byte)xrefAt, 0]];
        var predicted = new List<byte>();
        byte[] above = new byte[4];
        foreach (byte[] row in rows)
        {
            predicted.Add(2);
            predicted.AddRange(row.Select((b, i) => (byte)(b - above[i])));
            above = row;
        }

        byte[] xref = Deflate([.. predicted]);
        byte[] xrefHead = Encoding.Latin1.GetBytes($"5 0 obj\n<< /Type /XRef /Size 6 /W [1 2 1] /Root 1 0 R /Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 4 >> /Length {xref.Length} >>\nstream\n");
        byte[] tail = Encoding.Latin1.GetBytes($"\nendstream\nendobj\nstartxref\n{xrefAt}\n%%EOF\n");
        byte[] file = [.. before, .. packed, .. after, .. xrefHead, .. xref, .. tail];

        PdfFileInfo info = PdfFileInfo.Read(file);

        Assert.Empty(info.Diagnostics);
        Assert.Equal(new PdfPageInfo(1, 100, 50, 0), Assert.Single(info.Pages));
    }

    [Fact]
    public void PageTreeLoopsAreSkippedNotFollowed()
    {
        var pdf = new TestPdf();
        pdf.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Add(2, "<< /Type /Pages /Kids [3 0 R 2 0 R 6 0 R 3 0 R] /Count 3 /MediaBox [0 0 10 10] >>");
        pdf.Add(3, "<< /Type /Page >>");
        pdf.Section("/Root 1 0 R");

        PdfFileInfo info = PdfFileInfo.Read(pdf.Bytes());

        Assert.Equal(1, info.PageCount);
        Assert.Equal("ZPDF-P004", Assert.Single(info.Diagnostics).Code);
    }

    [Fact]
    public void EncryptedFilesSayso()
    {
        var pdf = new TestPdf();
        pdf.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Add(3, "<< /Type /Page /MediaBox [0 0 10 10] >>");
        pdf.Add(4, "<< /Title (\\377\\001scrambled) >>");
        pdf.Add(5, "<< /Filter /Standard /V 2 /R 3 /Length 128 >>");
        pdf.Section("/Root 1 0 R /Info 4 0 R /Encrypt 5 0 R");

        PdfFileInfo info = PdfFileInfo.Read(pdf.Bytes());

        Assert.Equal((true, null, 1), (info.IsEncrypted, info.Title, info.PageCount));
    }

    [Fact]
    public void HostileFilesFailWithReadExceptionsWithinTheLimits()
    {
        Assert.Throws<PdfReadException>(() => PdfFileInfo.Read("Hello, world"u8.ToArray()));
        Assert.Throws<PdfReadException>(() => PdfFileInfo.Read(new byte[100], PdfReadLimits.Default with { MaxFileBytes = 50 }));

        // A decompression bomb: 100 MB of zeros in a few kilobytes, read with a 1 MB stream limit.
        byte[] bomb = Deflate(new byte[100_000_000]);
        var pdf = new TestPdf();
        pdf.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Add(3, "<< /Type /Page /MediaBox [0 0 10 10] >>");
        pdf.Add(4, $"<< /Filter /FlateDecode /Length {bomb.Length} >>\nstream\n" + Encoding.Latin1.GetString(bomb) + "\nendstream");
        pdf.Section("/Root 1 0 R");
        var file = new CosFile(pdf.Bytes(), PdfReadLimits.Default with { MaxStreamBytes = 1_000_000 }, []);
        Assert.Contains("MaxStreamBytes", Assert.Throws<PdfReadException>(() => file.Decode((CosStream)file.Object(4))).Message, StringComparison.Ordinal);

        // Nesting deep enough to overflow a recursive parser's stack.
        var deep = new TestPdf();
        deep.Add(1, "<< /Type /Catalog /Pages 2 0 R /X " + new string('[', 100_000) + " >>");
        deep.Add(2, "<< /Type /Pages /Kids [] /Count 0 >>");
        deep.Section("/Root 1 0 R");
        Assert.Throws<PdfReadException>(() => PdfFileInfo.Read(deep.Bytes()));

        // Too many pages for the limit.
        var many = new TestPdf();
        many.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        many.Add(2, "<< /Type /Pages /Kids [" + string.Concat(Enumerable.Repeat("<< /Type /Page >> ", 50)) + "] >>");
        many.Section("/Root 1 0 R");
        Assert.Contains("MaxPages", Assert.Throws<PdfReadException>(() => PdfFileInfo.Read(many.Bytes(), PdfReadLimits.Default with { MaxPages = 10 })).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ASCIIHexDecode", "48 65 6c6C6f>", "Hello")]
    [InlineData("ASCII85Decode", "87cURD]i,\"Ebo80~>", "Hello World!")]
    [InlineData("RunLengthDecode", "\u0002abcýz\u0080", "abczzzz")]
    public void TheTextFiltersDecode(string filter, string raw, string expected)
    {
        var stream = new CosStream(new CosDictionary(new() { ["Filter"] = new CosName(filter) }), Encoding.Latin1.GetBytes(raw));

        Assert.Equal(expected, Encoding.Latin1.GetString(CosFilters.Decode(stream, v => v ?? CosNull.Value, 1000, out bool damaged)));
        Assert.False(damaged);
    }

    [Fact]
    public void LzwDecodesTheSpecificationsExample()
    {
        // ISO 32000-1, 7.4.4.2: "-----A---B" encodes to these bytes.
        var stream = new CosStream(new CosDictionary(new() { ["Filter"] = new CosName("LZWDecode") }), new byte[] { 0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01 });

        Assert.Equal("-----A---B", Encoding.ASCII.GetString(CosFilters.Decode(stream, v => v ?? CosNull.Value, 1000, out _)));
    }

    [Fact]
    public async Task MutatedFilesNeverThrowAnythingButReadExceptions()
    {
        // A small in-repo fuzzer: flipped bytes, cut files, repeated and deleted runs, over our own output (object and
        // cross-reference streams) and a classic file. Fixed seeds, so a failure repeats.
        byte[] ours = await Render(2);
        var classic = new TestPdf();
        classic.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        classic.Add(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 612 792] >>");
        classic.Add(3, "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>");
        classic.Add(4, "<< /Type /Page /Parent 2 0 R /Rotate 270 >>");
        classic.Add(5, "<< /Length 10 /Filter /ASCIIHexDecode >>\nstream\n4254204554>\nendstream");
        classic.Section("/Root 1 0 R");
        var limits = PdfReadLimits.Default with { MaxStreamBytes = 4_000_000, MaxDecodedBytes = 20_000_000 };
        var clock = Stopwatch.StartNew();
        int read = 0, refused = 0;
        foreach ((byte[] seed, int number) in new[] { (ours, 1), (classic.Bytes(), 2) })
        {
            var random = new Random(number);
            for (int i = 0; i < Iterations; i++)
            {
                byte[] mutated = Mutate(seed, random);
                try
                {
                    PdfFileInfo info = PdfFileInfo.Read(mutated, limits);
                    Assert.True(info.PageCount >= 0);
                    read++;
                }
                catch (PdfReadException)
                {
                    refused++;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Seed {number}, case {i}: {ex.GetType().Name}: {ex.Message}", ex);
                }
            }
        }

        Assert.True(read > 500, $"Only {read} of {read + refused} mutated files were read.");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60) * Math.Max(1, Iterations / 1500), $"Fuzzing took {clock.Elapsed}.");
    }

    /// <summary>1,500 per seed file; ZORG_PDF_FUZZ_ITERATIONS runs more, for a long local run.</summary>
    private static int Iterations => int.TryParse(Environment.GetEnvironmentVariable("ZORG_PDF_FUZZ_ITERATIONS"), out int n) && n > 0 ? n : 1500;

    private static byte[] Mutate(byte[] seed, Random random)
    {
        var bytes = new List<byte>(seed);
        int edits = random.Next(1, 6);
        for (int e = 0; e < edits && bytes.Count > 1; e++)
        {
            int at = random.Next(bytes.Count);
            switch (random.Next(6))
            {
                case 0:
                    bytes[at] = (byte)random.Next(256);
                    break;
                case 1:
                    bytes[at] = (byte)"0123456789<>[]()/ \n"[random.Next(19)];
                    break;
                case 2:
                    bytes.RemoveRange(at, Math.Min(random.Next(1, 64), bytes.Count - at));
                    break;
                case 3:
                    int length = Math.Min(random.Next(1, 256), bytes.Count - at);
                    bytes.InsertRange(random.Next(bytes.Count), bytes.GetRange(at, length));
                    break;
                case 4:
                    bytes.RemoveRange(at, bytes.Count - at); // Cut short, as a failed download is.
                    break;
                default:
                    for (int k = at; k < Math.Min(bytes.Count, at + 8); k++)
                    {
                        if (char.IsAsciiDigit((char)bytes[k]))
                        {
                            bytes[k] = (byte)('0' + random.Next(10));
                        }
                    }

                    break;
            }
        }

        return [.. bytes];
    }

    private static async Task<byte[]> Render(int pages)
    {
        var document = new PdfDocument("Read back")
        {
            Language = "nb",
            Content = [.. Enumerable.Range(1, pages).SelectMany(i => i < pages ? new Block[] { new Paragraph($"Page {i}"), new PageBreak() } : [new Paragraph($"Page {i}")])],
        };
        (byte[] file, _) = await RenderTests.RenderAsync(document, PdfInspect.Options(PdfConformance.PdfUA1));
        return file;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>Writes classic PDFs by hand: objects, then a cross-reference table and trailer per section (an incremental update after the first).</summary>
    private sealed class TestPdf(string prefix = "")
    {
        private readonly StringBuilder _text = new(prefix + "%PDF-1.4\n%âãÏÓ\n");
        private readonly int _base = prefix.Length;
        private readonly SortedDictionary<int, int> _offsets = [];
        private int? _previous;
        private int _size;

        public void Add(int number, string body)
        {
            _offsets[number] = _text.Length - _base;
            _text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{number} 0 obj\n{body}\nendobj\n");
            _size = Math.Max(_size, number + 1);
        }

        public void Section(string trailer)
        {
            int at = _text.Length - _base;
            _text.Append("xref\n0 1\n0000000000 65535 f \n");
            foreach ((int number, int offset) in _offsets)
            {
                _text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{number} 1\n{offset:D10} 00000 n \n");
            }

            string prev = _previous is { } p ? $" /Prev {p}" : "";
            _text.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size {_size}{prev} {trailer} >>\nstartxref\n{at}\n%%EOF\n");
            _previous = at;
            _offsets.Clear();
        }

        public byte[] Bytes() => Encoding.Latin1.GetBytes(_text.ToString());
    }
}
