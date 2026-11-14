using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Text in another language than the document's: /Lang on the block's element, or a Span with /Lang for a run.</summary>
public sealed class LanguageTests
{
    [Fact]
    public async Task ARunInAnotherLanguageIsASpanAndABlockGetsLangOnItsElement()
    {
        var document = new PdfDocument("x")
        {
            Language = "en",
            Styles = new StyleSheet { Styles = StyleSheet.Default.Styles.SetItem("german", new TextStyle { Language = "de" }) },
            Content =
            [
                new Paragraph(["The word ", new TextRun("Fernweh") { Style = new TextStyle { Language = "de" } }, " and ", new TextRun("hygge") { Style = new TextStyle { Language = "EN" } }, "."]),
                new Paragraph("Ein ganzer Absatz auf Deutsch.") { Style = "german" },
            ],
        };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, PdfInspect.Options(PdfConformance.PdfUA1));

        Assert.Empty(result.Diagnostics);
        PdfFile.CheckStructure(file);
        string content = PdfInspect.PageContents(file)[0];
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(content, @"/Span <</Lang \(de\)>> BDC\n").Cast<System.Text.RegularExpressions.Match>()); // Not for "EN": the document's.
        Assert.Contains("/S /P /P", PdfInspect.Text(file), StringComparison.Ordinal);
        Assert.Matches(@"/S /P /P \d+ 0 R /Lang \(de\)", PdfInspect.Text(file));
    }

    [Fact]
    public void LanguagesMustBeTags()
    {
        var document = new PdfDocument("x") { Content = [new Paragraph([new TextRun("x") { Style = new TextStyle { Language = "German!" } }])] };

        PdfDiagnostic problem = Assert.Single(document.Validate().Diagnostics);
        Assert.Equal(("ZPDF-A002", "/content/0/inlines/0/style/language"), (problem.Code, problem.Path));
    }
}
