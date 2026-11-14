using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Cells that span columns and rows: JSON, validation, tagging, and keeping spanned rows on one page.</summary>
public sealed class TableSpanTests
{
    private static readonly Column[] Columns = [new Column("Item") { Width = ColumnWidth.Star(3) }, new Column("Hours"), new Column("Amount") { Align = TextAlign.Right }];

    [Fact]
    public void SpansRoundTripThroughJson()
    {
        var document = new PdfDocument("x")
        {
            Content = [new Table([.. Columns]) { Rows = TableRows.Inline([[new Cell("Total") { ColumnSpan = 2 }, "54,00"]]) }],
        };
        string json = document.ToJson();

        Assert.Contains("""[{"inlines":["Total"],"columnSpan":2},"54,00"]""", json, StringComparison.Ordinal);
        Assert.Equal(json, PdfDocument.ParseJson(json).ToJson());
        Assert.Equal("ZPDF-J003 /content/0/rows/0/0/rowSpan", Assert.Single(PdfDocument.TryParseJson("""{"title":"x","content":[{"type":"table","columns":[{"header":"A"}],"rows":[[{"inlines":["a"],"rowSpan":"two"}]]}]}""").Diagnostics.Select(d => $"{d.Code} {d.Path}")));
    }

    [Fact]
    public void ValidationCountsSpansAndRowsCoveredFromAbove()
    {
        var document = new PdfDocument("x")
        {
            Content =
            [
                new Table([.. Columns])
                {
                    Rows = TableRows.Inline(
                    [
                        [new Cell("Travel") { RowSpan = 2 }, "2", "300"],
                        ["3", "450"], // The first column is taken by the span: two cells fill the row.
                        [new Cell("Total") { ColumnSpan = 2 }, "750"],
                        ["a", "b"], // One short.
                        [new Cell("x") { ColumnSpan = 0 }, "b", "c"],
                    ]),
                },
            ],
        };

        Assert.Equal(["ZPDF-J012 /content/0/rows/3", "ZPDF-J012 /content/0/rows/4/0"], document.Validate().Diagnostics.Select(d => $"{d.Code} {d.Path}"));
    }

    [Fact]
    public async Task SpanningCellsAreTaggedWithTheirSpans()
    {
        var document = new PdfDocument("x")
        {
            Content =
            [
                new Table([.. Columns])
                {
                    Rows = TableRows.Inline([[new Cell("Travel") { RowSpan = 2 }, "2", "300"], ["3", "450"], [new Cell("Total") { ColumnSpan = 2, Align = TextAlign.Right }, "750"]]),
                },
            ],
        };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, PdfInspect.Options(PdfConformance.PdfUA1));

        Assert.False(result.HasWarnings);
        PdfFile.CheckStructure(file);
        string pdf = PdfInspect.Text(file);
        Assert.Contains("/A <</O /Table /RowSpan 2>>", pdf, StringComparison.Ordinal);
        Assert.Contains("/A <</O /Table /ColSpan 2>>", pdf, StringComparison.Ordinal);
        Assert.Equal(4, PdfInspect.Elements(file, "TR").Count);
        Assert.Equal(7, PdfInspect.Elements(file, "TD").Count); // 3 + 2 + 2.
    }

    [Fact]
    public async Task RowsJoinedByASpanStayOnOnePage()
    {
        // However many rows come first, the two rows a span joins land on the same page.
        for (int before = 40; before <= 70; before += 3)
        {
            var rows = new List<IReadOnlyList<Cell>>();
            rows.AddRange(Enumerable.Range(0, before).Select(i => (IReadOnlyList<Cell>)[$"Row {i}", "1", "100"]));
            rows.Add([new Cell("Joined") { RowSpan = 2 }, "first", "1"]);
            rows.Add(["second", "2"]);
            var document = new PdfDocument("x") { Content = [new Table([.. Columns]) { Rows = TableRows.Bound("lines") }] };
            var options = new RenderOptions { Fonts = PdfInspect.Options().Fonts, Data = PdfData.Empty.WithRows("lines", rows) };

            (byte[] file, _) = await RenderTests.RenderAsync(document, options);

            List<int> cells = PdfInspect.Elements(file, "TD");
            int[] last = [.. cells.TakeLast(5)]; // Joined, first, 1, second, 2.
            List<int[]> pages = PdfInspect.ParentTree(file);
            int PageOf(int element) => pages.FindIndex(owners => owners.Contains(element));
            Assert.All(last, td => Assert.Equal(PageOf(last[0]), PageOf(td)));
        }
    }

    [Fact]
    public async Task BoundRowsThatDontFillTheirColumnsArePaddedWithAWarning()
    {
        var document = new PdfDocument("x") { Content = [new Table([.. Columns]) { Rows = TableRows.Bound("lines") }] };
        var options = new RenderOptions
        {
            Fonts = PdfInspect.Options().Fonts,
            Data = PdfData.Empty.WithRows("lines", [(IReadOnlyList<Cell>)[new Cell("Wide") { ColumnSpan = 2 }], ["a", "b", "c", "d"]]),
        };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, options);

        Assert.Equal(["ZPDF-L002 /content/0/rows/0", "ZPDF-L002 /content/0/rows/1"], result.Diagnostics.Where(d => d.Code == "ZPDF-L002").Select(d => $"{d.Code} {d.Path}"));
        Assert.Equal(5, PdfInspect.Elements(file, "TD").Count); // Wide and a padding cell; then a, b and c (d is cut).
    }
}
