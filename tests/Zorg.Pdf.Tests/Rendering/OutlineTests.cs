using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Bookmarks from headings, nested by level, and named destinations for block ids.</summary>
public sealed partial class OutlineTests
{
    private static readonly PdfDocument Report = new("Report")
    {
        Content =
        [
            new Heading("Summary"),
            new Heading("Sales") { Level = 2 },
            new Heading("Costs") { Level = 2, Id = "costs" },
            new PageBreak(),
            new Heading(["Appendix", LineBreak.Value, "Tables"]) { Id = "appendix" },
            new Heading("Detail") { Level = 3 }, // Skips level 2: nests under Appendix.
            new Heading("Too deep") { Level = 4 },
            new Paragraph("Text.") { Id = "text.1" },
        ],
    };

    [Fact]
    public async Task HeadingsBecomeNestedBookmarksThatOpenOnTheirPage()
    {
        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(Report, PdfInspect.Options()); // Not PDF/UA: Detail skips a level on purpose.

        Assert.Contains(result.Diagnostics, d => d.Code == "ZPDF-A004" && d.Path == "/content/5/level"); // And F003: headings are bold, the test font regular.
        PdfFile.CheckStructure(file);
        string text = PdfInspect.Text(file);
        Assert.Contains("/Outlines ", text, StringComparison.Ordinal);
        Assert.Contains("/PageMode /UseOutlines", text, StringComparison.Ordinal);

        Dictionary<string, string> items = PdfReader.Read(file).Values
            .Where(o => o.Value.Contains("/Title", StringComparison.Ordinal) && o.Value.Contains("/Dest [", StringComparison.Ordinal))
            .ToDictionary(o => TitlePattern().Match(o.Value).Groups[1].Value, o => o.Value);
        Assert.Equal(["Appendix Tables", "Costs", "Detail", "Sales", "Summary"], items.Keys.Order(StringComparer.Ordinal));
        Assert.Matches(@"/Count 2\b", items["Summary"]);    // Sales and Costs, open.
        Assert.Matches(@"/Count 1\b", items["Appendix Tables"]);
        Assert.DoesNotContain("/Count", items["Sales"], StringComparison.Ordinal);
        Assert.Contains("/Next", items["Summary"], StringComparison.Ordinal);  // Summary, then Appendix, at the top level.
        Assert.Matches(@"/Type /Outlines /First \d+ 0 R /Last \d+ 0 R /Count 5", text);

        // Appendix is on the second page, at the top of its text area.
        IReadOnlyList<string> pages = PageReferences(text);
        Match appendix = DestPattern().Match(items["Appendix Tables"]);
        Assert.Equal(pages[1], appendix.Groups[1].Value);
        Assert.Equal(Length.Mm(297).Points - Length.Cm(2).Points, double.Parse(appendix.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 1);
    }

    [Fact]
    public async Task IdsBecomeNamedDestinationsSortedByName()
    {
        (byte[] file, _) = await RenderTests.RenderAsync(Report, PdfInspect.Options());

        Match names = Regex.Match(PdfInspect.Text(file), @"/Names <</Dests <</Names \[\(appendix\) \[(\d+ 0 R) /XYZ null [\d.]+ null\] \(costs\) \[(\d+ 0 R) /XYZ null [\d.]+ null\] \(text\.1\) \[");
        Assert.True(names.Success);
        IReadOnlyList<string> pages = PageReferences(PdfInspect.Text(file));
        Assert.Equal((pages[1], pages[0]), (names.Groups[1].Value, names.Groups[2].Value));
    }

    [Fact]
    public async Task OutlineLevelsLimitsOrTurnsOffTheBookmarks()
    {
        (byte[] shallow, _) = await RenderTests.RenderAsync(Report with { OutlineLevels = 1 }, PdfInspect.Options());
        (byte[] none, _) = await RenderTests.RenderAsync(Report with { OutlineLevels = 0 }, PdfInspect.Options());

        Assert.Equal(2, PdfReader.Read(shallow).Values.Count(o => o.Value.Contains("/Dest [", StringComparison.Ordinal)));
        Assert.DoesNotContain("/Outlines", PdfInspect.Text(none), StringComparison.Ordinal);
    }

    [Fact]
    public void IdsAndOutlineLevelsRoundTripAndAreChecked()
    {
        PdfDocument parsed = PdfDocument.ParseJson("""{"title":"x","outlineLevels":2,"content":[{"type":"heading","id":"intro","inlines":["Intro"]}]}""");
        Assert.Equal(("intro", 2), (parsed.Content[0].Id, parsed.OutlineLevels));
        Assert.Equal(parsed.ToJson(), PdfDocument.ParseJson(parsed.ToJson()).ToJson());

        var document = new PdfDocument("x")
        {
            OutlineLevels = 9,
            Content =
            [
                new Paragraph("a") { Id = "same" },
                new Paragraph("b") { Id = "same" },
                new Paragraph("c") { Id = "has space" },
                new List([new ListItem([new Paragraph("d") { Id = "in-list" }])]),
                new Repeat("rows", [new Paragraph("e") { Id = "repeated" }]),
                new Conditional("flag", [new Paragraph("f") { Id = "shown" }]),
            ],
        };

        Assert.Equal(
            ["ZPDF-J026 /outlineLevels", "ZPDF-J025 /content/1/id", "ZPDF-J025 /content/2/id", "ZPDF-J025 /content/3/items/0/content/0/id", "ZPDF-J025 /content/4/content/0/id"],
            document.Validate().Diagnostics.Select(d => $"{d.Code} {d.Path}"));
    }

    /// <summary>The page objects' references, in page order, from the page tree's Kids.</summary>
    private static List<string> PageReferences(string text) =>
        [.. Regex.Matches(Regex.Match(text, @"/Type /Pages /Kids \[([^\]]*)\]").Groups[1].Value, @"\d+ 0 R").Select(m => m.Value)];

    [GeneratedRegex(@"/Title \(([^)]*)\)")]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"/Dest \[(\d+ 0 R) /XYZ null ([\d.]+) null\]")]
    private static partial Regex DestPattern();
}
