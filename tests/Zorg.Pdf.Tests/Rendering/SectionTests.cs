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
    public async Task ABreakWithNothingAfterItAddsNoPage()
    {
        var document = new PdfDocument("x") { Content = [new Paragraph("a"), new SectionBreak { Numbering = PageNumberStyle.LowerRoman }] };

        (byte[] file, _) = await RenderTests.RenderAsync(document, PdfInspect.Options());

        Assert.Single(PdfInspect.PageStreams(file));
        Assert.DoesNotContain("/PageLabels", PdfInspect.Text(file), StringComparison.Ordinal);
    }

    [Fact]
    public void LargeStartsAreRefusedWhereTheyWouldMakeLongOrOverflowingLabels()
    {
        static PdfDocument Start(PageNumberStyle style, int start) => new("x") { Content = [new SectionBreak { Numbering = style, StartAt = start }, new Paragraph("a")] };

        Assert.Equal("ZPDF-J029", Assert.Single(Start(PageNumberStyle.UpperLetter, 2_000_000_000).Validate().Diagnostics).Code);
        Assert.Empty(Start(PageNumberStyle.UpperLetter, 260).Validate().Diagnostics);
        Assert.Empty(Start(PageNumberStyle.Number, 1_000_000_000).Validate().Diagnostics);
        Assert.Single(Start(PageNumberStyle.Number, int.MaxValue).Validate().Diagnostics);
    }

    [Fact]
    public async Task TheContentsMakeRoomForLongLabels()
    {
        static PdfDocument Contents(string? prefix) => new("x")
        {
            Content = [new TableOfContents(), new SectionBreak { Numbering = PageNumberStyle.UpperRoman, StartAt = 3888, Prefix = prefix }, new Heading("Late")],
        };

        (byte[] plain, _) = await RenderTests.RenderAsync(Contents(null), PdfInspect.Options());
        (byte[] prefixed, _) = await RenderTests.RenderAsync(Contents("Appendix-Section-"), PdfInspect.Options());

        static int Dots(byte[] file) => Text(PdfInspect.PageStreams(file)[0][0].Content).Max(t => t.Count(c => c == '.'));
        Assert.Contains("Appendix-Section-MMMDCCCLXXXVIII", Text(PdfInspect.PageStreams(prefixed)[0][1].Content));
        Assert.True(Dots(prefixed) <= Dots(plain) - 10, $"Leaders: {Dots(prefixed)} with the prefix, {Dots(plain)} without."); // The leaders stop before the longer label.
    }

    [Fact]
    public async Task PlainNumberingWritesNoLabels()
    {
        (byte[] file, _) = await RenderTests.RenderAsync(new PdfDocument("x") { Content = [new Paragraph("a"), new SectionBreak(), new Paragraph("b")] }, PdfInspect.Options());

        Assert.DoesNotContain("/PageLabels", PdfInspect.Text(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SectionsHaveTheirOwnHeaderAndFooter()
    {
        var footer = new PageBand([new Paragraph(["Page ", Field.PageNumber])]);
        var header = new PageBand([new Paragraph("Appendix"), new Paragraph("Second header line"), new Paragraph("Third header line")]);
        var document = new PdfDocument("Report")
        {
            Content =
            [
                new Heading("Cover"),                                                        // No footer: the document has none.
                new SectionBreak { StartAt = 1, Footer = footer },
                new Paragraph("Body one."),
                new PageBreak(),
                new Paragraph("Body two."),
                new SectionBreak { Header = header },                                        // Keeps the footer, adds a header.
                new Paragraph("Appendix text."),
                new SectionBreak { Header = PageBand.None, Footer = PageBand.None },
                new Paragraph("The end."),
            ],
        };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, PdfInspect.Options(PdfConformance.PdfUA1));

        Assert.False(result.HasWarnings, string.Join(Environment.NewLine, result.Diagnostics));
        PdfFile.CheckStructure(file);
        var pages = PdfInspect.PageStreams(file);
        Assert.Equal([1, 2, 2, 2, 1], pages.Select(p => p.Length)); // Bands on the body and the appendix only.
        Assert.Equal(["Page 1", "Page 2"], pages.Skip(1).Take(2).Select(p => string.Concat(Text(p[1].Content))));
        Assert.Equal(["Appendix", "Second header line", "Third header line", "Page 3"], Text(pages[3][1].Content));

        // The header pushes the appendix text down; without bands the last page's text starts at the margin again.
        double Top(string content) => double.Parse(Regex.Match(content, @"1 0 0 1 [\d.]+ ([\d.]+) Tm").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Top(pages[3][0].Content) < Top(pages[1][0].Content) - 20);
        Assert.Equal(Top(pages[1][0].Content), Top(pages[4][0].Content), 6); // Same text style, no header: the same first baseline.
    }

    [Fact]
    public async Task ABandWithoutFieldsIsSharedWithinItsSection()
    {
        var document = new PdfDocument("x")
        {
            Footer = new PageBand([new Paragraph("Confidential")]),
            Content = [new Paragraph("a"), new PageBreak(), new Paragraph("b"), new SectionBreak { Footer = new PageBand([new Paragraph("Appendix")]) }, new Paragraph("c"), new PageBreak(), new Paragraph("d")],
        };

        (byte[] file, _) = await RenderTests.RenderAsync(document, PdfInspect.Options());

        var bands = PdfInspect.PageStreams(file).Select(p => p[1].Number).ToArray();
        Assert.Equal(4, bands.Length);
        Assert.Equal(bands[0], bands[1]);
        Assert.Equal(bands[2], bands[3]);
        Assert.NotEqual(bands[0], bands[2]);
    }

    [Fact]
    public void SectionBandsAreCheckedWhereTheyAre()
    {
        var document = new PdfDocument("x")
        {
            Content = [new Paragraph("a"), new SectionBreak { Footer = new PageBand([new Heading("Not in a band")]) }],
        };

        Assert.Equal("ZPDF-J018 /content/1/footer/content/0", Assert.Single(document.Validate().Diagnostics.Select(d => $"{d.Code} {d.Path}")));
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
