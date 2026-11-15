using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Text boxes: paragraphs at a fixed place, shrunk or cut to fit, tagged as a Div of Ps.</summary>
public sealed partial class TextBoxTests
{
    private static readonly ImmutableArray<Block> Address = [new Paragraph("Blåbær & Syltetøy AB"), new Paragraph("Att. Sven Svensson"), new Paragraph("Kungsgatan 12"), new Paragraph("111 43 Stockholm")];

    [Fact]
    public void ATextBoxRoundTripsThroughJsonAndPresetsGiveItsPlace()
    {
        PdfDocument document = PdfDocument.ParseJson("""{"title":"x","content":[{"type":"textBox","preset":"din5008B","overflow":"clip","y":"50mm","content":[{"type":"paragraph","inlines":["Kari"]}]}]}""");

        TextBox box = Assert.IsType<TextBox>(document.Content[0]);
        Assert.Equal((TextBoxPreset.Din5008B, TextOverflow.Clip), (box.Preset, box.Overflow));
        (double x, double y, double width, double height, Thickness padding) = box.Resolve()!.Value;
        Assert.Equal(Length.Mm(20).Points, x, 6);
        Assert.Equal(Length.Mm(50).Points, y, 6); // Overrides the preset's 45 mm.
        Assert.Equal((Length.Mm(85).Points, Length.Mm(45).Points), (width, height));
        Assert.Equal(Length.Mm(5), padding.Left);
        Assert.Equal(document.ToJson(), PdfDocument.ParseJson(document.ToJson()).ToJson());
        Assert.True(document.Validate().IsValid);
    }

    [Fact]
    public void TextBoxesAreChecked()
    {
        var document = new PdfDocument("x")
        {
            Content =
            [
                new TextBox(Address),
                new TextBox(Address) { X = Length.Mm(250), Y = Length.Zero, Width = Length.Mm(10), Height = Length.Mm(10) },
                new TextBox(Address) { Preset = TextBoxPreset.Din5008B, X = Length.Mm(150) },
                new TextBox([new Heading("Not here")]) { Preset = TextBoxPreset.Din5008A },
                new TextBox(Address) { Preset = TextBoxPreset.Din5008A, Padding = Thickness.All(Length.Mm(30)) },
                new TextBox([]) { Preset = TextBoxPreset.Din5008A, MinSize = Length.Zero },
            ],
        };

        Assert.Equal(
            ["ZPDF-J024 /content/0", "ZPDF-J024 /content/1/x", "ZPDF-L007 /content/2", "ZPDF-J024 /content/3/content/0", "ZPDF-J024 /content/4/padding", "ZPDF-J024 /content/5/minSize", "ZPDF-J024 /content/5/content"],
            document.Validate().Diagnostics.Select(d => $"{d.Code} {d.Path}"));
    }

    [Fact]
    public async Task AnAddressSitsInTheWindowAndTheFlowCarriesOnBelowItsTop()
    {
        var document = new PdfDocument("Letter")
        {
            Content = [new TextBox(Address) { Preset = TextBoxPreset.Din5008B }, new Spacer(Length.Mm(80)), new Paragraph("Dear Sven,")],
        };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, PdfInspect.Options(PdfConformance.PdfUA1));

