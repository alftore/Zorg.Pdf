using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Rendering;
using Zorg.Pdf.Tests.Fonts;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Section breaks and page labels: roman front matter, a restarted body, a prefixed appendix.</summary>
public sealed class SectionTests
{
    private static readonly PdfDocument Thesis = new("Thesis")
    {
        Numbering = PageNumberStyle.LowerRoman,
        Footer = new PageBand([new Paragraph(["Page ", Field.PageNumber, " of ", Field.PageCount])]),
        Content =
        [
            new TableOfContents { Title = "Contents" },
            new SectionBreak { Numbering = PageNumberStyle.Number },
            new Heading("Introduction"),
            new PageBreak(),
            new Heading("Method"),
            new SectionBreak { Prefix = "A-", StartAt = 1 },
            new Heading("Appendix"),
        ],
    };

    [Fact]
    public async Task SectionsNumberThePagesTheFootersAndTheContents()
    {
        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(Thesis, PdfInspect.Options(PdfConformance.PdfUA1));

        Assert.False(result.HasWarnings, string.Join(Environment.NewLine, result.Diagnostics));
        PdfFile.CheckStructure(file);
        Assert.Contains("/PageLabels <</Nums [0 <</S /r>> 1 <</S /D>> 3 <</S /D /P (A-)>>]>>", PdfInspect.Text(file), StringComparison.Ordinal);

        var pages = PdfInspect.PageStreams(file);
        Assert.Equal(4, pages.Count);
        Assert.Equal(["Page i of 4", "Page 1 of 4", "Page 2 of 4", "Page A-1 of 4"], pages.Select(p => string.Concat(Text(p[^1].Content))));
        Assert.Equal(["1", "2", "A-1"], Text(pages[0][1].Content)); // The contents' numbers are labels too.
    }

    [Fact]
    public async Task ABreakOnAFreshPageAddsNoBlankPage()
    {
        var document = new PdfDocument("x") { Content = [new SectionBreak { Numbering = PageNumberStyle.UpperRoman }, new Paragraph("One"), new SectionBreak(), new SectionBreak { Numbering = PageNumberStyle.Number, StartAt = 5 }, new Paragraph("Two")] };

        (byte[] file, _) = await RenderTests.RenderAsync(document, PdfInspect.Options());

        Assert.Equal(2, PdfInspect.PageStreams(file).Count);
        Assert.Contains("/PageLabels <</Nums [0 <</S /R>> 1 <</S /D /St 5>>]>>", PdfInspect.Text(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainNumberingWritesNoLabels()
    {
        (byte[] file, _) = await RenderTests.RenderAsync(new PdfDocument("x") { Content = [new Paragraph("a"), new SectionBreak(), new Paragraph("b")] }, PdfInspect.Options());

        Assert.DoesNotContain("/PageLabels", PdfInspect.Text(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4, PageNumberStyle.LowerRoman, "iv")]
    [InlineData(1994, PageNumberStyle.UpperRoman, "MCMXCIV")]
    [InlineData(27, PageNumberStyle.UpperLetter, "AA")]
    [InlineData(54, PageNumberStyle.LowerLetter, "bbb")]
    [InlineData(12, PageNumberStyle.Number, "12")]
    public void NumbersAreFormattedAsViewersCount(int number, PageNumberStyle style, string expected) =>
        Assert.Equal(expected, RenderEngine.Number(number, style));

    [Fact]
    public void SectionBreaksRoundTripAndAreChecked()
    {
        PdfDocument parsed = PdfDocument.ParseJson("""{"title":"x","numbering":"lowerRoman","content":[{"type":"sectionBreak","numbering":"upperLetter","startAt":3,"prefix":"B-"}]}""");
        Assert.Equal(PageNumberStyle.LowerRoman, parsed.Numbering);
        Assert.Equal(new SectionBreak { Numbering = PageNumberStyle.UpperLetter, StartAt = 3, Prefix = "B-" }, parsed.Content[0]);
        Assert.Equal(parsed.ToJson(), PdfDocument.ParseJson(parsed.ToJson()).ToJson());

        var bad = new PdfDocument("x") { Content = [new SectionBreak { StartAt = 0 }, new SectionBreak { Prefix = "a\tb" }, new List([new ListItem([new Paragraph("x")])])] };
        Assert.Equal(["ZPDF-J029 /content/0/startAt", "ZPDF-J029 /content/1/prefix"], bad.Validate().Diagnostics.Select(d => $"{d.Code} {d.Path}"));
    }

    /// <summary>The text of each Tj or TJ in a stream, read back through the test font's character map.</summary>
    private static List<string> Text(string content)
    {
        TrueTypeFont font = TrueTypeFont.Parse(TestFonts.Require());
        var characters = new Dictionary<string, char>();
        foreach (char c in Enumerable.Range(32, 95).Select(i => (char)i))
        {
            characters.TryAdd(font.GlyphFor(c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture), c);
        }

        return [.. Regex.Matches(content, @"(?:<([0-9A-F]+)> Tj|\[(.*?)\] TJ)").Select(m =>
            string.Concat(Regex.Matches(m.Groups[1].Success ? $"<{m.Groups[1].Value}>" : m.Groups[2].Value, @"<([0-9A-F]+)>")
                .SelectMany(h => h.Groups[1].Value.Chunk(4).Select(g => characters.TryGetValue(new string(g), out char c) ? c : '?'))))];
    }
}
