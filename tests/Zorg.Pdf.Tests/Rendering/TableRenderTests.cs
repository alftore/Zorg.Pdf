using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

public sealed class TableRenderTests
{
    /// <summary>The reference invoice: a heading, text, and a table bound to the slot "lines".</summary>
    internal static readonly PdfDocument Invoice = new("Invoice 2026-0042")
    {
        Content =
        [
            new Heading("Invoice 2026-0042"),
            new Paragraph("Thank you for your order. This invoice covers the consulting work below.") { Align = TextAlign.Justify },
            new Table(
            [
                new Column("#") { Width = ColumnWidth.Star(0.6), Align = TextAlign.Right },
                new Column("Description") { Width = ColumnWidth.Star(5) },
                new Column("Hours") { Width = ColumnWidth.Star(1.1), Align = TextAlign.Right },
                new Column("Amount") { Width = ColumnWidth.Fixed(Length.Cm(2.5)), Align = TextAlign.Right },
            ])
            {
                Rows = TableRows.Bound("lines"),
            },
            new Paragraph("Payment is due within 30 days.") { Align = TextAlign.Right },
        ],
    };

    [Fact]
    public async Task ALongTableFlowsOverPagesWithTheHeaderRepeatedAsAnArtifact()
    {
        (byte[] file, RenderResult result) = await RenderAsync(Invoice, Data(120));

        Assert.True(result.PageCount >= 3, $"{result.PageCount} pages");
        PdfFile.CheckStructure(file);
        string[] pages = PdfInspect.PageContents(file);
        Assert.Equal(4, Regex.Count(pages[0], "/TH <</MCID")); // The tagged header row, once.
        foreach (string page in pages.Skip(1))
        {
            Assert.DoesNotContain("/TH <</MCID", page, StringComparison.Ordinal);
            Assert.Contains("/Artifact <</Type /Pagination>> BDC", page, StringComparison.Ordinal);
        }

        Assert.Single(PdfInspect.Elements(file, "Table"));
        Assert.Equal(121, PdfInspect.Elements(file, "TR").Count); // Header plus 120 rows.
        Assert.Equal(4, PdfInspect.Elements(file, "TH").Count);
        Assert.Equal(480, PdfInspect.Elements(file, "TD").Count);
        Assert.Equal(4, Regex.Count(PdfInspect.Text(file), "/A <</O /Table /Scope /Column>>"));
    }

