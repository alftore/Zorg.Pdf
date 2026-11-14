using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Rendering;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Templates: Bind, Repeat and Conditional, bound to a JSON model before the render.</summary>
public sealed class TemplateTests
{
    private const string Model = """
        {
          "customer": { "name": "Blåbær & Syltetøy AB", "vat": "SE556677889901" },
          "total": 54375.5,
          "due": "2026-12-14",
          "overdue": false,
          "notes": [{ "text": "Hours are rounded to the half hour." }, { "text": "Travel is billed at cost." }],
          "groups": [{ "name": "Oslo", "people": ["Kari", "Ola"] }, { "name": "Bergen", "people": [] }],
          "lines": [{ "item": "Consulting", "hours": 37.5, "amount": 46875 }, { "item": "Travel", "amount": 7500.5 }]
        }
        """;

    [Fact]
    public void TemplatesRoundTripThroughJson()
    {
        string json = """{"title":"x","content":[{"type":"paragraph","inlines":["Dear ",{"bind":"customer.name","style":{"weight":"bold"}},", total ",{"bind":"total","format":"N2"}]},{"type":"repeat","items":"notes","content":[{"type":"paragraph","inlines":[{"bind":"text"}]}]},{"type":"when","test":"overdue","not":true,"content":[{"type":"paragraph","inlines":["Thanks."]}]}]}""";
        PdfDocument template = PdfDocument.ParseJson(json);

        Assert.IsType<Bind>(((Paragraph)template.Content[0]).Inlines[1]);
        Assert.Equal(new Bind("total") { Format = "N2" }, ((Paragraph)template.Content[0]).Inlines[3]);
        Assert.True(((Conditional)template.Content[2]).Not);
        Assert.Equal(template.ToJson(), PdfDocument.ParseJson(template.ToJson()).ToJson());
        Assert.True(template.Validate().IsValid);
    }

    [Fact]
    public void BindingFillsTextFormattedInTheDocumentsLanguage()
    {
        PdfDocument bound = Bind(new PdfDocument("x")
        {
            Language = "nb-NO",
            Content = [new Paragraph(["Til ", new Bind("customer.name"), ": ", new Bind("total") { Format = "N2" }, " kr, forfall ", new Bind("due") { Format = "d. MMMM yyyy" }])],
        });

        Assert.Equal("Til Blåbær & Syltetøy AB: 54 375,50 kr, forfall 14. desember 2026", Text(bound.Content[0]));
    }

    [Fact]
    public void RepeatExpandsPerItemWithPositionsAndOuterValues()
    {
        PdfDocument bound = Bind(new PdfDocument("x")
        {
            Content =
            [
                new Repeat("notes", [new Paragraph([new Bind("@number"), ". ", new Bind("text"), " (", new Bind("customer.name"), ")"])]),
                new Repeat("groups", [new Heading([new Bind("name")]) { Level = 2 }, new Repeat("people", [new Paragraph([new Bind("this")])]), new Conditional("people", [new Paragraph("Nobody yet.")]) { Not = true }]),
            ],
        });

        Assert.Equal(
            ["1. Hours are rounded to the half hour. (Blåbær & Syltetøy AB)", "2. Travel is billed at cost. (Blåbær & Syltetøy AB)", "Oslo", "Kari", "Ola", "Bergen", "Nobody yet."],
            bound.Content.Select(Text));
    }

    [Fact]
    public void AListItemThatIsOnlyARepeatBecomesOneItemPerElement()
    {
        PdfDocument bound = Bind(new PdfDocument("x")
        {
            Content =
            [
                new List(["First", new ListItem([new Repeat("notes", [new Paragraph([new Bind("text")])])]), "Last"]),
                new List([new ListItem([new Repeat("groups.1.people", [new Paragraph([new Bind("this")])])])]), // Empty: no list.
            ],
        });

        List list = Assert.IsType<List>(Assert.Single(bound.Content));
        Assert.Equal(["First", "Hours are rounded to the half hour.", "Travel is billed at cost.", "Last"], list.Items.Select(i => Text(i.Content[0])));
    }

    [Fact]
    public void ConditionalShowsContentOnlyWhenTheValueIsSet()
    {
        PdfDocument bound = Bind(new PdfDocument("x")
        {
            Content =
            [
                new Conditional("overdue", [new Paragraph("Overdue!")]),
                new Conditional("overdue", [new Paragraph("On time.")]) { Not = true },
                new Conditional("customer.vat", [new Paragraph(["VAT: ", new Bind("customer.vat")])]),
                new Conditional("missing.value", [new Paragraph("Never.")]),
            ],
        });

        Assert.Equal(["On time.", "VAT: SE556677889901"], bound.Content.Select(Text));
    }

