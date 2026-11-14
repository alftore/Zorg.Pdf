using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Xunit;
using Zorg.Pdf.Extensions.AI;
using Zorg.Pdf.Markdown;
using Zorg.Pdf.Tests.Images;
using Zorg.Pdf.Tests.Rendering;
using Zorg.Pdf.Tests.Writing;
using ImageBlock = Zorg.Pdf.Image;
using PdfList = Zorg.Pdf.List;
using PdfTable = Zorg.Pdf.Table;

namespace Zorg.Pdf.Tests.Extensions;

/// <summary>Markdown to documents with Markdig: the blocks and inlines each construct becomes, and what's dropped.</summary>
public sealed class MarkdownTests
{
    [Fact]
    public void HeadingsParagraphsAndEmphasisBecomeTheirBlocksAndStyles()
    {
        MarkdownResult result = MarkdownDocument.Parse("# Minutes\n\nPresent: **Kari**, *Ola* and ***Per***.\nSecond line  \nafter a hard break.\n\n### Budget");

        Assert.Empty(result.Diagnostics);
        Assert.Equal("Minutes", result.Document.Title);
        Assert.Equal("en", result.Document.Language);
        Assert.Collection(result.Document.Content,
            b => Assert.Equal(1, Assert.IsType<Heading>(b).Level),
            b =>
            {
                ImmutableArray<Inline> inlines = Assert.IsType<Paragraph>(b).Inlines;
                Assert.Equal("Present: ", Assert.IsType<TextRun>(inlines[0]).Text);
                Assert.Equal(("Kari", FontWeight.Bold), (((TextRun)inlines[1]).Text, ((TextRun)inlines[1]).Style!.Weight!.Value));
                Assert.Equal(("Ola", FontStyle.Italic), (((TextRun)inlines[3]).Text, ((TextRun)inlines[3]).Style!.FontStyle!.Value));
                TextStyle both = ((TextRun)inlines[5]).Style!;
                Assert.Equal((FontWeight.Bold, FontStyle.Italic), (both.Weight!.Value, both.FontStyle!.Value));
                Assert.Equal(".\nSecond line".Replace("\n", " ", StringComparison.Ordinal), ((TextRun)inlines[6]).Text); // A soft break is a space.
                Assert.IsType<LineBreak>(inlines[7]);
            },
            b => Assert.Equal(3, Assert.IsType<Heading>(b).Level));
    }

    [Fact]
    public void ListsKeepTheirMarkerStartNestingAndTaskBoxes()
    {
        MarkdownResult result = MarkdownDocument.Parse("3. Third\n4. Fourth\n   - nested\n\n- [x] done\n- [ ] open\n");

        PdfList ordered = Assert.IsType<PdfList>(result.Document.Content[0]);
        Assert.Equal((ListMarker.Number, 3, 2), (ordered.Marker, ordered.Start, ordered.Items.Length));
        Assert.IsType<PdfList>(ordered.Items[1].Content[1]);
        PdfList tasks = Assert.IsType<PdfList>(result.Document.Content[1]);
        Assert.Equal(ListMarker.Bullet, tasks.Marker);
        Assert.Equal("[x] done", Text(tasks.Items[0].Content[0]));
        Assert.Equal("[ ] open", Text(tasks.Items[1].Content[0]));
    }

    [Fact]
    public void PipeTablesBecomeTablesWithHeadersAndAlignment()
    {
        MarkdownResult result = MarkdownDocument.Parse("| Item | Amount |\n|:-----|-------:|\n| Consulting | 9 375,00 |\n| Travel |\n");

        PdfTable table = Assert.IsType<PdfTable>(Assert.Single(result.Document.Content));
        Assert.Equal(["Item", "Amount"], table.Columns.Select(c => c.Header));
        Assert.Equal([TextAlign.Left, TextAlign.Right], table.Columns.Select(c => c.Align));
        ImmutableArray<ImmutableArray<Cell>> rows = Assert.IsType<InlineTableRows>(table.Rows).Rows;
        Assert.Equal(2, rows.Length);
        Assert.Equal("9 375,00", Assert.IsType<TextRun>(rows[0][1].Inlines[0]).Text);
        Assert.Equal(2, rows[1].Length); // A short row is filled out.
    }

    [Fact]
    public void CodeQuotesRulesAndImagesBecomeTheirBlocks()
    {
        MarkdownResult result = MarkdownDocument.Parse("```\nvar x = 1;\n\tindented\n```\n\n> Quoted\n\n---\n\n![The logo](images/logo.png)\n\n![](rule.png)\n");

        Assert.Empty(result.Diagnostics);
        Paragraph code = Assert.IsType<Paragraph>(result.Document.Content[0]);
        Assert.Equal(("code", "var x = 1;\n    indented"), (code.Style, Text(code)));
        Assert.Equal("quote", Assert.IsType<Paragraph>(result.Document.Content[1]).Style);
        Assert.IsType<Spacer>(result.Document.Content[2]);
        ImageBlock logo = Assert.IsType<ImageBlock>(result.Document.Content[3]);
        Assert.Equal((new SlotImageSource("images/logo.png"), "The logo"), (logo.Source, logo.Alt));
        Assert.True(Assert.IsType<ImageBlock>(result.Document.Content[4]).Decorative);
        Assert.Equal(["images/logo.png", "rule.png"], result.Images);
        Assert.True(StyleSheet.Default.Styles.ContainsKey("code") && StyleSheet.Default.Styles.ContainsKey("quote"));
    }