    [Fact]
    public async Task MarkedContentIdsMatchTheParentTree()
    {
        (byte[] file, RenderResult result) = await RenderAsync(Invoice, Data(80));
        string[] pages = PdfInspect.PageContents(file);
        List<int[]> tree = PdfInspect.ParentTree(file);

        Assert.Equal(result.PageCount, tree.Count);
        for (int page = 0; page < pages.Length; page++)
        {
            int[] mcids = [.. Regex.Matches(pages[page], @"<</MCID (\d+)>> BDC").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))];
            Assert.Equal(Enumerable.Range(0, mcids.Length), mcids);
            Assert.Equal(mcids.Length, tree[page].Length);
        }
    }

    [Fact]
    public async Task TheHeaderStaysWithTheFirstRow()
    {
        for (int filler = 40; filler < 60; filler++)
        {
            PdfDocument document = Invoice with { Content = [.. Enumerable.Range(0, filler).Select(i => (Block)new Paragraph($"Line {i}") { SpaceAfter = Length.Pt(0) }), .. Invoice.Content.Skip(2)] };
            (byte[] file, _) = await RenderAsync(document, Data(3));
            List<int[]> tree = PdfInspect.ParentTree(file);
            int firstHeader = PdfInspect.Elements(file, "TH")[0];
            int firstCell = PdfInspect.Elements(file, "TD")[0];
            Assert.Equal(tree.FindIndex(p => p.Contains(firstHeader)), tree.FindIndex(p => p.Contains(firstCell)));
        }
    }

    [Fact]
    public async Task RowsFromAsyncAndSyncSourcesGiveTheSameFile()
    {
        (byte[] fromAsync, _) = await RenderAsync(Invoice, Data(50));
        RenderOptions options = SyncData(50);
        (byte[] fromSync, _) = await RenderAsync(Invoice, options);

        Assert.Equal(fromAsync, fromSync);
        Assert.Equal(fromAsync, RenderSync(Invoice, options));
    }

    private static RenderOptions SyncData(int rows)
    {
        RenderOptions options = PdfInspect.Options();
        return new RenderOptions { Fonts = options.Fonts, Data = PdfData.Empty.WithRows("lines", Enumerable.Range(1, rows), Cells) };
    }

    private static byte[] RenderSync(PdfDocument document, RenderOptions options)
    {
        using var output = new MemoryStream();
        document.Render(output, options);
        return output.ToArray();
    }

    [Fact]
    public async Task InlineRowsAndAnEmptyTableRender()
    {
        var document = new PdfDocument("Inline")
        {
            Content =
            [
                new Table([new Column("A"), new Column("B")]) { Rows = TableRows.Inline([["1", "2"], [new Cell("3") { Align = TextAlign.Right }, ""]]) },
                new Table([new Column("Only a header")]),
            ],
        };

        (byte[] file, RenderResult result) = await RenderAsync(document, PdfInspect.Options());

        Assert.False(result.HasWarnings);
        Assert.Equal(2, PdfInspect.Elements(file, "Table").Count);
        Assert.Equal(4, PdfInspect.Elements(file, "TD").Count); // The empty cell is still a TD.
        Assert.Equal(3, PdfInspect.Elements(file, "TH").Count);
    }

    [Fact]
    public async Task TableStyleControlsFillRulesAndPadding()
    {
        PdfDocument document = Invoice with
        {
            Content = [new Table([new Column("A")]) { Rows = TableRows.Inline([["x"]]), TableStyle = new TableStyle { HeaderFill = Color.Parse("#336699"), RuleWidth = Length.Pt(0) } }],
        };

        string page = PdfInspect.PageContents((await RenderAsync(document, PdfInspect.Options())).File)[0];
        Assert.Contains("0.2 0.4 0.6 rg", page, StringComparison.Ordinal);
        Assert.DoesNotContain(" l\nS\n", page, StringComparison.Ordinal); // No rules.
    }

    [Fact]
    public async Task StripesFillEverySecondBodyRow()
    {
        PdfDocument document = Invoice with
        {
            Content = [new Table([new Column("A")]) { Rows = TableRows.Inline([["1"], ["2"], ["3"], ["4"], ["5"]]), TableStyle = new TableStyle { StripeFill = Color.Parse("#336699") } }],
        };

        string page = PdfInspect.PageContents((await RenderAsync(document, PdfInspect.Options())).File)[0];

        // Rows are drawn one group at a time: the second and fourth rows each get a stripe artifact, the others none.
        Assert.Equal(2, Regex.Count(page, @"/Artifact <</Type /Layout>> BDC\nq\n0.2 0.4 0.6 rg\n[\d. ]+re\nf\nQ\nEMC"));
    }

    [Fact]
    public async Task RepeatHeaderCanBeTurnedOff()
    {
        PdfDocument document = Invoice with { Content = [Invoice.Content[2] is Table table ? table with { RepeatHeader = false } : throw new InvalidOperationException()] };
        string[] pages = PdfInspect.PageContents((await RenderAsync(document, Data(200))).File);
        Assert.All(pages.Skip(1), page => Assert.DoesNotContain("/Artifact <</Type /Pagination>> BDC", page, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMissingSlotIsAnErrorBeforeAnyOutput()
    {
        var output = new MemoryStream();
        PdfDocumentException error = await Assert.ThrowsAsync<PdfDocumentException>(
            () => Invoice.RenderAsync(output, PdfInspect.Options(), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("ZPDF-D001 /content/2/rows", Assert.Single(error.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Code} {d.Path}")));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void AsyncRowsInASyncRenderAreAnError()
    {
        PdfDocumentException error = Assert.Throws<PdfDocumentException>(() => Invoice.Render(new MemoryStream(), Data(5)));
        Assert.Equal("ZPDF-R002 /content/2/rows", Assert.Single(error.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Code} {d.Path}")));
    }

    [Fact]
    public async Task RowsWithTheWrongCellCountArePaddedOrCutWithAWarning()
    {
        RenderOptions options = PdfInspect.Options();
        IReadOnlyList<Cell>[] rows = [["1", "short"], ["2", "long", "1", "2", "3", "extra"]];
        options = new RenderOptions { Fonts = options.Fonts, Data = PdfData.Empty.WithRows("lines", rows) };

        (_, RenderResult result) = await RenderAsync(Invoice, options);

        Assert.Equal(["ZPDF-L002 /content/2/rows/0", "ZPDF-L002 /content/2/rows/1"], result.Diagnostics.Where(d => d.Code == "ZPDF-L002").Select(d => $"{d.Code} {d.Path}"));
    }

    [Fact]
    public async Task ARowTallerThanAPageSplitsAcrossPagesAsOneTaggedRow()
    {
        string tall = string.Join("\n", Enumerable.Range(0, 120).Select(i => $"line {i}"));
        PdfDocument document = Invoice with { Content = [new Table([new Column("A"), new Column("B")]) { Rows = TableRows.Inline([["short", tall], ["after", "after"]]) }] };

        (byte[] file, RenderResult result) = await RenderAsync(document, PdfInspect.Options());

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ZPDF-L001");
        Assert.True(result.PageCount >= 2, $"{result.PageCount} pages");
        PdfFile.CheckStructure(file);
        Assert.Equal(3, PdfInspect.Elements(file, "TR").Count); // Header, the split row once, the row after.
        Assert.Equal(4, PdfInspect.Elements(file, "TD").Count);
        string[] pages = PdfInspect.PageContents(file);
        Assert.Contains("/Artifact <</Type /Pagination>> BDC", pages[1], StringComparison.Ordinal); // The header repeats.
        Assert.Contains("/TD <</MCID", pages[1], StringComparison.Ordinal); // The rest of the tall cell.
        Assert.Matches(@"/Type /StructElem /S /TD .*?/K \[<</Type /MCR", PdfInspect.Text(file)); // One TD, marked on two pages.
    }

    [Fact]
    public async Task FixedColumnsWiderThanThePageAreScaledWithAWarning()
    {
        PdfDocument document = Invoice with { Content = [new Table([new Column("A") { Width = ColumnWidth.Fixed(Length.Cm(15)) }, new Column("B") { Width = ColumnWidth.Fixed(Length.Cm(15)) }]) { Rows = TableRows.Inline([["a", "b"]]) }] };
        (_, RenderResult result) = await RenderAsync(document, PdfInspect.Options());
        Assert.Contains(result.Diagnostics, d => d is { Code: "ZPDF-L003", Path: "/content/0/columns" });
    }

    [Fact]
    public async Task StopsAtTheRowLimit()
    {
        RenderOptions options = Data(50);
        options = new RenderOptions { Fonts = options.Fonts, Data = options.Data, Limits = new PdfLimits { MaxTableRows = 10 } };
        PdfLimitException error = await Assert.ThrowsAsync<PdfLimitException>(() => RenderAsync(Invoice, options));
        Assert.Contains("MaxTableRows", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamsPagesBeforeTheTableEnds()
    {
        var output = new CountingStream();
        long firstWriteAt = -1;
        output.OnWrite = () => firstWriteAt = firstWriteAt < 0 ? output.Length : firstWriteAt;

        RenderResult result = await Invoice.RenderAsync(output, Data(2000), TestContext.Current.CancellationToken);

        Assert.True(result.PageCount > 40);
        Assert.True(output.Writes >= output.Length / Zorg.Pdf.Writing.PdfObjectWriter.FlushThreshold / 2, $"{output.Writes} writes for {output.Length} bytes.");
        Assert.InRange(firstWriteAt, Zorg.Pdf.Writing.PdfObjectWriter.FlushThreshold, output.Length / 2);
    }

    [Fact]
    public async Task CancellationStopsARenderPartWay()
    {
        using var cancel = new CancellationTokenSource();
        var output = new CountingStream { OnWrite = cancel.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoice.RenderAsync(output, Data(5000), cancel.Token).AsTask());
        Assert.True(output.Length < 2_000_000, "It should stop soon after the token is cancelled.");
    }

    [Fact]
    public async Task IsDeterministic()
    {
        (byte[] first, _) = await RenderAsync(Invoice, Data(90));
        (byte[] second, _) = await RenderAsync(Invoice, Data(90));
        Assert.Equal(first, second);
    }

    /// <summary>Options with the test font and <paramref name="rows"/> invoice lines arriving asynchronously.</summary>
    internal static RenderOptions Data(int rows)
    {
        RenderOptions options = PdfInspect.Options();
        return new RenderOptions { Fonts = options.Fonts, Data = PdfData.Empty.WithRows("lines", LinesAsync(rows), Cells) };
    }

    internal static IReadOnlyList<Cell> Cells(int i)
    {
        double hours = 0.5 + (i * 37 % 16 / 2.0);
        double rate = i % 3 == 0 ? 1450 : 1250;
        string description = (i % 5) switch
        {
            0 => "Architecture review of the reporting pipeline, with written recommendations and a follow-up workshop",
            1 => "Development: PDF/UA tagging",
            2 => "Blåbærsyltetøy-integrasjon (Norwegian characters æ ø å)",
            3 => "Code review",
            _ => "Testing on Linux and Windows",
        };
        return [i.ToString(CultureInfo.InvariantCulture), description, hours.ToString("N1", CultureInfo.InvariantCulture), (hours * rate).ToString("N2", CultureInfo.InvariantCulture)];
    }

    private static async IAsyncEnumerable<int> LinesAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 1; i <= count; i++)
        {
            if (i % 25 == 0)
            {
                await Task.Yield();
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
        }
    }

    private static Task<(byte[] File, RenderResult Result)> RenderAsync(PdfDocument document, RenderOptions options) => RenderTests.RenderAsync(document, options);

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
