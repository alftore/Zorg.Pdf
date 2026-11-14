using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Zorg.Pdf.Extensions.AI;

/// <summary>
/// Tools that let an AI agent write PDF documents: <c>get_document_schema</c> to learn the format,
/// <c>validate_document</c> to check a draft and fix it from diagnostics with JSON pointers, and
/// <c>render_document</c> to render it with data. They take and return JSON, use no reflection (Native AOT works), and
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
    /// <summary>The three tools, configured with the host's fonts, limits and storage.</summary>
    /// <param name="options">Fonts, conformance and limits for rendering, and where rendered files go.</param>
    /// <returns><c>get_document_schema</c>, <c>validate_document</c> and <c>render_document</c>.</returns>
    public static IReadOnlyList<AIFunction> Create(PdfToolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Render);
        return [new SchemaTool(), new ValidateTool(), new RenderTool(options)];
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
        A document is JSON: { "title": "...", "language": "en", "content": [blocks] }, with optional "page", "header", "footer" and "styles".
        Blocks have a "type": "heading" (with "level" 1 to 6, never skipping a level), "paragraph", "table", "image", "spacer" or "pageBreak".
        Text is "inlines": an array of strings, { "text": "...", "style": { "weight": "bold" } }, { "lineBreak": true } and, in headers and footers, { "field": "pageNumber" } or { "field": "pageCount" }.
        Tables have "columns" ([{ "header": "Amount", "width": "2*", "align": "right" }]) and "rows": an array of rows of cells (strings), or { "$data": "slot" } to take them from data.
        Images need "alt" text unless "decorative": true. Their "source" is { "$data": "slot" } (or { "base64": "..." } for small ones).
        Lengths are numbers in points or strings with a unit: "2cm", "12pt", "5mm".
        Data is a separate JSON object whose properties are slots: { "lines": [["1", "Consulting", "9 375,00"]], "logo": { "base64": "..." } }.
        Unknown properties are errors. validate_document returns every problem with a code and a JSON pointer: fix those and validate again.
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
        (PdfDocument? document, PdfData data, List<PdfDiagnostic> problems) = Parse(arguments);
        if (document is null || problems.Exists(d => d.Severity == DiagnosticSeverity.Error))
        {
            return Failed(problems);
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
                Limits = host.Limits,
                CreationDate = host.CreationDate,
                Progress = host.Progress,
                Data = data,
            };
            result = await document.RenderAsync(output, render, cancellationToken).ConfigureAwait(false);
        }
        catch (PdfDocumentException ex)
        {
            return Failed(ex.Diagnostics);
        }
        catch (PdfLimitException ex)
        {
            return Failed([new PdfDiagnostic("ZPDF-R003", DiagnosticSeverity.Error, ex.Message, "")]);
        }

        string? saved = options.SaveAsync is { } save
            ? await save(new RenderedPdf(document.Title, output.ToArray(), result.PageCount), cancellationToken).ConfigureAwait(false)
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

    private static JsonElement Failed(IEnumerable<PdfDiagnostic> problems) => Result(writer =>
    {
        writer.WriteBoolean("rendered", false);
        Diagnostics(writer, problems);
    });
}
