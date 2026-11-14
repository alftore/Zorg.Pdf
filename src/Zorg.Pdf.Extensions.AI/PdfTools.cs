using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Zorg.Pdf.Extensions.AI;

/// <summary>
/// Tools that let an AI agent write PDF documents: <c>get_document_schema</c> to learn the format,
/// <c>validate_document</c> to check a draft and fix it from diagnostics with JSON pointers, <c>render_document</c>
/// to render it with data, <c>get_capabilities</c> to learn the fonts and limits, and, when the host supplies
/// <see cref="PdfToolOptions.PreviewAsync"/>, <c>render_preview</c> to look at a page as a picture. They take and return JSON, use no reflection (Native AOT works), and
/// never touch the file system: fonts and limits come from the host's <see cref="PdfToolOptions.Render"/>, and the PDF
/// goes wherever <see cref="PdfToolOptions.SaveAsync"/> puts it.
/// </summary>
/// <example>
/// <code>
/// IList&lt;AITool&gt; tools = [.. PdfTools.Create(new PdfToolOptions
/// {
///     Render = new RenderOptions { Fonts = fonts, Conformance = PdfConformance.PdfUA1 },
///     SaveAsync = async (pdf, ct) =&gt; await storage.SaveAsync($"{pdf.Title}.pdf", pdf.Bytes, ct),
/// })];
/// var response = await chatClient.GetResponseAsync(messages, new ChatOptions { Tools = tools }, cancellationToken);
/// </code>
/// </example>
public static class PdfTools
{
    /// <summary>The tools, configured with the host's fonts, limits, storage and previews.</summary>
    /// <param name="options">Fonts, conformance and limits for rendering, where rendered files go, and how pages are drawn.</param>
    /// <returns>
    /// <c>get_document_schema</c>, <c>validate_document</c>, <c>render_document</c> and <c>get_capabilities</c>;
    /// with <see cref="PdfToolOptions.PreviewAsync"/>, <c>render_preview</c> too.
    /// </returns>
    public static IReadOnlyList<AIFunction> Create(PdfToolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Render);
        return options.PreviewAsync is null
            ? [new SchemaTool(), new ValidateTool(), new RenderTool(options), new CapabilitiesTool(options)]
            : [new SchemaTool(), new ValidateTool(), new RenderTool(options), new CapabilitiesTool(options), new PreviewTool(options)];
    }
}

/// <summary>What the tools from <see cref="PdfTools.Create"/> render with, and where the files go.</summary>
public sealed class PdfToolOptions
{
    /// <summary>
    /// Fonts, conformance, limits and anything else the host decides. The model supplies the document and its data;
    /// <see cref="RenderOptions.Data"/> here is replaced by the data in the tool call.
    /// </summary>
    public required RenderOptions Render { get; init; }

    /// <summary>
    /// Stores a rendered PDF and returns what the model should be told about it: a file name, a link or an id.
    /// Without it, <c>render_document</c> reports pages, size and diagnostics but keeps no file.
    /// </summary>
    public Func<RenderedPdf, CancellationToken, ValueTask<string>>? SaveAsync { get; init; }

    /// <summary>
    /// Draws a page of a rendered PDF as PNG: the PDF, the page from 0, and a token. With it, the tools include
    /// <c>render_preview</c>. <c>Zorg.Pdf.Imaging</c> has one: <c>(pdf, page, ct) =&gt; PdfPreview.RenderPagePngAsync(pdf, page, cancellationToken: ct)</c>.
    /// </summary>
    public Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<byte[]>>? PreviewAsync { get; init; }
}

/// <summary>A PDF rendered by <c>render_document</c>.</summary>
/// <param name="Title">The document's title.</param>
/// <param name="Bytes">The file.</param>
/// <param name="PageCount">The number of pages.</param>
public sealed record RenderedPdf(string Title, ReadOnlyMemory<byte> Bytes, int PageCount);

/// <summary>Shared plumbing: a fixed name, description and parameter schema, and JSON in and out.</summary>
internal abstract class PdfTool(string name, string description, string parameters) : AIFunction
{
    private readonly JsonElement _schema = JsonDocument.Parse(parameters).RootElement.Clone();

    public override string Name { get; } = name;

    public override string Description { get; } = description;

    public override JsonElement JsonSchema => _schema;

