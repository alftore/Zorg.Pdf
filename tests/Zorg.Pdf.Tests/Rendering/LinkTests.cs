using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Rendering;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Links: annotations with areas per line, tagged as Link elements with an OBJR, for URIs and block ids.</summary>
public sealed partial class LinkTests
{
    private static readonly PdfDocument Document = new("Links")
    {
        Content =
        [
            new Paragraph(["Read ", new Link(["our terms"]) { Target = "https://example.com/terms?a=1&b=æ", Description = "Terms of sale" }, " or see ", new Link(["the appendix"]) { Target = "#appendix" }, "."]),
            new Paragraph(["Wrapped: ", new Link([string.Concat(Enumerable.Repeat("long link text ", 20))]) { Target = "mailto:kari@example.com" }]),
            new PageBreak(),
            new Heading("Appendix") { Id = "appendix" },
        ],
    };

    [Fact]
    public async Task LinksAreAnnotationsTaggedAsLinkElements()
    {
        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(Document, PdfInspect.Options(PdfConformance.PdfUA1));

        Assert.False(result.HasWarnings);
        PdfFile.CheckStructure(file);
        string text = PdfInspect.Text(file);
        string[] annotations = [.. AnnotationPattern().Matches(text).Select(m => m.Value)];
        Assert.Equal(3, annotations.Length);
        Assert.All(annotations, a => Assert.Matches(@"/Border \[0 0 0\] /F 4 /StructParent \d+ /Contents ", a));
        Assert.Contains(annotations, a => a.Contains("/Contents (Terms of sale) /A <</S /URI /URI (https://example.com/terms?a=1&b=%C3%A6)>>", StringComparison.Ordinal));
        Assert.Contains(annotations, a => a.Contains("/Contents (the appendix) /Dest (appendix)", StringComparison.Ordinal));
        string wrapped = Assert.Single(annotations, a => a.Contains("mailto:", StringComparison.Ordinal));
        int points = Regex.Match(wrapped, @"/QuadPoints \[([^\]]*)\]").Groups[1].Value.Split(' ').Length;
        Assert.True(points >= 16 && points % 8 == 0, $"{points} numbers"); // Eight per line, on several lines.

        // Three Link elements, each with its text's marked content and an object reference to its annotation.
        Assert.Equal(3, PdfInspect.Elements(file, "Link").Count);
        Assert.Equal(3, Regex.Count(text, @"<</Type /OBJR /Pg \d+ 0 R /Obj \d+ 0 R>>"));
        Assert.Matches(@"/Annots \[\d+ 0 R \d+ 0 R \d+ 0 R\]", text);
        Assert.Equal(3, Regex.Count(PdfInspect.PageContents(file)[0], @"/Link <</MCID \d+>> BDC"));
    }

    [Fact]
    public async Task ParentTreeKeysCoverPagesAndAnnotationsInOrder()
    {
        (byte[] file, _) = await RenderTests.RenderAsync(Document, PdfInspect.Options());

        string text = PdfInspect.Text(file);
        int[] keys = [.. Regex.Matches(Regex.Match(text, @"/Nums \[(.*?)\]>>", RegexOptions.Singleline).Groups[1].Value, @"(?<![\d.])(\d+) (?=\[|\d+ 0 R)").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];
        Assert.Equal(Enumerable.Range(0, 5), keys); // Page 1, its three links, page 2.
        Assert.Matches(@"/ParentTreeNextKey 5", text);
    }

    [Theory]
    [InlineData("https://bücher.example/ä(b)", "https://xn--bcher-kva.example/%C3%A4(b)")]
    [InlineData("https://example.com/terms?a=1&b=æ", "https://example.com/terms?a=1&b=%C3%A6")]
    [InlineData("mailto:kari@example.com", "mailto:kari@example.com")]
    public void LinkTargetsAreWrittenAsAsciiWithPunycodeHosts(string target, string expected) =>
        Assert.Equal(expected, RenderEngine.AsciiUri(target));

    [Fact]
    public void LinksRoundTripAndAreChecked()
    {
        PdfDocument parsed = PdfDocument.ParseJson("""{"title":"x","content":[{"type":"paragraph","inlines":[{"link":"https://example.com","inlines":["x"],"description":"Example"}]}]}""");
        Link link = Assert.IsType<Link>(((Paragraph)parsed.Content[0]).Inlines[0]);
        Assert.Equal(("https://example.com", "Example"), (link.Target, link.Description));
        Assert.Equal(parsed.ToJson(), PdfDocument.ParseJson(parsed.ToJson()).ToJson());

        var bad = new PdfDocument("x")
        {
            Content =
            [
                new Paragraph([new Link(["a"]) { Target = "javascript:alert(1)" }]),
                new Paragraph([new Link(["b"]) { Target = "#nowhere" }]),
                new Paragraph([new Link([new Link(["c"]) { Target = "https://x.org" }]) { Target = "https://y.org" }]),
                new Table([new Column("A")]) { Rows = TableRows.Inline([[new Cell([new Link(["d"]) { Target = "https://x.org" }])]]) },
            ],
            Footer = new PageBand([new Paragraph([new Link(["e"]) { Target = "https://x.org" }])]),
        };

        Assert.Equal(
            ["ZPDF-J027 /footer/content/0/inlines/0", "ZPDF-J027 /content/0/inlines/0/link", "ZPDF-J027 /content/2/inlines/0/inlines/0", "ZPDF-J027 /content/3/rows/0/0/inlines/0", "ZPDF-J027 /content/1/inlines/0/link"],
            bad.Validate().Diagnostics.Select(d => $"{d.Code} {d.Path}"));
    }

    [GeneratedRegex(@"/Type /Annot /Subtype /Link .*?>>(?=\s*endobj|\n|$)", RegexOptions.Singleline)]
    private static partial Regex AnnotationPattern();
}
