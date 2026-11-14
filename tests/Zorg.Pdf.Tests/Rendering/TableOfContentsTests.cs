using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Tests.Fonts;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Tables of contents: entries laid out in place, numbers drawn when the headings' pages are known, links and tags.</summary>
public sealed partial class TableOfContentsTests
{
    [Fact]
    public async Task TheNumbersAreThePagesTheHeadingsLandOn()
    {
        var document = new PdfDocument("Report")
        {
            Content =
            [
                new TableOfContents { Title = "Contents" },
                new PageBreak(),
                new Heading("First"),
                new Heading("Inside") { Level = 2 },
                new PageBreak(),
                new Heading("Second") { Id = "second" },
                .. Enumerable.Repeat<Block>(new Paragraph(string.Concat(Enumerable.Repeat("Filler text. ", 200))), 12),
                new Heading("Last"),
            ],
        };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, PdfInspect.Options(PdfConformance.PdfUA1));

        Assert.False(result.HasWarnings, string.Join(Environment.NewLine, result.Diagnostics));
        PdfFile.CheckStructure(file);
        var pages = PdfInspect.PageStreams(file);
        int lastPage = pages.Count;
        Assert.True(lastPage > 3);

        // The first page has a second stream with the numbers, each in the Link of its entry.
        Assert.Equal(2, pages[0].Length);
        string numbers = pages[0][1].Content;
        Assert.Equal(["2", "2", "3", lastPage.ToString(System.Globalization.CultureInfo.InvariantCulture)], Digits(numbers));
        Assert.Equal(4, Regex.Count(numbers, @"/Link <</MCID \d+>> BDC"));

        string text = PdfInspect.Text(file);
        Assert.Single(PdfInspect.Elements(file, "TOC"));
        Assert.Equal(4, PdfInspect.Elements(file, "TOCI").Count);
        Assert.Contains("/Dest (second)", text, StringComparison.Ordinal); // A heading's own id.
        Assert.Contains("/Dest (_h2)", text, StringComparison.Ordinal);    // Generated for the others.
        Assert.Matches(@"\(_h2\) \[\d+ 0 R /XYZ null", text);
    }

    [Fact]
    public async Task ALongTableOfContentsRunsOverPages()
    {
        var document = new PdfDocument("Manual")
        {
            Content = [new TableOfContents { MaxLevel = 1, Leaders = false }, .. Enumerable.Range(1, 120).Select(i => (Block)new Heading($"Chapter {i}"))],
        };

        (byte[] file, _) = await RenderTests.RenderAsync(document, PdfInspect.Options());

        var pages = PdfInspect.PageStreams(file);
        int withNumbers = pages.Count(p => p.Length == 2);
        Assert.True(withNumbers >= 2, $"{withNumbers} pages of contents");
        Assert.Equal(120, pages.Where(p => p.Length == 2).Sum(p => Regex.Count(p[1].Content, "BDC")));
        Assert.DoesNotContain("/Artifact", pages[0][0].Content, StringComparison.Ordinal); // No leaders.
    }

    [Fact]
    public void ATableOfContentsRoundTripsAndIsChecked()
    {
        PdfDocument parsed = PdfDocument.ParseJson("""{"title":"x","content":[{"type":"tableOfContents","title":"Contents","maxLevel":2,"leaders":false}]}""");
        Assert.Equal(new TableOfContents { Title = "Contents", MaxLevel = 2, Leaders = false }, parsed.Content[0]);
        Assert.Equal(parsed.ToJson(), PdfDocument.ParseJson(parsed.ToJson()).ToJson());

        var document = new PdfDocument("x")
        {
            Content = [new TableOfContents { MaxLevel = 7 }, new TableOfContents(), new List([new ListItem([new Paragraph("x")])])],
        };
        Assert.Equal(["ZPDF-J028 /content/0/maxLevel", "ZPDF-J028 /content/1"], document.Validate().Diagnostics.Select(d => $"{d.Code} {d.Path}"));
    }

    /// <summary>The numbers drawn in a stream, read back through the test font's character map.</summary>
    private static List<string> Digits(string content)
    {
        TrueTypeFont font = TrueTypeFont.Parse(TestFonts.Require());
        var digits = Enumerable.Range('0', 10).ToDictionary(c => font.GlyphFor(c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture), c => (char)c);
        return [.. Regex.Matches(content, @"<([0-9A-F]+)> Tj").Select(m => new string([.. m.Groups[1].Value.Chunk(4).Select(g => digits[new string(g)])]))];
    }
}
