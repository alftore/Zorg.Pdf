using System;
using Xunit;

namespace Zorg.Pdf.Tests.Model;

/// <summary>Every parse failure is a diagnostic with a code, a JSON pointer and a message in the document's terms.</summary>
public sealed class ParseErrorTests
{
    [Theory]
    [InlineData("""{"title":"t","content":[{"type":"paragraph","inlines":["x"],"colour":"red"}]}""", "ZPDF-J002", "/content/0/colour", "colour")]
    [InlineData("""{"title":"t","pages":{}}""", "ZPDF-J002", "/pages", "pages")]
    [InlineData("""{"title":"t","content":[{"type":"paragraph","inlines":[{"txt":"x"}]}]}""", "ZPDF-J002", "/content/0/inlines/0", "txt")]
    [InlineData("""{"title":"t","page":{"margin":{"middle":"1cm"}}}""", "ZPDF-J002", "/page/margin", "middle")]
    [InlineData("""{"language":"en"}""", "ZPDF-J004", "", "title")]
    [InlineData("""{"title":"t","content":[{"type":"paragraph"}]}""", "ZPDF-J004", "/content/0", "inlines")]
    [InlineData("""{"title":"t","content":[{"type":"table","columns":[{"width":"*"}]}]}""", "ZPDF-J004", "/content/0/columns/0", "header")]
    [InlineData("""{"title":"t","content":[{"type":"chart"}]}""", "ZPDF-J005", "/content/0", "chart")]
    [InlineData("""{"title":"t","content":[{"inlines":["x"]}]}""", "ZPDF-J005", "/content/0", "type")]
    [InlineData("""{"title":"t",""", "ZPDF-J003", "", "valid JSON")]
    [InlineData("""{"title":"t","content":[{"type":"heading","level":"one","inlines":["x"]}]}""", "ZPDF-J003", "/content/0/level", "")]
    [InlineData("""{"title":"t","page":{"margin":"2 parsecs"}}""", "ZPDF-J003", "/page/margin", "length")]
    [InlineData("""{"title":"t","page":{"size":"B5"}}""", "ZPDF-J003", "/page/size", "B5")]
    [InlineData("""{"title":"t","styles":{"styles":{"x":{"color":"red"}}}}""", "ZPDF-J003", "/styles/styles/x/color", "#RRGGBB")]
    [InlineData("""{"title":"t","content":[{"type":"paragraph","align":"centre","inlines":["x"]}]}""", "ZPDF-J003", "/content/0/align", "")]
    [InlineData("""{"title":"t","content":[{"type":"paragraph","inlines":[{"text":"x","field":"pageNumber"}]}]}""", "ZPDF-J003", "/content/0/inlines/0", "exactly one")]
    [InlineData("""{"title":null}""", "ZPDF-J003", "/title", "")]
    [InlineData("""{"title":"t","content":[{"type":"paragraph","inlines":["a",{"text":"b","style":{"color":"red"}}]}]}""", "ZPDF-J003", "/content/0/inlines/1/style/color", "#RRGGBB")]
    [InlineData("""{"title":"t","content":[{"type":"paragraph","inlines":["a",{"text":"b","style":{"weight":"heavy"}}]}]}""", "ZPDF-J003", "/content/0/inlines/1/style/weight", "")]
    [InlineData("""{"title":"t","content":[{"type":"table","columns":[{"header":"A"}],"rows":[["x"],[{"inlines":[{"field":"today"}]}]]}]}""", "ZPDF-J003", "/content/0/rows/1/0/inlines/0/field", "")]
    [InlineData("""{"title":"t","content":[{"type":"table","columns":[{"header":"A"}],"rows":[["x"],[{"text":"y"}]]}]}""", "ZPDF-J002", "/content/0/rows/1/0", "text")]
    [InlineData("""{"title":"t","content":[{"type":"table","columns":[{"header":"A"}],"rows":{"$data":"lines","limit":5}}]}""", "ZPDF-J002", "/content/0/rows", "limit")]
    [InlineData("""{"title":"t","content":[{"type":"image","source":{"path":"/etc/passwd"},"alt":"x"}]}""", "ZPDF-J002", "/content/0/source", "path")]
    [InlineData("""{"$schema":"https://alftore.github.io/Zorg.Pdf/schema/document-9.0.schema.json","title":"t"}""", "ZPDF-J001", "/$schema", "document-9.0")]
    public void ReportsCodePathAndCause(string json, string code, string path, string mention)
    {
        DocumentParseResult result = PdfDocument.TryParseJson(json);

        Assert.False(result.IsSuccess);
        PdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(path, diagnostic.Path);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(mention, diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", diagnostic.Message, StringComparison.Ordinal); // No .NET type names for agents.
        Assert.DoesNotContain("Zorg.Pdf.", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseJsonThrowsWithTheSameDiagnostics()
    {
        PdfDocumentException error = Assert.Throws<PdfDocumentException>(() => PdfDocument.ParseJson("""{"title":"t","x":1}"""));
        Assert.Equal("ZPDF-J002", Assert.Single(error.Diagnostics).Code);
        Assert.Contains("error ZPDF-J002 at /x", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesDocumentsOverTheSizeLimit()
    {
        byte[] huge = new byte[(16 * 1024 * 1024) + 1];
        Assert.Equal("ZPDF-J006", Assert.Single(PdfDocument.TryParseJson(huge).Diagnostics).Code);
    }

    [Fact]
    public void RefusesNestingDeeperThanTheLimit()
    {
        // Deep enough to pass the reader's limit before any mapping error: arrays where blocks belong.
        string deep = """{"title":"t","content":""" + new string('[', 100) + new string(']', 100) + "}";
        Assert.Equal("ZPDF-J003", Assert.Single(PdfDocument.TryParseJson(deep).Diagnostics).Code);
    }

    [Theory]
    [InlineData("$.content[2].inlines[0]", "/content/2/inlines/0")]
    [InlineData("$['a/b'].c", "/a~1b/c")]
    [InlineData("$['x~y']", "/x~0y")]
    [InlineData("$", "")]
    public void ConvertsSerializerPathsToJsonPointers(string path, string expected) =>
        Assert.Equal(expected, Zorg.Pdf.Json.DocumentJson.Pointer(path));
}