        Assert.Empty(result.Diagnostics);
        PdfFile.CheckStructure(file);
        string page = PdfInspect.PageContents(file)[0];
        (double x, double y)[] positions = [.. Positions(page)];
        double top = Length.Mm(297).Points - Length.Mm(45).Points;
        Assert.Equal(Length.Mm(25).Points, positions[0].x, 2);                  // Text starts at the 25 mm margin.
        Assert.InRange(top - positions[0].y, 7, 10);                            // First baseline one ascent below the box's top.
        Assert.Equal([12, 12, 12], positions.Take(4).Zip(positions.Skip(1)).Take(3).Select(p => Math.Round(p.First.y - p.Second.y, 1))); // No space between lines.
        Assert.True(positions[4].y < top - Length.Mm(45).Points);               // The flow went on below the spacer, not the box.
        Assert.Single(PdfInspect.Elements(file, "Div"));
        Assert.Equal(5, PdfInspect.Elements(file, "P").Count);
    }

    [Fact]
    public async Task ANorwegianWindowAddressSitsOnTheGridAndPostensRulesAreChecked()
    {
        static PdfDocument Letter(params Block[] address) => new("Brev") { Language = "nb", Content = [new TextBox([.. address]) { Preset = TextBoxPreset.NorwegianWindow }, new Spacer(Length.Mm(70)), new Paragraph("Hei Kari,")] };
        RenderOptions options = PdfInspect.Options();

        (byte[] file, RenderResult good) = await RenderTests.RenderAsync(Letter(new Paragraph("Kari Nordmann"), new Paragraph("Eksempelveien 12 B"), new Paragraph("0371 OSLO")), options);
        (_, RenderResult oneLine) = await RenderTests.RenderAsync(Letter(new Paragraph("Kari Nordmann")), options);
        (_, RenderResult blank) = await RenderTests.RenderAsync(Letter(new Paragraph("Kari Nordmann"), new Paragraph(""), new Paragraph("0371 OSLO")), options);
        (_, RenderResult bold) = await RenderTests.RenderAsync(Letter(new Paragraph([new TextRun("Kari Nordmann") { Style = new TextStyle { Weight = FontWeight.Bold } }]), new Paragraph("0371 OSLO")), options);

        (double x, double y) first = Positions(PdfInspect.PageContents(file)[0]).First();
        Assert.Equal(Length.Mm(21.27).Points, first.x, 2);                                   // The grid's first tab.
        Assert.InRange(Length.Mm(297).Points - Length.Mm(40).Points - first.y, 7, 10);     // From 40 mm down, Posten's least.
        Assert.DoesNotContain(good.Diagnostics, d => d.Code == "ZPDF-L008");
        Assert.Contains(oneLine.Diagnostics, d => d.Code == "ZPDF-L008" && d.Severity == DiagnosticSeverity.Warning);
        Assert.Contains(blank.Diagnostics, d => d.Code == "ZPDF-L008" && d.Message.Contains("blank", StringComparison.Ordinal));
        Assert.Contains(bold.Diagnostics, d => d.Code == "ZPDF-L009" && d.Severity == DiagnosticSeverity.Info);
        Assert.Equal(TextBoxPreset.NorwegianWindow, Assert.IsType<TextBox>(PdfDocument.ParseJson("""{"title":"x","content":[{"type":"textBox","preset":"norwegianWindow","content":[]}]}""").Content[0]).Preset);
    }

    [Fact]
    public async Task LongTextIsMadeSmallerToFitAndSaysSo()
    {
        var box = new TextBox([.. Enumerable.Range(1, 14).Select(i => (Block)new Paragraph($"Line {i}"))]) { Preset = TextBoxPreset.Din5008B };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(new PdfDocument("x") { Content = [box] }, PdfInspect.Options());

        PdfDiagnostic shrunk = Assert.Single(result.Diagnostics);
        Assert.Equal(("ZPDF-L005", DiagnosticSeverity.Info), (shrunk.Code, shrunk.Severity));
        double size = Sizes(PdfInspect.PageContents(file)[0]).Single();
        Assert.InRange(size, 7, 7.75);                                           // 14 lines × 1.2 × size ≤ 45 mm.
        Assert.Equal(0, size * 4 % 1, 6);                                       // In 0.25 pt steps.
        Assert.Equal(14, Positions(PdfInspect.PageContents(file)[0]).Count());     // Every line kept.
    }

    [Fact]
    public async Task TextThatStillDoesntFitIsCutAtAWholeLine()
    {
        ImmutableArray<Block> lines = [.. Enumerable.Range(1, 14).Select(i => (Block)new Paragraph($"Line {i}"))];
        var clip = new TextBox(lines) { Preset = TextBoxPreset.Din5008B, Overflow = TextOverflow.Clip };
        var shrink = new TextBox(lines) { Preset = TextBoxPreset.Din5008B, MinSize = Length.Pt(9) };
        var visible = new TextBox(lines) { Preset = TextBoxPreset.Din5008B, Overflow = TextOverflow.Visible };

        (byte[] clipped, RenderResult clipResult) = await RenderTests.RenderAsync(new PdfDocument("x") { Content = [clip] }, PdfInspect.Options());
        (_, RenderResult shrinkResult) = await RenderTests.RenderAsync(new PdfDocument("x") { Content = [shrink] }, PdfInspect.Options());
        (byte[] shown, RenderResult visibleResult) = await RenderTests.RenderAsync(new PdfDocument("x") { Content = [visible] }, PdfInspect.Options());

        PdfDiagnostic cut = Assert.Single(clipResult.Diagnostics);
        Assert.Equal(("ZPDF-L006", "/content/0/content/10"), (cut.Code, cut.Path)); // 10 lines of 12 pt fit in 127.6 pt.
        Assert.Contains("4 lines", cut.Message, StringComparison.Ordinal);
        Assert.Equal(10, Positions(PdfInspect.PageContents(clipped)[0]).Count());
        Assert.Equal(["ZPDF-L005", "ZPDF-L006"], shrinkResult.Diagnostics.Select(d => d.Code));
        Assert.Contains("even at 9 pt", shrinkResult.Diagnostics[1].Message, StringComparison.Ordinal);
        Assert.Equal("ZPDF-L006", Assert.Single(visibleResult.Diagnostics).Code);
        Assert.Equal(14, Positions(PdfInspect.PageContents(shown)[0]).Count());
    }

    [Fact]
    public async Task ABorderIsAnArtifactAndTemplatesFillTheBox()
    {
        var template = new PdfDocument("x")
        {
            Content =
            [
                new TextBox([new Paragraph([new Bind("customer.name")]), new Repeat("customer.lines", [new Paragraph([new Bind("this")])])])
                {
                    X = Length.Mm(20), Y = Length.Mm(45), Width = Length.Mm(85), Height = Length.Mm(30), BorderColor = Color.Gray(0x80),
                },
            ],
        };
        using JsonDocument model = JsonDocument.Parse("""{ "customer": { "name": "Blåbær AB", "lines": ["Kungsgatan 12", "111 43 Stockholm"] } }""");

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(template, new RenderOptions { Fonts = PdfInspect.Options().Fonts, Data = PdfData.Empty.WithModel(model.RootElement), Conformance = PdfConformance.PdfUA1 });

        Assert.Empty(result.Diagnostics);
        string page = PdfInspect.PageContents(file)[0];
        Assert.Matches(@"/Artifact <</Type /Layout>> BDC\nq\n0.5 w\n[\d. ]+RG\n[\d. ]+re\nS\nQ\nEMC", page);
        Assert.Equal(3, Positions(page).Count()); // The name and the two repeated lines.
        Assert.Equal(3, PdfInspect.Elements(file, "P").Count);
    }

    private static IEnumerable<(double X, double Y)> Positions(string content) =>
        PositionPattern().Matches(content).Select(m => (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));

    private static IEnumerable<double> Sizes(string content) =>
        SizePattern().Matches(content).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct();

    [GeneratedRegex(@"1 0 0 1 ([\d.]+) ([\d.]+) Tm")]
    private static partial Regex PositionPattern();

    [GeneratedRegex(@" ([\d.]+) Tf")]
    private static partial Regex SizePattern();
}