    [Fact]
    public void MissingValuesAreWarningsAtTheTemplatesPathOnce()
    {
        var sink = new DiagnosticSink();
        PdfDocument template = new("x")
        {
            Content =
            [
                new Repeat("notes", [new Paragraph([new Bind("nope")])]),
                new Repeat("customer", [new Paragraph("x")]),
                new Paragraph([new Bind("customer")]),
            ],
        };
        using JsonDocument model = JsonDocument.Parse(Model);

        TemplateBinder.Bind(template, model.RootElement, sink);

        Assert.Equal(
            ["ZPDF-B001 /content/0/content/0/inlines/0/bind", "ZPDF-B002 /content/1/items", "ZPDF-B001 /content/2/inlines/0/bind"],
            sink.ToImmutable().Select(d => $"{d.Code} {d.Path}"));

        var noModel = new DiagnosticSink();
        TemplateBinder.Bind(new PdfDocument("x") { Content = [new Paragraph([new Bind("a")])] }, null, noModel);
        Assert.Contains("WithModel", Assert.Single(noModel.ToImmutable()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IncompleteTemplatePartsAreErrors()
    {
        var document = new PdfDocument("x") { Content = [new Paragraph([new Bind(" ")]), new Repeat("", []), new Conditional("x", [])] };

        Assert.Equal(
            ["ZPDF-J023 /content/0/inlines/0/bind", "ZPDF-J023 /content/1/items", "ZPDF-J023 /content/1/content", "ZPDF-J023 /content/2/content"],
            document.Validate().Diagnostics.Select(d => $"{d.Code} {d.Path}"));
    }

    [Fact]
    public async Task ATemplateRendersWithItsDataFromJson()
    {
        var template = new PdfDocument("Invoice")
        {
            Footer = new PageBand([new Paragraph([new Bind("customer.name"), " · page ", Field.PageNumber])]),
            Content =
            [
                new Heading("Invoice"),
                new Paragraph(["Bill to: ", new Bind("customer.name")]),
                new Table([new Column("Item"), new Column("Amount")]) { Rows = TableRows.Bound("lines") },
                new List([new ListItem([new Repeat("notes", [new Paragraph([new Bind("text")])])])]),
            ],
        };
        PdfData data = PdfData.ParseJson("""{ "customer": { "name": "Blåbær AB" }, "lines": [["Consulting", "9 375,00"]], "notes": [{ "text": "Thanks!" }] }""");

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(template, new RenderOptions { Fonts = PdfInspect.Options().Fonts, Data = data, Conformance = PdfConformance.PdfUA1 });

        Assert.False(result.HasWarnings, string.Join(Environment.NewLine, result.Diagnostics));
        PdfFile.CheckStructure(file);
        Assert.Single(PdfInspect.Elements(file, "LI"));
    }

    [Fact]
    public void RowTemplatesRoundTripThroughJsonWithTheCellShorthand()
    {
        string json = """{"title":"x","content":[{"type":"table","columns":[{"header":"Item"},{"header":"Amount"}],"rows":{"repeat":"lines","cells":[{"bind":"item"},{"bind":"amount","format":"N2","align":"right"}]}}]}""";
        PdfDocument template = PdfDocument.ParseJson(json);

        RepeatedTableRows rows = Assert.IsType<RepeatedTableRows>(((Table)template.Content[0]).Rows);
        Assert.Equal("lines", rows.Items);
        Assert.Equal(new Bind("amount") { Format = "N2" }, Assert.Single(rows.Cells[1].Inlines));
        Assert.Equal(TextAlign.Right, rows.Cells[1].Align);
        Assert.Equal(template.ToJson(), PdfDocument.ParseJson(template.ToJson()).ToJson());
        Assert.True(template.Validate().IsValid);
    }

    [Theory]
    [InlineData("""{"$data":"lines","repeat":"lines","cells":["x","y"]}""", "not both")]
    [InlineData("""{"repeat":"lines"}""", "\"cells\"")]
    [InlineData("""{"cells":["x","y"]}""", "\"repeat\"")]
    [InlineData("""{"repeat":"lines","cells":[{"bind":"a","inlines":["b"]},"y"]}""", "not both")]
    [InlineData("""{"repeat":"lines","cells":[{"format":"N2"},"y"]}""", "goes with")]
    public void BrokenRowTemplatesAreJsonErrors(string rows, string message)
    {
        DocumentParseResult parsed = PdfDocument.TryParseJson($$"""{"title":"x","content":[{"type":"table","columns":[{"header":"A"},{"header":"B"}],"rows":{{rows}}}]}""");

        Assert.Null(parsed.Document);
        Assert.Contains(message, Assert.Single(parsed.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RowTemplatesMakeOneRowPerItem()
    {
        PdfDocument bound = Bind(new PdfDocument("x")
        {
            Language = "nb-NO",
            Content =
            [
                new Table([new Column("#"), new Column("Item"), new Column("Amount")])
                {
                    Rows = TableRows.Repeat("lines", [new Cell([new Bind("@number")]), new Cell([new Bind("item"), " for ", new Bind("customer.name")]), new Cell([new Bind("amount") { Format = "N2" }])]),
                },
            ],
        });

        ImmutableArray<ImmutableArray<Cell>> rows = Assert.IsType<InlineTableRows>(((Table)bound.Content[0]).Rows).Rows;
        Assert.Equal(
            [["1", "Consulting for Blåbær & Syltetøy AB", "46 875,00"], ["2", "Travel for Blåbær & Syltetøy AB", "7 500,50"]],
            rows.Select(r => r.Select(c => string.Concat(c.Inlines.OfType<TextRun>().Select(t => t.Text))).ToArray()));
    }

    [Fact]
    public void RowTemplateProblemsAreReportedOnceAtTheTemplatesPath()
    {
        var sink = new DiagnosticSink();
        var template = new PdfDocument("x")
        {
            Content =
            [
                new Table([new Column("Item"), new Column("Hours")]) { Rows = TableRows.Repeat("lines", [new Cell([new Bind("item")]), new Cell([new Bind("hours")])]) },
                new Table([new Column("Item")]) { Rows = TableRows.Repeat("customer", [new Cell([new Bind("item")])]) },
            ],
        };
        using JsonDocument model = JsonDocument.Parse(Model);

        PdfDocument bound = TemplateBinder.Bind(template, model.RootElement, sink);

        Assert.Equal(["ZPDF-B001 /content/0/rows/cells/1/inlines/0/bind", "ZPDF-B002 /content/1/rows/repeat"], sink.ToImmutable().Select(d => $"{d.Code} {d.Path}"));
        Assert.Empty(Assert.IsType<InlineTableRows>(((Table)bound.Content[1]).Rows).Rows);
    }

    [Fact]
    public void RowTemplatesAreChecked()
    {
        var document = new PdfDocument("x")
        {
            Content =
            [
                new Table([new Column("A"), new Column("B")]) { Rows = TableRows.Repeat(" ", [new Cell("a"), new Cell("b") { RowSpan = 2 }]) },
                new Table([new Column("A"), new Column("B")]) { Rows = TableRows.Repeat("lines", [new Cell("a")]) },
                new Table([new Column("A")]) { Rows = TableRows.Repeat("lines", []) },
            ],
        };

        Assert.Equal(
            ["ZPDF-J023 /content/0/rows/repeat", "ZPDF-J012 /content/0/rows/cells/1", "ZPDF-J012 /content/1/rows/cells", "ZPDF-J023 /content/2/rows/cells"],
            document.Validate().Diagnostics.Select(d => $"{d.Code} {d.Path}"));
    }

    [Fact]
    public async Task ARowTemplateRendersAsTaggedRows()
    {
        var template = new PdfDocument("Lines")
        {
            Content = [new Table([new Column("Item"), new Column("Amount")]) { Rows = TableRows.Repeat("lines", [new Cell([new Bind("item")]), new Cell([new Bind("amount") { Format = "N2" }])]) }],
        };
        using JsonDocument model = JsonDocument.Parse(Model);

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(template, new RenderOptions { Fonts = PdfInspect.Options().Fonts, Data = PdfData.Empty.WithModel(model.RootElement), Conformance = PdfConformance.PdfUA1 });

        Assert.False(result.HasWarnings, string.Join(Environment.NewLine, result.Diagnostics));
        PdfFile.CheckStructure(file);
        Assert.Equal(3, PdfInspect.Elements(file, "TR").Count); // The header and two rows.
    }

    private static PdfDocument Bind(PdfDocument template)
    {
        using JsonDocument model = JsonDocument.Parse(Model);
        var sink = new DiagnosticSink();
        PdfDocument bound = TemplateBinder.Bind(template, model.RootElement, sink);
        Assert.Empty(sink.ToImmutable());
        return bound;
    }

    private static string Text(Block block) => string.Concat((block switch
    {
        Paragraph p => p.Inlines,
        Heading h => h.Inlines,
        _ => [],
    }).OfType<TextRun>().Select(r => r.Text));
}
