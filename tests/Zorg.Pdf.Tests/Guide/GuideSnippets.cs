using System;
using System.IO;
using System.Threading.Tasks;
using Zorg.Pdf.Fonts;

namespace Zorg.Pdf.Tests.Guide;

/// <summary>
/// The C# snippets in <c>docs/guide</c>, compiled. They aren't run: they name files that don't exist here.
/// <see cref="GuideTests"/> checks that every C# block in the guide appears in this file, line for line.
/// </summary>
internal static class GuideSnippets
{
    // getting-started.md
    public static async Task GettingStartedAsync()
    {
        FontCollection fonts = FontCollection.FromFolder("fonts").WithDefault("Inter");

        var document = new PdfDocument("Hello")
        {
            Content = [new Heading("Hello, PDF"), new Paragraph("Tagged, accessible and the same on every machine.")],
        };
        await using FileStream output = File.Create("hello.pdf");
        RenderResult result = await document.RenderAsync(output, new RenderOptions { Fonts = fonts });
        Console.WriteLine($"{result.PageCount} pages, {result.BytesWritten} bytes");

        PdfDocument fromJson = PdfDocument.ParseJson(await File.ReadAllTextAsync("hello.json"));
        foreach (PdfDiagnostic problem in fromJson.Validate().Diagnostics)
        {
            Console.WriteLine($"{problem.Code} at {problem.Path}: {problem.Message}");
        }
    }

    // design.md
    public static (StyleSheet Styles, TextBox Cover, Table Table) Design()
    {
        var styles = StyleSheet.Default with
        {
            Styles = StyleSheet.Default.Styles
                .SetItem("heading1", new TextStyle { Size = Length.Pt(22), Weight = FontWeight.Bold, Color = Color.Parse("#0F2A3D") })
                .SetItem("coverTitle", new TextStyle { Size = Length.Pt(40), Weight = FontWeight.Bold, Color = Color.White }),
        };

        var cover = new TextBox([new Paragraph("Fjordlys AS") { Style = "coverTitle" }])
        {
            X = Length.Zero,
            Y = Length.Zero,
            Width = Length.Mm(210),
            Height = Length.Mm(110),
            Padding = new Thickness(Length.Mm(34), Length.Mm(20), Length.Zero, Length.Mm(20)),
            Fill = Color.Parse("#0F2A3D"),
        };

        var table = new Table([new Column("Measure"), new Column("2026") { Align = TextAlign.Right }])
        {
            Rows = TableRows.Inline([["Lamps taken back", "11 200"], ["Parts reused", "93 %"]]),
            TableStyle = new TableStyle { HeaderFill = Color.Parse("#0F2A3D"), StripeFill = Color.Parse("#F4F6F9"), CellPadding = Length.Pt(4.5) },
        };

        return (styles, cover, table);
    }
}
