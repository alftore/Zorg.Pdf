using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Tests.Model;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>
/// Every example in <c>docs/examples</c> renders under PDF/UA-1 with the data in <c>docs/examples/data</c>, with no
/// warnings, the same bytes sync and async. The Linux gate also runs them through veraPDF, and CI hashes them on
/// Windows and Linux.
/// </summary>
public sealed class ExampleRenderTests
{
    public static TheoryData<string> Examples() =>
        [.. Directory.GetFiles(RepoFiles.Path("docs", "examples"), "*.json").Select(path => Path.GetFileNameWithoutExtension(path)!).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Examples))]
    public async Task AnExampleRendersAsPdfUAWithoutWarnings(string name)
    {
        PdfDocument document = PdfDocument.ParseJson(await File.ReadAllTextAsync(RepoFiles.Path("docs", "examples", name + ".json"), TestContext.Current.CancellationToken));
        string dataPath = RepoFiles.Path("docs", "examples", "data", name + ".json");
        var options = new RenderOptions
        {
            Fonts = PdfInspect.Options().Fonts,
            Conformance = PdfConformance.PdfUA1,
            Data = File.Exists(dataPath) ? PdfData.ParseJson(await File.ReadAllTextAsync(dataPath, TestContext.Current.CancellationToken)) : PdfData.Empty,
        };

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, options);

        Assert.False(result.HasWarnings, string.Join(Environment.NewLine, result.Diagnostics));
        PdfFile.CheckStructure(file);
        Assert.Equal(file, RenderSync(document, options));
    }

    [Fact]
    public void DataJsonProblemsComeBackWithPointers()
    {
        PdfDocumentException error = Assert.Throws<PdfDocumentException>(
            () => PdfData.ParseJson("""{ "lines": [["a", { "inlines": ["b"], "colour": "red" }]], "logo": { "$data": "x" }, "n": 3 }"""));

        Assert.Equal(["ZPDF-J002 /lines/0/1", "ZPDF-J003 /logo", "ZPDF-J003 /n"], error.Diagnostics.Select(d => $"{d.Code} {d.Path}"));
    }

    [Fact]
    public void DataJsonHoldsRowsAndImages()
    {
        PdfData data = PdfData.ParseJson("""{ "lines": [["1", "Consulting"], ["2", { "inlines": [{ "text": "Bold", "style": { "weight": "bold" } }] }]], "logo": { "base64": "AQID" } }""");

        Assert.False(data.Rows["lines"].IsAsync);
        Assert.Equal([1, 2, 3], data.Images["logo"].Bytes!.Value.ToArray());
    }

    private static byte[] RenderSync(PdfDocument document, RenderOptions options)
    {
        using var output = new MemoryStream();
        document.Render(output, options);
        return output.ToArray();
    }
}
