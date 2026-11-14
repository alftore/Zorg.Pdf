using System;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace Zorg.Pdf.Tests.Model;

public sealed class DocumentJsonTests
{
    internal static readonly PdfDocument Invoice = new("Invoice 2026-0042")
    {
        Footer = new PageBand([new Paragraph(["Page ", Field.PageNumber, " of ", Field.PageCount]) { Align = TextAlign.Right, Style = "footer" }]),
        Content =
        [
            new Heading("Invoice 2026-0042"),
            new Paragraph(["Zorg Consulting AS", LineBreak.Value, "Storgata 1"]),
            new Paragraph(["Due: ", new TextRun("30 days") { Style = TextStyle.Bold }]) { Align = TextAlign.Justify, SpaceAfter = Length.Pt(12) },
            new Table([new Column("Description") { Width = ColumnWidth.Star(5) }, new Column("Amount") { Width = ColumnWidth.Fixed(Length.Cm(3)), Align = TextAlign.Right }])
            {
                Rows = TableRows.Bound("lines"),
            },
            new Table([new Column("A"), new Column("B")])
            {
                Rows = TableRows.Inline([["1", "2"], [new Cell("3") { Align = TextAlign.Center }, "4"]]),
                RepeatHeader = false,
            },
            new Image(ImageSource.FromSlot("logo")) { Alt = "Logo", Width = Length.Cm(4) },
            new Spacer(Length.Mm(5)),
            new PageBreak(),
        ],
    };

    [Fact]
    public void RoundTripsThroughJson()
    {
        string json = Invoice.ToJson();
        PdfDocument back = PdfDocument.ParseJson(json);

        Assert.Equal(json, back.ToJson());
        Assert.Equal(8, back.Content.Length);
        Assert.Equal("lines", Assert.IsType<BoundTableRows>(Assert.IsType<Table>(back.Content[3]).Rows).Slot);
        Assert.False(Assert.IsType<Table>(back.Content[4]).RepeatHeader);
        Assert.Equal(TextAlign.Center, Assert.IsType<InlineTableRows>(Assert.IsType<Table>(back.Content[4]).Rows).Rows[1][0].Align);
    }

