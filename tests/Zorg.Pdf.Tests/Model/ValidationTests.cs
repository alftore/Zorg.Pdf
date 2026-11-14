using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace Zorg.Pdf.Tests.Model;

public sealed class ValidationTests
{
    private static readonly PdfDocument Valid = DocumentJsonTests.Invoice;

    [Fact]
    public void AValidDocumentHasNoDiagnostics() => Assert.Empty(Valid.Validate().Diagnostics);

    [Fact]
    public void FindsEveryProblemInOnePass()
    {
        PdfDocument broken = Valid with
        {
            Title = " ",
            Language = "english please",
            Content =
            [
                new Heading("x") { Level = 7 },
                new Table([]),
                new Table([new Column("A"), new Column("B") { Width = ColumnWidth.Star(0) }]) { Rows = TableRows.Inline([["1", "2"], ["3"]]) },
                new Spacer(Length.Pt(-5)),
                new Image(ImageSource.FromSlot("logo")),
                new Paragraph("x") { Style = "nope" },
                new Paragraph([new TextRun("y") { Style = new TextStyle { LineHeight = 20, Size = Length.Pt(0) } }]),
                new Table([new Column("A")]) { Rows = TableRows.Bound(" ") },
            ],
            Footer = new PageBand([new Table([new Column("A")])]),
        };

        ValidationResult result = broken.Validate();

        Assert.False(result.IsValid);
        string[] found = [.. result.Diagnostics.Select(d => $"{d.Code} {d.Path}")];
        Assert.Equal(
            [
                "ZPDF-A001 /title",
                "ZPDF-A002 /language",
                "ZPDF-J018 /footer/content/0",
                "ZPDF-J010 /content/0/level",
                "ZPDF-J011 /content/1/columns",
                "ZPDF-J016 /content/2/columns/1/width",
                "ZPDF-J012 /content/2/rows/1",
                "ZPDF-J013 /content/3/height",
                "ZPDF-A003 /content/4/alt",
                "ZPDF-J015 /content/5/style",
                "ZPDF-J013 /content/6/inlines/0/style/size",
                "ZPDF-J019 /content/6/inlines/0/style/lineHeight",
                "ZPDF-J017 /content/7/rows/$data",
            ],
            found);
        Assert.Equal(DiagnosticSeverity.Warning, result.Diagnostics.Single(d => d.Code == "ZPDF-J015").Severity);
    }

    [Fact]
    public void AnUnknownStyleIsOnlyAWarning()
    {
        ValidationResult result = (Valid with { Content = [new Paragraph("x") { Style = "nope" }] }).Validate();
        Assert.True(result.IsValid);
        Assert.Equal("ZPDF-J015", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void StylesFromTheDocumentAndBuiltInsAreKnown()
    {
        PdfDocument document = Valid with
        {
            Styles = new StyleSheet { Styles = ImmutableDictionary<string, TextStyle>.Empty.Add("note", new TextStyle { Size = Length.Pt(8) }) },
            Content = [new Paragraph("a") { Style = "note" }, new Paragraph("b") { Style = "footer" }],
        };

        Assert.Empty(document.Validate().Diagnostics);
    }

    [Fact]
    public void DecorativeImagesNeedNoAltText() =>
        Assert.Empty((Valid with { Content = [new Image(ImageSource.FromSlot("rule")) { Decorative = true }] }).Validate().Diagnostics);

    [Fact]
    public void MarginsMustLeaveRoom()
    {
        PdfDocument document = Valid with { Page = PageSettings.A4 with { Margin = Thickness.All(Length.Cm(10.4)) } };
        Assert.Equal("ZPDF-J014 /page/margin", Assert.Single(document.Validate().Diagnostics.Select(d => $"{d.Code} {d.Path}")));
    }

    [Fact]
    public void ADefaultArrayFromCSharpIsReportedNotThrown()
    {
        PdfDocument document = Valid with { Content = default };
        Assert.Equal("ZPDF-J004 /content", Assert.Single(document.Validate().Diagnostics.Select(d => $"{d.Code} {d.Path}")));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("nb-NO")]
    [InlineData("zh-Hant-TW")]
    [InlineData("de-CH-1996")]
    public void AcceptsLanguageTags(string tag) => Assert.Empty((Valid with { Language = tag }).Validate().Diagnostics);
}
