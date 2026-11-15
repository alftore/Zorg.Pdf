using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Rendering;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Bar charts: data checked, drawn in one tagged Figure, bound from the model. The gate checks them with veraPDF and MuPDF.</summary>
public sealed class ChartTests
{
    private static readonly Chart Sales = new(ChartKind.Bar, ["Q1", "Q2", "Q3"], [new ChartSeries("2025", [12, 15, 14]), new ChartSeries("2026", [14, 17, 19])])
    {
        Title = "Sales",
        Alt = "Sales rose every quarter.",
    };

    [Theory]
    [InlineData(3.7, 5)]
    [InlineData(1.2, 2)]
    [InlineData(2.3, 2.5)]
    [InlineData(0.07, 0.1)]
    [InlineData(1800, 2000)]
    [InlineData(1, 1)]
    public void ValueAxisStepsAreRound(double rough, double step) => Assert.Equal(step, RenderEngine.NiceStep(rough), 9);

    [Fact]
    public async Task AChartIsOneFigureWithItsTextInside()
    {
        var document = new PdfDocument("Charts")
        {
            Content =
            [
                Sales,
                Sales with { Kind = ChartKind.StackedBar, Horizontal = true, Title = null },
                new Barcode(BarcodeKind.Ean13, "590123412345") { Alt = "Product number" },
                Sales with { Kind = ChartKind.Line },
                Sales with { Kind = ChartKind.Pie, Series = [new ChartSeries("2026", [14, 17, 19])] },
            ],
        };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, PdfInspect.Options(PdfConformance.PdfUA1));

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity != DiagnosticSeverity.Info); // The test font may lack bold for the title.
        PdfFile.CheckStructure(file);
        Assert.Equal(5, PdfInspect.Elements(file, "Figure").Count);
        string page = PdfInspect.PageContents(file)[0];
        Assert.Matches(@"\sc\s", page); // Pie arcs and round markers are Bézier curves.
        // Text inside a figure mustn't close the figure's marked content: every BDC has its EMC, in order.
        Assert.Equal(Regex.Count(page, @"\b(BDC|BMC)\b"), Regex.Count(page, @"\bEMC\b"));
        int depth = 0;
        foreach (Match m in Regex.Matches(page, @"\b(BDC|BMC|EMC)\b"))
        {
            depth += m.Value == "EMC" ? -1 : 1;
            Assert.True(depth >= 0, "EMC without its BDC");
        }
    }

    [Fact]
    public void ChartsAreChecked()
    {
        var document = new PdfDocument("x")
        {
            Content =
            [
                Sales with { Alt = null },
                Sales with { Series = [new ChartSeries("a", [1, 2])] },
                Sales with { Kind = ChartKind.StackedBar, Series = [new ChartSeries("a", [1, -2, 3])] },
                Sales with { Bind = "sales" },
                Sales with { Minimum = 10, Maximum = 5 },
                Sales with { Kind = ChartKind.Pie },
                Sales with { Kind = ChartKind.Line, Horizontal = true },
                Sales with { Kind = ChartKind.Pie, Series = [new ChartSeries("a", [0, 0, 0])] },
            ],
        };

        Assert.Equal(
            ["ZPDF-A003 /content/0/alt", "ZPDF-J033 /content/1/series/0/values", "ZPDF-J033 /content/2/series/0/values", "ZPDF-J033 /content/3", "ZPDF-J033 /content/4/maximum", "ZPDF-J033 /content/5/series", "ZPDF-J033 /content/6/horizontal", "ZPDF-J033 /content/7/series/0/values"],
            document.Validate().Diagnostics.Select(d => $"{d.Code} {d.Path}"));
    }

    [Fact]
    public async Task ChartsRoundTripThroughJsonAndBindFromTheModel()
    {
        PdfDocument parsed = PdfDocument.ParseJson("""{"title":"x","content":[{"type":"chart","kind":"stackedBar","horizontal":true,"categories":["A"],"series":[{"name":"s","values":[1.5],"color":"#FF0000"}],"valueFormat":"N1","alt":"a"}]}""");
        Chart chart = Assert.IsType<Chart>(parsed.Content[0]);
        Assert.Equal((ChartKind.StackedBar, true, 1.5, new Color(255, 0, 0)), (chart.Kind, chart.Horizontal, chart.Series[0].Values[0], chart.Series[0].Color!.Value));
        Assert.Equal(parsed.ToJson(), PdfDocument.ParseJson(parsed.ToJson()).ToJson());

        var template = new PdfDocument("Bound") { Content = [new Chart(ChartKind.Bar, [], []) { Bind = "sales", Alt = "Sales" }] };
        RenderOptions inspect = PdfInspect.Options();
        JsonElement model = JsonDocument.Parse("""{"sales":{"categories":["Q1","Q2"],"series":[{"name":"2026","values":[3,4]}]}}""").RootElement;
        JsonElement broken = JsonDocument.Parse("""{"sales":{"categories":["Q1","Q2"],"series":[{"name":"2026","values":[3]}]}}""").RootElement;

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(template, new RenderOptions { Fonts = inspect.Fonts, Data = PdfData.Empty.WithModel(model) });
        PdfDocumentException failed = await Assert.ThrowsAsync<PdfDocumentException>(() => RenderTests.RenderAsync(template, new RenderOptions { Fonts = inspect.Fonts, Data = PdfData.Empty.WithModel(broken) }));

        Assert.Empty(result.Diagnostics);
        Assert.Single(PdfInspect.Elements(file, "Figure"));
        Assert.Equal(("ZPDF-J033", "/content/0/bind/series/0/values"), (failed.Diagnostics[0].Code, failed.Diagnostics[0].Path));
    }
}