    [Fact]
    public void WritesCompactAgentFriendlyJson()
    {
        string json = Invoice.ToJson();

        Assert.StartsWith($"{{\"$schema\":\"{PdfJsonSchema.Id}\",\"title\":\"Invoice 2026-0042\"", json, StringComparison.Ordinal);
        Assert.Contains("{\"type\":\"heading\",\"level\":1,\"inlines\":[\"Invoice 2026-0042\"]}", json, StringComparison.Ordinal);
        Assert.Contains("[\"Page \",{\"field\":\"pageNumber\"},\" of \",{\"field\":\"pageCount\"}]", json, StringComparison.Ordinal);
        Assert.Contains("{\"text\":\"30 days\",\"style\":{\"weight\":\"bold\"}}", json, StringComparison.Ordinal);
        Assert.Contains("\"rows\":{\"$data\":\"lines\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"width\":\"5*\"", json, StringComparison.Ordinal);
        Assert.Contains("{\"lineBreak\":true}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("keepWithNext", json, StringComparison.Ordinal); // Defaults are left out.
        Assert.DoesNotContain("\"align\":\"left\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsUnitsNamesAndShorthands()
    {
        PdfDocument document = PdfDocument.ParseJson("""
            {
              // Comments and trailing commas are fine: agents and people write them.
              "title": "Units",
              "page": { "size": { "width": "100mm", "height": "6in" }, "orientation": "landscape", "margin": { "top": "1cm", "left": 36 } },
              "styles": { "styles": { "note": { "size": "8pt", "color": "#abc", "lineHeight": 1.4 } } },
              "content": [
                { "inlines": ["The type can come last."], "type": "paragraph", "style": "note" },
                { "type": "table", "columns": [{ "header": "A", "width": "*" }, { "header": "B", "width": "2.5cm" }], },
              ],
            }
            """);

        Assert.Equal(Length.Mm(100).Points, document.Page.Size.Width.Points, 6);
        Assert.Equal(432, document.Page.Size.Height.Points, 6);
        Assert.Equal(PageOrientation.Landscape, document.Page.Orientation);
        Assert.Equal(Length.Cm(1).Points, document.Page.Margin.Top.Points, 6);
        Assert.Equal(36, document.Page.Margin.Left.Points);
        Assert.Equal(new Color(0xAA, 0xBB, 0xCC), document.Styles!.Styles["note"].Color);
        Assert.IsType<Paragraph>(document.Content[0]);
        Table table = Assert.IsType<Table>(document.Content[1]);
        Assert.True(table.Columns[0].Width.IsStar);
        Assert.Equal(Length.Cm(2.5).Points, table.Columns[1].Width.Value, 6);
        Assert.Same(TableRows.Empty, table.Rows);
    }

    [Theory]
    [InlineData("12pt", 12)]
    [InlineData(" 2 cm ", 56.692913)]
    [InlineData("5MM", 14.173228)]
    [InlineData("0.5in", 36)]
    [InlineData("-3", -3)]
    [InlineData("1e1pt", 10)]
    public void ParsesLengths(string text, double points) => Assert.Equal(points, Length.Parse(text).Points, 5);

    [Theory]
    [InlineData("")]
    [InlineData("cm")]
    [InlineData("2 parsecs")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void RefusesNonLengths(string text) => Assert.False(Length.TryParse(text, out _));

    [Theory]
    [InlineData("#1F6FEB", 0x1F, 0x6F, 0xEB)]
    [InlineData("#fff", 255, 255, 255)]
    public void ParsesColours(string text, byte r, byte g, byte b)
    {
        Assert.Equal(new Color(r, g, b), Color.Parse(text));
        Assert.False(Color.TryParse("1F6FEB", out _));
        Assert.False(Color.TryParse("#12345", out _));
    }

    [Fact]
    public void ParsesTheSameFromBytesAndText()
    {
        string json = Invoice.ToJson();
        Assert.Equal(json, PdfDocument.ParseJson(System.Text.Encoding.UTF8.GetBytes(json)).ToJson());
    }

    [Fact]
    public void IndentedJsonIsTheSameDocument()
    {
        string indented = Invoice.ToJson(indented: true);
        Assert.Contains("\n  \"title\"", indented, StringComparison.Ordinal);
        Assert.Equal(Invoice.ToJson(), PdfDocument.ParseJson(indented).ToJson());
    }

    [Fact]
    public void BytesImagesTravelAsBase64()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
        var document = new PdfDocument("Image") { Content = [new Image(ImageSource.FromBytes(jpeg)) { Decorative = true }] };

        string json = document.ToJson();
        Assert.Contains("\"source\":{\"base64\":\"/9j/4AECAw==\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"decorative\":true", json, StringComparison.Ordinal);
        BytesImageSource source = Assert.IsType<BytesImageSource>(Assert.IsType<Image>(PdfDocument.ParseJson(json).Content[0]).Source);
        Assert.Equal(jpeg, source.Bytes.ToArray());
    }

    [Fact]
    public void DiagnosticsPrintAsOneLine()
    {
        var diagnostic = new PdfDiagnostic("ZPDF-J002", DiagnosticSeverity.Error, "Unknown property 'colour'.", "/content/0/colour");
        Assert.Equal("error ZPDF-J002 at /content/0/colour: Unknown property 'colour'.", diagnostic.ToString());
        Assert.Equal("warning ZPDF-T001 at /: x", new PdfDiagnostic("ZPDF-T001", DiagnosticSeverity.Warning, "x", "").ToString());
    }

    [Fact]
    public void TheModelIsAValue()
    {
        PdfDocument changed = Invoice with { Title = "Credit note" };
        Assert.Equal("Invoice 2026-0042", Invoice.Title);
        Assert.Equal(Invoice.Content.Length, changed.Content.Length);
        Assert.Equal(new TextRun("a"), new TextRun("a"));
        Assert.Equal(TextStyle.Bold.Merge(TextStyle.Italic), new TextStyle { Weight = FontWeight.Bold, FontStyle = FontStyle.Italic });
        Assert.Same(TextStyle.Bold, TextStyle.Bold.Merge(null));
        Assert.Equal(Enumerable.Range(1, 6).Select(n => $"heading{n}"), StyleSheet.Default.Styles.Keys.Where(k => k.StartsWith("heading", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.True(ImmutableArray<Block>.Empty.IsEmpty);
    }
}
