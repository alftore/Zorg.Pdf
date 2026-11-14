using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Tests.Fonts;

namespace Zorg.Pdf.Tests.Rendering;

public sealed class FontRenderingTests
{
    private static readonly PdfDocument Mixed = new("Fonts")
    {
        Content =
        [
            new Heading("Heading"),
            new Paragraph(["Plain, ", new TextRun("bold") { Style = TextStyle.Bold }, ", ", new TextRun("italic") { Style = TextStyle.Italic }, " and plain again."]),
        ],
    };

    [Fact]
    public async Task UsesTheRealBoldAndItalicFacesWhenTheFamilyHasThem()
    {
        var options = new RenderOptions
        {
            Fonts = FontCollection.Empty.Add(new FontFamily("Body")
            {
                Regular = FontSource.FromBytes(TestFonts.Require()),
                Bold = FontSource.FromBytes(TestFonts.Require("bold")),
                Italic = FontSource.FromBytes(TestFonts.Require("italic")),
            }),
        };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(Mixed, options);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ZPDF-F003");
        Assert.Equal(3, Regex.Count(PdfInspect.Text(file), "/Subtype /Type0")); // Regular, bold and italic embedded.
        string page = PdfInspect.PageContents(file)[0];
        Assert.DoesNotContain(" Tr\n", page, StringComparison.Ordinal); // No synthetic bold.
        Assert.DoesNotContain("1 0 0.2126 1 ", page, StringComparison.Ordinal); // No synthetic italic.
    }

