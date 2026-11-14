using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Xunit;
using Zorg.Pdf.Extensions.AI;
using Zorg.Pdf.Tests.Model;
using Zorg.Pdf.Tests.Rendering;

namespace Zorg.Pdf.Tests.Extensions;

/// <summary>The agent tools, called the way a chat client calls them: JSON arguments in, JSON out.</summary>
public sealed class PdfToolsTests
{
    [Fact]
    public void TheToolsHaveNamesDescriptionsAndParameterSchemas()
    {
        IReadOnlyList<AIFunction> tools = PdfTools.Create(new PdfToolOptions { Render = PdfInspect.Options() });

        Assert.Equal(["get_document_schema", "validate_document", "render_document"], tools.Select(t => t.Name));
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        Assert.Equal("object", tools[2].JsonSchema.GetProperty("type").GetString());
        Assert.Equal("document", tools[2].JsonSchema.GetProperty("required")[0].GetString());
    }

    [Fact]
    public async Task TheSchemaToolReturnsTheSchemaAGuideAndAValidExample()
    {
        JsonElement result = await Call("get_document_schema", []);

        Assert.Equal(PdfJsonSchema.Id, result.GetProperty("schema").GetProperty("$id").GetString());
        Assert.Contains("validate_document", result.GetProperty("guide").GetString(), StringComparison.Ordinal);
        Assert.True(PdfDocument.ParseJson(result.GetProperty("example").GetRawText()).Validate().IsValid);
    }

    [Fact]
    public async Task ValidateReturnsEveryProblemWithAPointer()
    {
        JsonElement result = await Call("validate_document", new() { ["document"] = Json("""{ "title": "x", "content": [{ "type": "heading", "level": 3, "inlines": ["x"], "colour": "red" }] }""") });

        Assert.False(result.GetProperty("valid").GetBoolean());
        JsonElement problem = Assert.Single(result.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal(("ZPDF-J002", "error", "/content/0/colour"), (problem.GetProperty("code").GetString(), problem.GetProperty("severity").GetString(), problem.GetProperty("path").GetString()));

        JsonElement fixedUp = await Call("validate_document", new() { ["document"] = Json("""{ "title": "x", "content": [{ "type": "heading", "level": 3, "inlines": ["x"] }] }""") });
        Assert.True(fixedUp.GetProperty("valid").GetBoolean()); // A skipped level is a warning here.
        Assert.Equal("ZPDF-A004", fixedUp.GetProperty("diagnostics")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task RenderRendersAnExampleWithItsDataAndSavesIt()
    {
        string document = await File.ReadAllTextAsync(RepoFiles.Path("docs", "examples", "invoice.json"), TestContext.Current.CancellationToken);
        string data = await File.ReadAllTextAsync(RepoFiles.Path("docs", "examples", "data", "invoice.json"), TestContext.Current.CancellationToken);
        RenderedPdf? saved = null;
        var options = new PdfToolOptions
        {
            Render = PdfInspect.Options(PdfConformance.PdfUA1),
            SaveAsync = (pdf, _) =>
            {
                saved = pdf;
                return ValueTask.FromResult("invoice-2026-0042.pdf");
            },
        };

        // Documents arrive as JSON objects, or as strings holding JSON from clients that send them that way.
        JsonElement result = await Call("render_document", new() { ["document"] = Json(document), ["data"] = data }, options);

        Assert.True(result.GetProperty("rendered").GetBoolean(), result.GetRawText());
        Assert.Equal("invoice-2026-0042.pdf", result.GetProperty("file").GetString());
        Assert.Equal(saved!.PageCount, result.GetProperty("pages").GetInt32());
        Assert.Equal("Invoice 2026-0042", saved.Title);
        Assert.StartsWith("%PDF-1.7", System.Text.Encoding.ASCII.GetString(saved.Bytes.Span[..8]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderReportsWhatStoppedIt()
    {
        JsonElement result = await Call("render_document", new()
        {
            ["document"] = Json("""{ "title": "x", "content": [{ "type": "table", "columns": [{ "header": "A" }], "rows": { "$data": "lines" } }] }"""),
            ["data"] = Json("""{ "lines": [["a", { "inlines": ["b"], "colour": "red" }]] }"""),
        });

        Assert.False(result.GetProperty("rendered").GetBoolean());
        Assert.Equal(["ZPDF-J002 data:/lines/0/1"], result.GetProperty("diagnostics").EnumerateArray().Select(d => $"{d.GetProperty("code").GetString()} {d.GetProperty("path").GetString()}"));

        JsonElement missing = await Call("render_document", new() { ["document"] = Json("""{ "title": "x", "content": [{ "type": "table", "columns": [{ "header": "A" }], "rows": { "$data": "lines" } }] }""") });
        Assert.Contains("ZPDF-D001", missing.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("code").GetString()));

        JsonElement noDocument = await Call("render_document", []);
        Assert.Equal("ZPDF-J004", noDocument.GetProperty("diagnostics")[0].GetProperty("code").GetString());
    }

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> Call(string name, AIFunctionArguments arguments, PdfToolOptions? options = null)
    {
        AIFunction tool = PdfTools.Create(options ?? new PdfToolOptions { Render = PdfInspect.Options() }).Single(t => t.Name == name);
        object? result = await tool.InvokeAsync(arguments, TestContext.Current.CancellationToken);
        return Assert.IsType<JsonElement>(result);
    }
}