    /// <summary>A JSON argument as text: an object the model sent, or a string holding JSON.</summary>
    protected static string? Json(AIFunctionArguments arguments, string name) =>
        arguments.TryGetValue(name, out object? value) ? value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement { ValueKind: JsonValueKind.String } text => text.GetString(),
            JsonElement element => element.GetRawText(),
            string text => text,
            JsonDocument document => document.RootElement.GetRawText(),
            _ => throw new ArgumentException($"The argument \"{name}\" must be a JSON object."),
        } : null;

    /// <summary>Builds the result object.</summary>
    protected static JsonElement Result(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    protected static void Diagnostics(Utf8JsonWriter writer, IEnumerable<PdfDiagnostic> diagnostics)
    {
        writer.WriteStartArray("diagnostics");
        foreach (PdfDiagnostic diagnostic in diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("code", diagnostic.Code);
            writer.WriteString("severity", diagnostic.Severity switch { DiagnosticSeverity.Error => "error", DiagnosticSeverity.Warning => "warning", _ => "info" });
            writer.WriteString("path", diagnostic.Path);
            writer.WriteString("message", diagnostic.Message);
            if (diagnostic.Page is { } page)
            {
                writer.WriteNumber("page", page);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>Parses the document and its data, collecting every problem instead of throwing.</summary>
    protected static (PdfDocument? Document, PdfData Data, List<PdfDiagnostic> Problems) Parse(AIFunctionArguments arguments)
    {
        var problems = new List<PdfDiagnostic>();
        string? json = Json(arguments, "document");
        PdfDocument? document = null;
        if (json is null)
        {
            problems.Add(new PdfDiagnostic("ZPDF-J004", DiagnosticSeverity.Error, "The \"document\" argument is missing: pass the document as a JSON object. Call get_document_schema for its format.", ""));
        }
        else
        {
            DocumentParseResult parsed = PdfDocument.TryParseJson(json);
            document = parsed.Document;
            problems.AddRange(parsed.Diagnostics);
            if (document is not null)
            {
                problems.AddRange(document.Validate().Diagnostics);
            }
        }

        PdfData data = PdfData.Empty;
        if (Json(arguments, "data") is { } dataJson)
        {
            try
            {
                data = PdfData.ParseJson(dataJson);
            }
            catch (PdfDocumentException ex)
            {
                foreach (PdfDiagnostic diagnostic in ex.Diagnostics)
                {
                    problems.Add(diagnostic with { Path = "data:" + diagnostic.Path });
                }
            }
        }

        return (document, data, problems);
    }
}

internal sealed class SchemaTool() : PdfTool(
    "get_document_schema",
    "Returns the JSON Schema of a Zorg.Pdf document, a short guide to writing one, and an example. Call it before writing a document.",
    """{ "type": "object", "properties": {} }""")
{
    internal const string Guide = """
        A document is JSON: { "title": "...", "language": "en", "content": [blocks] }, with optional "page", "header", "footer", "watermark" ({ "text": "DRAFT" }) and "styles".
        Blocks have a "type": "heading" (with "level" 1 to 6, never skipping a level), "paragraph", "list", "table", "image", "spacer", "pageBreak" or "textBox".
        A letter's address for a window envelope: { "type": "textBox", "preset": "din5008B", "content": [paragraphs, one per line] }, then a spacer of about 5.5cm so the text below starts under it. Text too long for the box is made smaller; "overflow": "clip" cuts whole lines instead. Other boxes take "x", "y", "width" and "height" from the paper's top-left corner.
        Lists: { "type": "list", "marker": "bullet" or "number", "items": ["text", { "content": [paragraphs and nested lists] }] }.
        Text is "inlines": an array of strings, { "text": "...", "style": { "weight": "bold" } }, { "lineBreak": true } and, in headers and footers, { "field": "pageNumber" } or { "field": "pageCount" }.
        Tables have "columns" ([{ "header": "Amount", "width": "2*", "align": "right" }]) and "rows": an array of rows of cells (strings, or { "inlines": [...], "columnSpan": 2, "rowSpan": 2 }), or { "$data": "slot" } to take them from data.
        Images need "alt" text unless "decorative": true. Their "source" is { "$data": "slot" } (or { "base64": "..." } for small ones).
        Templates: bind model values with { "bind": "customer.name", "format": "N2" } inlines, repeat blocks with { "type": "repeat", "items": "notes", "content": [...] } (inside, bind the item's fields, or "this" and "@number"), and show blocks with { "type": "when", "test": "overdue", "content": [...] }, and make table rows from an array of objects with "rows": { "repeat": "lines", "cells": [{ "bind": "item" }, { "bind": "amount", "format": "N2" }] }. The model is the data object.
        Text in another language than the document's gets a style with "language": { "text": "Fernweh", "style": { "language": "de" } }; screen readers need it.
        Headings become the viewer's bookmarks; give a block "id": "appendix-b" to make it a link target (top-level blocks, unique).
        Links are inlines: { "link": "https://example.com", "inlines": ["our site"], "description": "Example's home page" }, or "mailto:..." or "#appendix-b"; in paragraphs, headings and list items.
        Lengths are numbers in points or strings with a unit: "2cm", "12pt", "5mm".
        Data is a separate JSON object whose properties are slots: { "lines": [["1", "Consulting", "9 375,00"]], "logo": { "base64": "..." } }.
        Unknown properties are errors. validate_document returns every problem with a code and a JSON pointer: fix those and validate again.
        get_capabilities lists the fonts and limits. If render_preview is offered, look at the pages after rendering: text overlapping, cut off or outside its box is easy to see and not reported as an error.
        """;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        ValueTask.FromResult<object?>(Result(writer =>
        {
            writer.WritePropertyName("schema");
            using (JsonDocument schema = JsonDocument.Parse(PdfJsonSchema.Current))
            {
                schema.RootElement.WriteTo(writer);
            }

            writer.WriteString("guide", Guide);
            writer.WritePropertyName("example");
            using JsonDocument example = JsonDocument.Parse(Example.ToJson());
            example.RootElement.WriteTo(writer);
        }));

    private static readonly PdfDocument Example = new("Invoice 2026-0042")
    {
        Footer = new PageBand([new Paragraph(["Page ", Field.PageNumber, " of ", Field.PageCount]) { Align = TextAlign.Right }]),
        Content =
        [
            new Heading("Invoice 2026-0042"),
            new Paragraph(["Payment is due within ", new TextRun("30 days") { Style = TextStyle.Bold }, "."]),
            new Table([new Column("Description") { Width = ColumnWidth.Star(4) }, new Column("Amount") { Align = TextAlign.Right }]) { Rows = TableRows.Bound("lines") },
        ],
    };
}

internal sealed class ValidateTool() : PdfTool(
    "validate_document",
    "Checks a Zorg.Pdf document (and optionally its data) without rendering it. Returns \"valid\" and every problem with a code, a severity and a JSON pointer to fix.",
    """
    {
      "type": "object",
      "properties": {
        "document": { "type": "object", "description": "The document, as JSON. See get_document_schema." },
        "data": { "type": "object", "description": "Optional data for the document's slots: table rows and images." }
      },
      "required": ["document"]
    }
    """)
{
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        (_, _, List<PdfDiagnostic> problems) = Parse(arguments);
        return ValueTask.FromResult<object?>(Result(writer =>
        {
            writer.WriteBoolean("valid", !problems.Exists(d => d.Severity == DiagnosticSeverity.Error));
            Diagnostics(writer, problems);
        }));
    }
}

internal sealed class RenderTool(PdfToolOptions options) : PdfTool(
    "render_document",
    "Renders a Zorg.Pdf document with its data as a tagged PDF. Returns the page count, the size, warnings, and where the file was saved; or the problems that stopped it, each with a JSON pointer.",
    """
    {
      "type": "object",
      "properties": {
        "document": { "type": "object", "description": "The document, as JSON. See get_document_schema." },
        "data": { "type": "object", "description": "Data for the document's slots: table rows and images." }
      },
      "required": ["document"]
    }
    """)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        (PdfDocument? document, byte[]? pdf, RenderResult? result, IReadOnlyList<PdfDiagnostic> problems) = await RenderPdfAsync(options, arguments, cancellationToken).ConfigureAwait(false);
        if (document is null || pdf is null || result is null)
        {
            return Failed(problems);
        }

        string? saved = options.SaveAsync is { } save
            ? await save(new RenderedPdf(document.Title, pdf, result.PageCount), cancellationToken).ConfigureAwait(false)
            : null;
        return Result(writer =>
        {
            writer.WriteBoolean("rendered", true);
            writer.WriteNumber("pages", result.PageCount);
            writer.WriteNumber("bytes", result.BytesWritten);
            if (saved is not null)
            {
                writer.WriteString("file", saved);
            }

            Diagnostics(writer, result.Diagnostics);
        });
    }

    /// <summary>Parses, checks and renders the document in the arguments with the host's options: the PDF, or the problems.</summary>
    internal static async ValueTask<(PdfDocument? Document, byte[]? Pdf, RenderResult? Result, IReadOnlyList<PdfDiagnostic> Problems)> RenderPdfAsync(PdfToolOptions options, AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        (PdfDocument? document, PdfData data, List<PdfDiagnostic> problems) = Parse(arguments);
        if (document is null || problems.Exists(d => d.Severity == DiagnosticSeverity.Error))
        {
            return (null, null, null, problems);
        }

        using var output = new MemoryStream();
        RenderResult result;
        try
        {
            RenderOptions host = options.Render;
            var render = new RenderOptions
            {
                Fonts = host.Fonts,
                FontResolver = host.FontResolver,
                Conformance = host.Conformance,
                Archive = host.Archive,
                Limits = host.Limits,
                ImageProcessor = host.ImageProcessor,
                CreationDate = host.CreationDate,
                Progress = host.Progress,
                Data = data,
            };
            result = await document.RenderAsync(output, render, cancellationToken).ConfigureAwait(false);
        }
        catch (PdfDocumentException ex)
        {
            return (null, null, null, ex.Diagnostics);
        }
        catch (PdfLimitException ex)
        {
            return (null, null, null, [new PdfDiagnostic("ZPDF-R003", DiagnosticSeverity.Error, ex.Message, "")]);
        }

        return (document, output.ToArray(), result, problems);
    }

    internal static JsonElement Failed(IEnumerable<PdfDiagnostic> problems) => Result(writer =>
    {
        writer.WriteBoolean("rendered", false);
        Diagnostics(writer, problems);
    });
}

