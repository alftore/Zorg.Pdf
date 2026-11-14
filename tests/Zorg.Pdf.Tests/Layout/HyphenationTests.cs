using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Hyphenation;
using Zorg.Pdf.Tests.Rendering;

namespace Zorg.Pdf.Tests.Layout;

/// <summary>Hyphenation: Liang's patterns from hyph-utf8, and the layout breaking only the word that overflows.</summary>
public sealed partial class HyphenationTests
{
    [Theory]
    [InlineData("en-US", "hyphenation", "hy-phen-ation")]
    [InlineData("en", "computer", "com-puter")] // US English keeps three letters after a hyphen.
    [InlineData("en-GB", "hyphenation", "hy-phen-a-tion")]
    [InlineData("de", "Versicherungsgesellschaft", "Ver-si-che-rungs-ge-sell-schaft")]
    [InlineData("de-CH", "Donaudampfschifffahrt", "Do-nau-dampf-schiff-fahrt")]
    [InlineData("nb", "sykehusavdeling", "syke-hus-av-de-ling")]
    [InlineData("nn-NO", "kommunestyre", "kom-mune-sty-re")]
    [InlineData("fr", "anticonstitutionnellement", "an-ti-cons-ti-tu-tion-nel-le-ment")]
    [InlineData("da", "kommunalbestyrelse", "kom-mu-nal-be-sty-rel-se")]
    public void WordsBreakWhereTheLanguageAllows(string language, string word, string expected) =>
        Assert.Equal(expected, Hyphenate(language, word));

    [Theory]
    [InlineData("hyph-en-us.tex", "en-US")]
    [InlineData("hyph-en-gb.tex", "en-GB")]
    [InlineData("hyph-nb.tex", "nb")]
    [InlineData("hyph-nn.tex", "nn")]
    [InlineData("hyph-no.tex", "nb")]
    public void EveryListedExceptionComesBackAsListed(string file, string language)
    {
        using Stream stream = typeof(PatternHyphenator).Assembly.GetManifestResourceStream(file)!;
        string text = Regex.Replace(new StreamReader(stream, Encoding.UTF8).ReadToEnd(), @"(?<!\\)%[^\n]*", "");
        string[] listed = Regex.Match(text, @"\\hyphenation\s*\{([^}]*)\}").Groups[1].Value.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(listed);
        (int left, int right) = language == "nb" || language == "nn" ? (2, 2) : (2, 3);
        string[] overridden = file == "hyph-no.tex" ? ["attende", "betre"] : []; // hyph-nb.tex lists these again, differently.
        foreach (string entry in listed.Where(e => e.All(c => char.IsLetter(c) || c == '-') && !overridden.Contains(e.Replace("-", "", StringComparison.Ordinal))))
        {
            Assert.Equal(TrimToMinimums(entry, left, right), Hyphenate(language, entry.Replace("-", "", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void ThePatternFilesShipUnchanged()
    {
        // Their licences ask that the notices stay; byte-for-byte copies keep them and every other line.
        string[] sums = File.ReadAllLines(Zorg.Pdf.Tests.Model.RepoFiles.Path("src", "Zorg.Pdf.Hyphenation", "Patterns", "SHA256SUMS"));
        Assert.Equal(8, sums.Length);
        foreach (string line in sums)
        {
            string[] parts = line.Split("  ");
            using Stream stream = typeof(PatternHyphenator).Assembly.GetManifestResourceStream(parts[1])!;
            Assert.Equal(parts[0], Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)));
        }
    }

    [Fact]
    public void UnknownLanguagesAreLeftAlone()
    {
        Assert.False(PatternHyphenator.Default.Supports("sv"));
        Assert.True(PatternHyphenator.Default.Supports("no-NO"));
        bool[] breaks = new bool[8];
        Assert.False(PatternHyphenator.Default.TryHyphenate("sv", "kommunen", breaks));
        Assert.DoesNotContain(true, breaks);
    }

    [Fact]
    public async Task AutoHyphenationBreaksOnlyTheOverflowingWordAndCopiesAsASoftHyphen()
    {
        const string Text = "Forsikringsselskapet behandler skadesaken etter at dokumentasjonen fra verkstedet er mottatt og kontrollert.";
        var document = new PdfDocument("x")
        {
            Language = "nb",
            Page = new PageSettings { Size = new PageSize(Length.Mm(70), Length.Mm(200)), Margin = Thickness.All(Length.Mm(10)) },
            Content = [new Paragraph(Text) { Style = "hyphenated", Align = TextAlign.Justify }, new Paragraph(Text) { Align = TextAlign.Justify }],
            Styles = new StyleSheet { Styles = StyleSheet.Default.Styles.SetItem("hyphenated", new TextStyle { Hyphens = Hyphens.Auto }) },
        };
        RenderOptions options = PdfInspect.Options();

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, new RenderOptions { Fonts = options.Fonts, Hyphenator = PatternHyphenator.Default });
        (_, RenderResult without) = await RenderTests.RenderAsync(document, options);

        Assert.Empty(result.Diagnostics);
        string content = PdfInspect.PageContents(file)[0];
        int hyphens = Regex.Count(content, @"/Span <</ActualText <FEFF00AD>>> BDC");
        Assert.InRange(hyphens, 1, 6); // The first paragraph only: the second is Manual.
        Assert.Equal("ZPDF-T003", Assert.Single(without.Diagnostics).Code); // Auto without a hyphenator: a note, once.
    }

    [Fact]
    public async Task NoneIgnoresSoftHyphensAndHeadingsStayManual()
    {
        var document = new PdfDocument("x")
        {
            Page = new PageSettings { Size = new PageSize(Length.Mm(40), Length.Mm(100)), Margin = Thickness.All(Length.Mm(5)) },
            Content = [new Paragraph("Super\u00ADcali\u00ADfragi\u00ADlistic\u00ADexpiali\u00ADdocious words") { Style = "none" }],
            Styles = new StyleSheet { Styles = StyleSheet.Default.Styles.SetItem("none", new TextStyle { Hyphens = Hyphens.None }) },
        };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, PdfInspect.Options());

        Assert.DoesNotContain("ActualText", PdfInspect.PageContents(file)[0], StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, d => d.Code == "ZPDF-T001"); // Broken between characters instead.
        Assert.Equal(Hyphens.Manual, StyleSheet.Default.Styles["heading1"].Hyphens);
    }

    private static string Hyphenate(string language, string word)
    {
        bool[] breaks = new bool[word.Length];
        Assert.True(PatternHyphenator.Default.TryHyphenate(language, word, breaks));
        var text = new StringBuilder();
        for (int i = 0; i < word.Length; i++)
        {
            text.Append(word[i]);
            if (breaks[i])
            {
                text.Append('-');
            }
        }

        return text.ToString();
    }

    /// <summary>A listed hyphenation without the breaks too near either end.</summary>
    private static string TrimToMinimums(string entry, int left, int right)
    {
        string word = entry.Replace("-", "", StringComparison.Ordinal);
        var text = new StringBuilder();
        int letters = 0;
        foreach (char c in entry)
        {
            if (c == '-')
            {
                if (letters >= left && word.Length - letters >= right)
                {
                    text.Append('-');
                }
            }
            else
            {
                text.Append(c);
                letters++;
            }
        }

        return text.ToString();
    }
}