    [Fact]
    public async Task SynthesisesFacesTheFamilyLacksAndSaysSo()
    {
        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(Mixed);

        // Once per face, at its first use: the heading and the bold run both ask for "Body bold".
        Assert.Equal(
            ["Info ZPDF-F003 /content/0", "Info ZPDF-F003 /content/1/inlines/3/style"],
            result.Diagnostics.Select(d => $"{d.Severity} {d.Code} {d.Path}"));
        Assert.Contains("no bold face", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Contains("no italic face", result.Diagnostics[1].Message, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(PdfInspect.Text(file), "/Subtype /Type0")); // One font, drawn three ways.
        string page = PdfInspect.PageContents(file)[0];
        Assert.Contains("2 Tr\n", page, StringComparison.Ordinal); // Fill and stroke for bold.
        Assert.Contains("0 Tr\n", page, StringComparison.Ordinal); // Back to fill.
        Assert.Contains("1 0 0.2126 1 ", page, StringComparison.Ordinal); // 12° slant for italic.
        Assert.Contains("0 0 0 RG", page, StringComparison.Ordinal); // Stroke in the text colour.
    }

    [Fact]
    public async Task RunsChangeSizeAndColourWithinALine()
    {
        var document = new PdfDocument("Runs")
        {
            Content = [new Paragraph(["Total: ", new TextRun("12 450,00") { Style = new TextStyle { Size = Length.Pt(20), Color = Color.Parse("#C00000") } }, " NOK"])],
        };

        (byte[] file, _) = await RenderTests.RenderAsync(document);
        string page = PdfInspect.PageContents(file)[0];

        Assert.Contains("/F1 10 Tf", page, StringComparison.Ordinal);
        Assert.Contains("/F1 20 Tf", page, StringComparison.Ordinal);
        Assert.Contains("0.7529 0 0 rg", page, StringComparison.Ordinal);
        Assert.Contains("0 0 0 rg", page, StringComparison.Ordinal); // Back to black for " NOK".
        Assert.Single(Regex.Matches(page, "BT\n")); // One line, one text object.
    }

    [Fact]
    public async Task AColourDoesNotLeakIntoTheNextParagraph()
    {
        var document = new PdfDocument("Colour")
        {
            Styles = new StyleSheet { Styles = ImmutableDictionary<string, TextStyle>.Empty.Add("red", new TextStyle { Color = Color.Parse("#FF0000") }) },
            Content = [new Paragraph("Red") { Style = "red" }, new Paragraph("Black")],
        };

        string page = PdfInspect.PageContents((await RenderTests.RenderAsync(document)).File)[0];
        int red = page.IndexOf("1 0 0 rg", StringComparison.Ordinal);
        int black = page.IndexOf("0 0 0 rg", StringComparison.Ordinal);
        Assert.True(red >= 0 && black > red, "The second paragraph must reset the fill colour to black.");
    }

    [Fact]
    public async Task FallbackFontsDrawWhatThePrimaryLacks()
    {
        byte[] devanagari = TestFonts.Require("devanagari");
        const string Text = "Namaste: \u0928\u092E\u0938\u094D\u0924\u0947 \u0915";
        var document = new PdfDocument("Fallback") { Content = [new Paragraph(Text)] };

        var without = new RenderOptions { Fonts = FontCollection.Empty.Add(new FontFamily("Body") { Regular = FontSource.FromBytes(TestFonts.Require()) }) };
        (_, RenderResult missing) = await RenderTests.RenderAsync(document, without);
        Assert.Contains(missing.Diagnostics, d => d is { Code: "ZPDF-F002", Severity: DiagnosticSeverity.Warning, Path: "/content/0/inlines/0" });

        var with = new RenderOptions
        {
            Fonts = FontCollection.Empty
                .Add(new FontFamily("Body") { Regular = FontSource.FromBytes(TestFonts.Require()), Fallbacks = ["Devanagari"] })
                .Add(new FontFamily("Devanagari") { Regular = FontSource.FromBytes(devanagari) })
                .WithDefault("Body"),
        };
        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, with);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ZPDF-F002");
        Assert.Equal(2, Regex.Count(PdfInspect.Text(file), "/Subtype /Type0"));
        string page = PdfInspect.PageContents(file)[0];
        Assert.Contains("/F1 10 Tf", page, StringComparison.Ordinal);
        Assert.Contains("/F2 10 Tf", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingGlyphIsAnErrorUnderPdfUa()
    {
        var document = new PdfDocument("x") { Content = [new Paragraph("bad \U000F0000")] };
        var output = new MemoryStream();
        PdfDocumentException error = await Assert.ThrowsAsync<PdfDocumentException>(
            () => document.RenderAsync(output, PdfInspect.Options(PdfConformance.PdfUA1), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("ZPDF-F002 Error /content/0/inlines/0", Assert.Single(error.Diagnostics.Select(d => $"{d.Code} {d.Severity} {d.Path}")));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task ConcurrentRendersShareParsedFontsAndAgreeWithSequentialOnes()
    {
        RenderOptions options = PdfInspect.Options();
        PdfDocument[] documents = [.. Enumerable.Range(0, 16).Select(i => Mixed with { Content = [.. Mixed.Content, new Paragraph($"Document {i}: AVATAR To Wa {new string('x', i)}")] })];

        var sequential = new List<byte[]>();
        foreach (PdfDocument document in documents)
        {
            sequential.Add((await RenderTests.RenderAsync(document, options)).File);
        }

        byte[][] parallel = await Task.WhenAll(documents.Select(document => Task.Run(async () =>
        {
            using var output = new MemoryStream();
            await document.RenderAsync(output, options, TestContext.Current.CancellationToken);
            return output.ToArray();
        })));

        for (int i = 0; i < documents.Length; i++)
        {
            Assert.Equal(sequential[i], parallel[i]);
        }
    }

    [Fact]
    public void TheFontCacheReturnsTheSameParsedFontForTheSameBytes()
    {
        byte[] font = TestFonts.Require();
        TrueTypeFont first = FontCache.GetOrParse(font);
        TrueTypeFont second = FontCache.GetOrParse((byte[])font.Clone());

        Assert.Same(first, second);
        Assert.InRange(FontCache.Count, 1, FontCache.Capacity);
    }

    [Fact]
    public async Task TheSystemResolverFindsInstalledFonts()
    {
        string family = OperatingSystem.IsWindows() ? "Arial" : "DejaVu Sans";
        FontSource? found = await FontResolvers.System().ResolveAsync(new FontRequest(family, FontWeight.Bold, FontStyle.Normal), TestContext.Current.CancellationToken);
        if (found is null)
        {
            Assert.Skip($"{family} isn't installed.");
        }

        var options = new RenderOptions { Fonts = FontCollection.Empty.WithDefault(family), FontResolver = FontResolvers.System() };
        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(Mixed, options);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ZPDF-F003" && d.Path == "/content/0"); // A real bold face was found.
        Assert.Contains("Bold", PdfInspect.Text(file), StringComparison.Ordinal); // e.g. "+Arial-BoldMT" or "+DejaVuSans-Bold".
    }

    [Fact]
    public void TheSystemResolverReadsFamilyAndFace()
    {
        string? path = OperatingSystem.IsWindows() ? @"C:\Windows\Fonts\arialbd.ttf" : "/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf";
        if (!File.Exists(path))
        {
            Assert.Skip("The bold test font isn't installed.");
        }

        (string? family, bool bold, bool italic, _) = SystemFontResolver.Describe(path);
        Assert.Equal(OperatingSystem.IsWindows() ? "Arial" : "DejaVu Sans", family);
        Assert.True(bold);
        Assert.False(italic);
    }

    [Fact]
    public void ASyncRenderRefusesAResolverThatAnswersLater()
    {
        var options = new RenderOptions { Fonts = FontCollection.Empty.WithDefault("Remote"), FontResolver = new SlowResolver() };
        PdfDocumentException error = Assert.Throws<PdfDocumentException>(() => Mixed.Render(new MemoryStream(), options));
        Assert.Contains(error.Diagnostics, d => d.Code == "ZPDF-F004" && d.Message.Contains("RenderAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAsyncResolverWorksWithRenderAsync()
    {
        var options = new RenderOptions { Fonts = FontCollection.Empty.WithDefault("Remote"), FontResolver = new SlowResolver() };
        (byte[] file, _) = await RenderTests.RenderAsync(Mixed, options);
        Assert.True(file.Length > 1000);
    }

    /// <summary>Answers every family with the test font, after a real delay.</summary>
    private sealed class SlowResolver : IFontResolver
    {
        public async ValueTask<FontSource?> ResolveAsync(FontRequest request, CancellationToken cancellationToken = default)
        {
            await Task.Delay(5, cancellationToken);
            return FontSource.FromBytes(TestFonts.Require());
        }
    }
}
