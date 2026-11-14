using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Fonts;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Errors stop a render before anything is written; warnings come back in the result with a path.</summary>
public sealed class RenderDiagnosticsTests
{
    [Fact]
    public async Task NoFontsIsAnErrorBeforeAnyOutput()
    {
        var output = new MemoryStream();
        PdfDocumentException error = await Assert.ThrowsAsync<PdfDocumentException>(
            () => new PdfDocument("x") { Content = [new Paragraph("Hello")] }.RenderAsync(output, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Contains(error.Diagnostics, d => d.Code == "ZPDF-F001");
        Assert.Contains("FontCollection.Empty.Add", error.Diagnostics.Single(d => d.Code == "ZPDF-F001").Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task EveryMissingGlyphIsAWarningWithItsPathAndDrawnAsNotdef()
    {
        var document = new PdfDocument("x")
        {
            Content = [new Paragraph("fine"), new Paragraph(["ok ", "bad \U000F0000 here"]), new Heading("also \U000F0001")],
        };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document);

        PdfDiagnostic[] missing = [.. result.Diagnostics.Where(d => d.Code == "ZPDF-F002")];
        Assert.Equal(["/content/1/inlines/1", "/content/2/inlines/0"], missing.Select(d => d.Path));
        Assert.All(missing, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Contains("U+F0000", missing[0].Message, StringComparison.Ordinal);
        Assert.Contains("at index 4", missing[0].Message, StringComparison.Ordinal);
        Assert.Contains("<0000> <FFFD>", InflateAll(file), StringComparison.Ordinal); // .notdef copies as U+FFFD.
    }

    /// <summary>Every stream in the file, decoded and joined.</summary>
    private static string InflateAll(byte[] file) => PdfInspect.Text(file);

    [Fact]
    public async Task ControlCharactersAreErrors()
    {
        PdfDocumentException error = await Assert.ThrowsAsync<PdfDocumentException>(
            () => new PdfDocument("x") { Content = [new Paragraph("bell\u0007")] }.RenderAsync(new MemoryStream(), PdfInspect.Options(), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("ZPDF-T002 /content/0/inlines/0", Assert.Single(error.Diagnostics.Select(d => $"{d.Code} {d.Path}")));
    }

    [Fact]
    public async Task ValidationErrorsStopTheRenderToo()
    {
        PdfDocumentException error = await Assert.ThrowsAsync<PdfDocumentException>(
            () => new PdfDocument(" ") { Content = [new Heading("x") { Level = 9 }] }.RenderAsync(new MemoryStream(), PdfInspect.Options(), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(["ZPDF-A001", "ZPDF-J010"], error.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task AnUnknownFamilyOrADamagedFontIsAnError()
    {
        var document = new PdfDocument("x")
        {
            Styles = new StyleSheet { Styles = System.Collections.Immutable.ImmutableDictionary<string, TextStyle>.Empty.Add("fancy", new TextStyle { FontFamily = "Fancy" }) },
            Content = [new Paragraph("a") { Style = "fancy" }],
        };
        PdfDocumentException unknown = await Assert.ThrowsAsync<PdfDocumentException>(
            () => document.RenderAsync(new MemoryStream(), PdfInspect.Options(), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("ZPDF-F004 /content/0/style", Assert.Single(unknown.Diagnostics.Select(d => $"{d.Code} {d.Path}")));
        Assert.Contains("Body", unknown.Diagnostics[0].Message, StringComparison.Ordinal); // It lists the families there are.

        var broken = new RenderOptions { Fonts = FontCollection.Empty.Add(new FontFamily("Body") { Regular = FontSource.FromBytes(new byte[100]) }) };
        PdfDocumentException damaged = await Assert.ThrowsAsync<PdfDocumentException>(
            () => new PdfDocument("x") { Content = [new Paragraph("a")] }.RenderAsync(new MemoryStream(), broken, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("ZPDF-F005", Assert.Single(damaged.Diagnostics).Code);
    }

    [Fact]
    public async Task PartsNotDrawnYetAreWarningsAndTheRestRenders()
    {
        var document = new PdfDocument("x")
        {
            Footer = new PageBand([new Paragraph(["Page ", Field.PageNumber])]),
            Content =
            [
                new Paragraph(["Total ", new TextRun("12") { Style = TextStyle.Bold }, " on page ", Field.PageNumber]),
                new Table([new Column("A")]),
            ],
        };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document);

        Assert.True(file.Length > 0);
        Assert.Equal(
            [
                "Info ZPDF-F003 /content/0/inlines/1/style",
                "Warning ZPDF-R001 /content/0/inlines/3",
            ],
            result.Diagnostics.Select(d => $"{d.Severity} {d.Code} {d.Path}"));
        Assert.True(result.HasWarnings);
    }

    [Fact]
    public async Task AWordTooWideIsAWarningAtItsRun()
    {
        var document = new PdfDocument("x") { Content = [new Paragraph(["Short. ", "Pneumonoultramicroscopicsilicovolcanoconiosis" + new string('x', 60)])] };
        (_, RenderResult result) = await RenderTests.RenderAsync(document);

        PdfDiagnostic warning = Assert.Single(result.Diagnostics);
        Assert.Equal("ZPDF-T001 /content/0/inlines/1", $"{warning.Code} {warning.Path}");
        Assert.Contains("index 0", warning.Message, StringComparison.Ordinal); // Index within the run, not the paragraph.
    }
}
