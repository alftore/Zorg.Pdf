using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Rendering;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Lists: JSON, validation, markers, and the L/LI/Lbl/LBody structure across pages.</summary>
public sealed class ListRenderTests
{
    [Fact]
    public void ListsRoundTripThroughJsonWithShortItems()
    {
        var list = new List(["One", new ListItem([new Paragraph("Two"), new List(["Two a", "Two b"]) { Marker = ListMarker.LowerAlpha }])])
        {
            Marker = ListMarker.Number,
            Start = 3,
        };
        string json = new PdfDocument("x") { Content = [list] }.ToJson();

        Assert.Contains("""{"type":"list","items":["One",{"content":[{"type":"paragraph","inlines":["Two"]},{"type":"list","items":["Two a","Two b"],"marker":"lowerAlpha","start":1}]}],"marker":"number","start":3}""", json, StringComparison.Ordinal);
        Assert.Equal(json, PdfDocument.ParseJson(json).ToJson());
    }

    [Fact]
    public void BadListsAreReportedWithPaths()
    {
        List deep = new(["leaf"]);
        for (int i = 0; i < 6; i++)
        {
            deep = new List([new ListItem([deep])]);
        }

        var document = new PdfDocument("x")
        {
            Content =
            [
                new List([]),
                new List([new ListItem([new Table([new Column("A")])])]) { Start = -1 },
                deep,
            ],
        };

        Assert.Equal(
            ["ZPDF-J020 /content/0/items", "ZPDF-J020 /content/1/start", "ZPDF-J021 /content/1/items/0/content/0", "ZPDF-J021 /content/2/items/0/content/0/items/0/content/0/items/0/content/0/items/0/content/0/items/0/content/0/items/0/content/0"],
            document.Validate().Diagnostics.Select(d => $"{d.Code} {d.Path}"));
    }

    [Fact]
    public async Task AListIsTaggedAsLWithItemsLabelsAndBodies()
    {
        var document = new PdfDocument("x")
        {
            Content =
            [
                new Heading("Steps"),
                new List(["Open the invoice", "Check the amounts", new ListItem([new List(["by card", "by transfer"]), new Paragraph("Then pay.")])]) { Marker = ListMarker.Number },
                new Paragraph("After the list."),
            ],
        };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, PdfInspect.Options(PdfConformance.PdfUA1));

        Assert.False(result.HasWarnings);
        PdfFile.CheckStructure(file);
        Assert.Equal(2, PdfInspect.Elements(file, "L").Count);
        Assert.Equal(5, PdfInspect.Elements(file, "LI").Count);
        Assert.Equal(5, PdfInspect.Elements(file, "Lbl").Count);
        Assert.Equal(5, PdfInspect.Elements(file, "LBody").Count);
        string pdf = PdfInspect.Text(file);
        Assert.Contains("/A <</O /List /ListNumbering /Decimal>>", pdf, StringComparison.Ordinal);
        Assert.Contains("/A <</O /List /ListNumbering /Disc>>", pdf, StringComparison.Ordinal);

        // Every label is drawn, the third item's on the first line of its nested list, beside that list's own marker.
        string page = PdfInspect.PageContents(file)[0];
        Assert.Equal(5, Regex.Count(page, @"/Lbl <</MCID \d+>> BDC"));
        Assert.Equal(6, Regex.Count(page, @"/P <</MCID \d+>> BDC"));
    }

    [Fact]
    public async Task ALongListFlowsOverPagesWithEachMarkerBesideItsItem()
    {
        var document = new PdfDocument("x") { Content = [new List([.. Enumerable.Range(1, 150).Select(i => (ListItem)$"Item {i} of a long list")]) { Marker = ListMarker.UpperRoman }] };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document);

        Assert.True(result.PageCount >= 3, $"{result.PageCount} pages");
        PdfFile.CheckStructure(file);
        foreach (string page in PdfInspect.PageContents(file))
        {
            // A page ends after an item's text, never between a marker and its first line.
            Assert.Equal(Regex.Count(page, @"/Lbl <</MCID"), Regex.Count(page, @"/P <</MCID"));
        }
    }

    [Theory]
    [InlineData(1, "A", "I")]
    [InlineData(4, "D", "IV")]
    [InlineData(26, "Z", "XXVI")]
    [InlineData(27, "AA", "XXVII")]
    [InlineData(53, "BA", "LIII")]
    [InlineData(1994, "BXR", "MCMXCIV")]
    [InlineData(0, "0", "0")]
    public void MarkersCountInLettersAndRomanNumerals(int number, string alpha, string roman)
    {
        Assert.Equal(alpha, RenderEngine.Alpha(number));
        Assert.Equal(roman, RenderEngine.Roman(number));
    }
}