/// <summary><c>render_preview</c>: renders the document and returns one page as a PNG picture, to check the layout by looking.</summary>
internal sealed class PreviewTool(PdfToolOptions options) : PdfTool(
    "render_preview",
    "Renders a Zorg.Pdf document with its data and returns one page as a PNG picture, with the page count and warnings. Use it to check the layout by looking: overlapping or cut-off text, empty pages, an address outside its window, tables running off the page.",
    """
    {
      "type": "object",
      "properties": {
        "document": { "type": "object", "description": "The document, as JSON. See get_document_schema." },
        "data": { "type": "object", "description": "Data for the document's slots and template model." },
        "page": { "type": "integer", "minimum": 1, "default": 1, "description": "The page to show, from 1." }
      },
      "required": ["document"]
    }
    """)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        int page = arguments.TryGetValue("page", out object? value) ? value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out int p) => p,
            int p => p,
            long p => (int)Math.Clamp(p, int.MinValue, int.MaxValue),
            _ => 1,
        } : 1;
        (_, byte[]? pdf, RenderResult? result, IReadOnlyList<PdfDiagnostic> problems) = await RenderTool.RenderPdfAsync(options, arguments, cancellationToken).ConfigureAwait(false);
        if (pdf is null || result is null)
        {
            return RenderTool.Failed(problems);
        }

        if (page < 1 || page > result.PageCount)
        {
            return Result(writer =>
            {
                writer.WriteBoolean("rendered", true);
                writer.WriteNumber("pages", result.PageCount);
                writer.WriteString("error", $"There's no page {page}: the document has {result.PageCount}, numbered from 1.");
            });
        }

        byte[] png = await options.PreviewAsync!(pdf, page - 1, cancellationToken).ConfigureAwait(false);
        JsonElement summary = Result(writer =>
        {
            writer.WriteBoolean("rendered", true);
            writer.WriteNumber("page", page);
            writer.WriteNumber("pages", result.PageCount);
            Diagnostics(writer, result.Diagnostics);
        });
        return new List<AIContent> { new TextContent(summary.GetRawText()), new DataContent(png, "image/png") };
    }
}

