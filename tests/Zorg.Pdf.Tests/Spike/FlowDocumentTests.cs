using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Spike;
using Zorg.Pdf.Tests.Fonts;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Spike;

public sealed class FlowDocumentTests
{
    [Fact]
    public async Task ALongTableFlowsOverPagesWithTheHeaderRepeated()
    {
        (byte[] file, FlowResult result) = await RenderAsync(120);

        Assert.True(result.Pages >= 3, $"120 rows should need several pages, got {result.Pages}.");
        PdfFile.CheckStructure(file);
        string text = PdfFile.Text(file);
        Assert.Equal(result.Pages, Regex.Count(text, @"/Type /Page /Parent"));
        Assert.Contains($"/Type /Pages /Kids [", text, StringComparison.Ordinal);
        Assert.Contains($"/Count {result.Pages}>>", text, StringComparison.Ordinal);

        // Every page after the first starts its table with a repeated header, drawn as a pagination artifact.
        string[] pages = PageContents(file);
        Assert.Equal(result.Pages, pages.Length);
        foreach (string page in pages.Skip(1).SkipLast(1))
        {
            Assert.Contains("/Artifact <</Type /Pagination>> BDC", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EveryPageHasAFooterAndTheTotalComesFromOneFormXObject()
    {
        (byte[] file, FlowResult result) = await RenderAsync(120);

        foreach (string page in PageContents(file))
        {
            Assert.Contains("/Artifact <</Type /Pagination /Subtype /Footer>> BDC", page, StringComparison.Ordinal);
            Assert.Contains("/TP Do", page, StringComparison.Ordinal);
        }

        Assert.Single(Regex.Matches(PdfFile.Text(file), "/Subtype /Form"));
        Assert.True(result.Pages > 1);
    }

    [Fact]
    public async Task IsTaggedForPdfUa()
    {
        (byte[] file, _) = await RenderAsync(60);
        string text = PdfFile.Text(file);

        Assert.Contains("/MarkInfo <</Marked true>>", text, StringComparison.Ordinal);
        Assert.Contains("/Lang (en)", text, StringComparison.Ordinal);
        Assert.Contains("/ViewerPreferences <</DisplayDocTitle true>>", text, StringComparison.Ordinal);
        Assert.Contains("<pdfuaid:part>1</pdfuaid:part>", text, StringComparison.Ordinal);
        Assert.Contains("<rdf:li xml:lang=\"x-default\">Invoice 2026-0042</rdf:li>", text, StringComparison.Ordinal);
        foreach (string role in new[] { "Document", "H1", "P", "Table", "TR", "TH", "TD" })
        {
            Assert.Contains($"/S /{role} /P ", text, StringComparison.Ordinal);
        }

        Assert.Contains("/A <</O /Table /Scope /Column>>", text, StringComparison.Ordinal);
        Assert.Contains("/StructParents 0", text, StringComparison.Ordinal);
        Assert.Contains("/ParentTree", text, StringComparison.Ordinal);

        // A paragraph that crosses a page break refers to its content with MCR dictionaries.
        (byte[] crossing, _) = await RenderAsync(0, string.Concat(Enumerable.Repeat("Lorem ipsum dolor sit amet, consectetur adipiscing elit. ", 400)));
        Assert.Contains("/Type /MCR /Pg", PdfFile.Text(crossing), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkedContentIdsMatchTheParentTree()
    {
        (byte[] file, FlowResult result) = await RenderAsync(80);
        string[] pages = PageContents(file);
        string text = PdfFile.Text(file);
        Match nums = Regex.Match(text, @"/Nums \[(.*?)\]>>", RegexOptions.Singleline);
        Assert.True(nums.Success);

        for (int page = 0; page < result.Pages; page++)
        {
            int[] mcids = [.. Regex.Matches(pages[page], @"<</MCID (\d+)>> BDC").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];
            Assert.Equal(Enumerable.Range(0, mcids.Length), mcids); // 0, 1, 2 ... in order on every page.
            Match entry = Regex.Match(nums.Groups[1].Value, $@"(?<![\d ]\d)\b{page} \[([^\]]*)\]");
            Assert.True(entry.Success, $"No parent tree entry for page {page}.");
            Assert.Equal(mcids.Length, Regex.Count(entry.Groups[1].Value, @"\d+ 0 R"));
        }
    }

    [Fact]
    public async Task IsDeterministic()
    {
        (byte[] first, _) = await RenderAsync(90);
        (byte[] second, _) = await RenderAsync(90);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task StreamsPagesBeforeTheTableEnds()
    {
        // Pages are flushed whenever 64 KiB of them have built up, so a long document reaches the output in pieces,
        // the first of them long before the last row has been read.
        var output = new CountingStream();
        long firstWriteAt = -1;
        output.OnWrite = () => firstWriteAt = firstWriteAt < 0 ? output.Length : firstWriteAt;

        FlowResult result = await FlowDocument.RenderAsync(output, TestFonts.Require(), SpikeInvoice.Options, SpikeInvoice.Blocks(2000), TestContext.Current.CancellationToken);

        Assert.True(result.Pages > 40);
        // The parent tree, font and page tree can only be written at the end, so allow for one large final piece.
        Assert.True(output.Writes >= output.Length / Zorg.Pdf.Writing.PdfObjectWriter.FlushThreshold / 2, $"{output.Writes} writes for {output.Length} bytes.");
        Assert.InRange(firstWriteAt, Zorg.Pdf.Writing.PdfObjectWriter.FlushThreshold, output.Length / 2);
    }

    [Fact]
    public async Task CancellationStopsARenderPartWay()
    {
        using var cancel = new CancellationTokenSource();
        var output = new CountingStream { OnWrite = cancel.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FlowDocument.RenderAsync(output, TestFonts.Require(), SpikeInvoice.Options, SpikeInvoice.Blocks(5000), cancel.Token).AsTask());
        Assert.True(output.Length < 2_000_000, "It should stop soon after the token is cancelled, not render everything.");
    }

    [Fact]
    public async Task RefusesADocumentWithoutATitle()
    {
        PdfException error = await Assert.ThrowsAsync<PdfException>(
            () => FlowDocument.RenderAsync(new MemoryStream(), TestFonts.Require(), new FlowOptions(" "), [], TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("title", error.Message, StringComparison.Ordinal);
    }

    private static async Task<(byte[] File, FlowResult Result)> RenderAsync(int rows, string? extra = null)
    {
        IEnumerable<FlowBlock> blocks = rows > 0 ? SpikeInvoice.Blocks(rows) : [];
        if (extra is not null)
        {
            blocks = blocks.Append(new FlowParagraph(extra, TextAlign.Justify));
        }

        using var output = new MemoryStream();
        FlowResult result = await FlowDocument.RenderAsync(output, TestFonts.Require(), SpikeInvoice.Options, blocks, TestContext.Current.CancellationToken);
        return (output.ToArray(), result);
    }

    /// <summary>The inflated content stream of each page, in page order.</summary>
    private static string[] PageContents(byte[] file)
    {
        string text = PdfFile.Text(file);
        var contents = new List<string>();
        foreach (Match page in Regex.Matches(text, @"/Type /Page /Parent .*?/Contents (\d+) 0 R"))
        {
            string number = page.Groups[1].Value;
            int obj = text.IndexOf($"\n{number} 0 obj\n", StringComparison.Ordinal) + 1;
            int start = text.IndexOf("stream\n", obj, StringComparison.Ordinal) + "stream\n".Length;
            using var zlib = new ZLibStream(new MemoryStream(file, start, file.Length - start), CompressionMode.Decompress);
            using var inflated = new MemoryStream();
            zlib.CopyTo(inflated);
            contents.Add(Encoding.Latin1.GetString(inflated.ToArray()));
        }

        return [.. contents];
    }

    private sealed class CountingStream : MemoryStream
    {
        public int Writes { get; private set; }

        public Action? OnWrite { get; set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            await base.WriteAsync(buffer, cancellationToken);
            OnWrite?.Invoke();
        }
    }
}
