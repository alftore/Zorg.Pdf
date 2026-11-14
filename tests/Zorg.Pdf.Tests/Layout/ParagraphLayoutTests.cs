using System;
using System.Linq;
using Xunit;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Layout;
using Zorg.Pdf.Tests.Fonts;

namespace Zorg.Pdf.Tests.Layout;

public sealed class ParagraphLayoutTests
{
    private const double Size = 12;
    private const string Long = "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs. Sphinx of black quartz, judge my vow.";

    private readonly EmbeddedFont _font = new(TrueTypeFont.Parse(TestFonts.Require()));

    [Fact]
    public void WrapsInsideTheWidth()
    {
        ParagraphLayout layout = Layout(Long, 150);

        Assert.True(layout.Lines.Count >= 4);
        Assert.All(layout.Lines, line => Assert.True(line.Width <= 150 + 1e-6, $"A line is {line.Width} pt wide."));
        Assert.Empty(layout.Diagnostics);

        // Nothing is lost: every glyph that isn't a hanging space is drawn, in order.
        ushort[] expected = [.. _font.Encode(Long).Where(g => g != Glyph(' '))];
        ushort[] drawn = [.. layout.Lines.SelectMany(l => l.Glyphs).Where(g => g != Glyph(' '))];
        Assert.Equal(expected, drawn);
    }

    [Fact]
    public void BreaksAfterSpacesAndNeverStartsALineWithOne()
    {
        ParagraphLayout layout = Layout("aaa  bbb ccc", WidthOf("aaa  bbb"));

        Assert.Equal(["aaa  bbb", "ccc"], Texts(layout));
        Assert.Equal(["aaa", "bbb", "ccc"], Texts(Layout("aaa  bbb ccc", WidthOf("aaa  b"))));
    }

    [Fact]
    public void BreaksAfterHyphensButNotBeforeDigits()
    {
        Assert.Equal(["well-", "known"], Texts(Layout("well-known", WidthOf("well-kn"))));
        Assert.Equal(["pages", "10-15"], Texts(Layout("pages 10-15", WidthOf("pages 10-1"))));
    }

    [Fact]
    public void NoBreakSpacesHoldWordsTogether() =>
        Assert.Equal(["a", "b c"], Texts(Layout("a b c", WidthOf("a b "))));

    [Fact]
    public void SoftHyphensShowOnlyWhereTheLineBreaks()
    {
        const string Word = "Blåbær­syltetøy";
        Assert.Equal(["Blåbær-", "syltetøy"], Texts(Layout(Word, WidthOf("Blåbær-sy"))));
        Assert.Equal(["Blåbærsyltetøy"], Texts(Layout(Word, 1000)));
    }

    [Fact]
    public void LineFeedsAlwaysBreakAndKeepEmptyLines()
    {
        Assert.Equal(["one", "two", "three", "four"], Texts(Layout("one\ntwo\r\nthree" + (char)0x2028 + "four", 1000)));
        Assert.Equal(["a", "", "b"], Texts(Layout("a\n\nb", 1000)));
    }

    [Fact]
    public void AWordWiderThanTheColumnIsBrokenAndReported()
    {
        ParagraphLayout layout = Layout("Hi Supercalifragilisticexpialidocious", WidthOf("Supercal"));

        Assert.True(layout.Lines.Count >= 4);
        Assert.All(layout.Lines, line => Assert.NotEmpty(line.Glyphs));
        TextDiagnostic diagnostic = Assert.Single(layout.Diagnostics, d => d.Index == 3);
        Assert.Equal(ParagraphLayout.WordTooWide, diagnostic.Code);
        Assert.Contains("index 3", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JustifiedLinesFillTheWidthExceptTheLast()
    {
        ParagraphLayout layout = Layout(Long, 180, TextAlign.Justify);

        foreach (LayoutLine line in layout.Lines.SkipLast(1))
        {
            Assert.Equal(180, line.Width, 6);
            double natural = WidthOfGlyphs(line);
            Assert.Equal(180, natural, 3); // The glyphs plus all the extra space add up to the width.
        }

        Assert.True(layout.Lines[^1].Width < 180);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AlignsShortLines(int alignment)
    {
        var align = (TextAlign)alignment; // The enum is internal, and xUnit theories must be public.
        LayoutLine line = Assert.Single(Layout("Hello", 200, align).Lines);
        double expected = align switch { TextAlign.Right => 200 - line.Width, TextAlign.Center => (200 - line.Width) / 2, _ => 0 };
        Assert.Equal(expected, line.Offset, 6);
    }

    [Fact]
    public void KerningIsInTheAdjustmentsAndTheWidth()
    {
        LayoutLine kerned = Assert.Single(Layout("AV", 1000).Lines);
        double unkerned = (_font.Width(Glyph('A')) + _font.Width(Glyph('V'))) * Size / 1000;

        Assert.True(kerned.ExtraAfter[0] < 0);
        Assert.True(kerned.Width < unkerned);
    }

    [Fact]
    public void ControlCharactersAreRefusedWithTheirIndex()
    {
        PdfException error = Assert.Throws<PdfException>(() => Layout("ab\u0007c", 100));
        Assert.Contains("U+0007 at index 2", error.Message, StringComparison.Ordinal);
        Assert.Equal(["a b"], Texts(Layout("a\tb", 1000)));
    }

    private ParagraphLayout Layout(string text, double width, TextAlign align = TextAlign.Left) =>
        ParagraphLayout.Create(text, _font, Size, width, align);

    /// <summary>The width of text on one line, plus a hair so it just fits.</summary>
    private double WidthOf(string text) => Assert.Single(Layout(text, 10_000).Lines).Width + 0.01;

    private double WidthOfGlyphs(LayoutLine line) =>
        line.Glyphs.Select((g, i) => _font.Width(g) + line.ExtraAfter[i]).Sum() * Size / 1000;

    private ushort Glyph(char c) => _font.Font.GlyphFor(c);

    /// <summary>Each line's text, from a reverse map of the characters these tests use.</summary>
    private string[] Texts(ParagraphLayout layout)
    {
        const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789åæøÅÆØ -., ";
        var map = new System.Collections.Generic.Dictionary<ushort, char>();
        foreach (char c in Alphabet)
        {
            map.TryAdd(Glyph(c), c); // In Arial the no-break space shares the space's glyph.
        }

        // Fonts differ in whether U+00A0 has its own glyph, so it always reads back as a plain space.
        return [.. layout.Lines.Select(l => new string([.. l.Glyphs.Select(g => map[g])]).Replace(' ', ' '))];
    }
}