/// <summary><c>get_capabilities</c>: what the host renders with, so the model writes documents that can be rendered.</summary>
internal sealed class CapabilitiesTool(PdfToolOptions options) : PdfTool(
    "get_capabilities",
    "Returns what documents can use here: the font families, the default font, whether other installed fonts can be named, the conformance claimed, the limits, and whether render_preview exists.",
    """{ "type": "object", "properties": {} }""")
{
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        RenderOptions render = options.Render;
        return ValueTask.FromResult<object?>(Result(writer =>
        {
            writer.WriteStartArray("fontFamilies");
            foreach (string family in render.Fonts.Families.Keys.Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(family);
            }

            writer.WriteEndArray();
            if (render.Fonts.DefaultFamily is { } defaultFamily)
            {
                writer.WriteString("defaultFontFamily", defaultFamily);
            }

            writer.WriteBoolean("installedFontsByName", render.FontResolver is not null);
            writer.WriteString("conformance", render.Conformance == PdfConformance.PdfUA1 ? "pdfUA1" : "none");
            writer.WriteString("archive", render.Archive switch { PdfArchive.PdfA2a => "pdfA2a", PdfArchive.PdfA2u => "pdfA2u", PdfArchive.PdfA2b => "pdfA2b", _ => "none" });
            writer.WriteBoolean("imageFormatsBeyondJpegAndPng", render.ImageProcessor is not null);
            writer.WriteStartObject("limits");
            writer.WriteNumber("maxPages", render.Limits.MaxPages);
            writer.WriteNumber("maxTableRows", render.Limits.MaxTableRows);
            writer.WriteNumber("maxImageBytes", render.Limits.MaxImageBytes);
            writer.WriteNumber("maxImagePixels", render.Limits.MaxImagePixels);
            writer.WriteEndObject();
            writer.WriteBoolean("previews", options.PreviewAsync is not null);
            writer.WriteString("schema", PdfJsonSchema.Id);
        }));
    }
}