    [Fact]
    public void LinksThatAPdfCanUseBecomeLinksAndOthersKeepTheirText()
    {
        MarkdownResult result = MarkdownDocument.Parse("See [our site](https://example.com \"Home\"), <post@example.com>, [the appendix](#appendix) and [a file](notes.md).");

        ImmutableArray<Inline> inlines = Assert.IsType<Paragraph>(Assert.Single(result.Document.Content)).Inlines;
        Link[] links = [.. inlines.OfType<Link>()];
        Assert.Equal(["https://example.com", "mailto:post@example.com", "#appendix"], links.Select(l => l.Target));
        Assert.Equal("Home", links[0].Description);
        Assert.Contains("a file", Text(Assert.Single(result.Document.Content)), StringComparison.Ordinal);
        Assert.Equal(("ZPDF-M003", "markdown:1:"), (Assert.Single(result.Diagnostics).Code, Assert.Single(result.Diagnostics).Path[..11]));
    }

    [Fact]
    public void RawHtmlIsDroppedWithAWarningAndFrontMatterSetsTitleAndLanguage()
    {
        MarkdownResult result = MarkdownDocument.Parse("---\ntitle: \"Referat\"\nlang: nb\nauthor: Kari\n---\n# Møte\n\n<div>hidden</div>\n\nText <b>bold</b> ~~gone~~.\n");

        Assert.Equal(("Referat", "nb"), (result.Document.Title, result.Document.Language));
        Assert.Equal(2, result.Document.Content.Length);
        Assert.Equal("Text bold gone.", Text(result.Document.Content[1]));
        Assert.Equal(["ZPDF-M002", "ZPDF-M001", "ZPDF-M001", "ZPDF-M001", "ZPDF-M002"], result.Diagnostics.Select(d => d.Code));
        Assert.Equal("markdown:8:1", result.Diagnostics[1].Path);
        Assert.Equal(("Options", "en"), (MarkdownDocument.Parse("# H", new MarkdownOptions { Title = "Options" }).Document.Title, MarkdownDocument.Parse("text").Document.Language));
        Assert.Equal("Untitled", MarkdownDocument.Parse("text").Document.Title);
    }

    [Theory]
    [InlineData("quotes")]
    [InlineData("lists")]
    [InlineData("links")]
    public void MarkdownNestedPastTheParsersLimitIsAnErrorNotACrash(string nesting)
    {
        string text = nesting switch
        {
            "quotes" => new string('>', 200) + " x",
            "lists" => string.Concat(Enumerable.Range(0, 120).Select(i => new string(' ', i * 2) + "- a\n")),
            _ => new string('[', 300) + "x" + string.Concat(Enumerable.Repeat("](https://u.example)", 300)),
        };

        MarkdownResult result = MarkdownDocument.Parse(text);

        if (nesting == "links")
        {
            Assert.NotEmpty(result.Document.Content); // Markdig reads these without nesting them deeply: no crash, no error.
            return;
        }

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(result.Diagnostics, d => d.Code == "ZPDF-M004").Severity);
        Assert.Empty(result.Document.Content);
    }

    [Fact]
    public async Task AMarkdownDocumentRendersTaggedWithItsImage()
    {
        const string Text = "# Report\n\nIntro with a [link](https://example.com).\n\n![A red square](red.png)\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n- one\n- two\n\n```\ncode\n```\n";
        MarkdownResult result = MarkdownDocument.Parse(Text);
        RenderOptions options = PdfInspect.Options(PdfConformance.PdfUA1);
        byte[] png = TestImages.Png(4, 4, 2, 8, (_, _) => [255, 0, 0]);

        (byte[] file, RenderResult rendered) = await Rendering.RenderTests.RenderAsync(result.Document, new RenderOptions { Fonts = options.Fonts, Conformance = PdfConformance.PdfUA1, Data = PdfData.Empty.WithImage("red.png", png) });

        Assert.DoesNotContain(rendered.Diagnostics, d => d.Severity != DiagnosticSeverity.Info);
        PdfFile.CheckStructure(file);
        Assert.Single(PdfInspect.Elements(file, "Figure"));
        Assert.Single(PdfInspect.Elements(file, "Table"));
        Assert.Single(PdfInspect.Elements(file, "Link"));
    }

    [Fact]
    public async Task TheAgentToolRendersMarkdownWhenTheHostGivesAParser()
    {
        Assert.DoesNotContain(PdfTools.Create(new PdfToolOptions { Render = PdfInspect.Options() }), t => t.Name == "render_markdown");
        AIFunction tool = PdfTools.Create(new PdfToolOptions { Render = PdfInspect.Options(), ParseMarkdown = m => MarkdownDocument.Parse(m).ToParseResult() }).Single(t => t.Name == "render_markdown");

        var rendered = Assert.IsType<JsonElement>(await tool.InvokeAsync(new AIFunctionArguments { ["markdown"] = "# Notes\n\nSome <i>text</i>." }, TestContext.Current.CancellationToken));
        var missing = Assert.IsType<JsonElement>(await tool.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken));

        Assert.True(rendered.GetProperty("rendered").GetBoolean());
        Assert.Equal(1, rendered.GetProperty("pages").GetInt32());
        Assert.Contains(rendered.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("code").GetString() == "ZPDF-M001");
        Assert.False(missing.GetProperty("rendered").GetBoolean());
    }

    private static string Text(Block block) => block switch
    {
        Paragraph p => string.Concat(p.Inlines.Select(Text)),
        _ => "",
    };

    private static string Text(Inline inline) => inline switch
    {
        TextRun run => run.Text,
        Link link => string.Concat(link.Inlines.Select(Text)),
        LineBreak => "\n",
        _ => "",
    };
}
