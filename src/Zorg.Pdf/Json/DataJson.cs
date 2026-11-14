using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Zorg.Pdf.Validation;

namespace Zorg.Pdf.Json;

/// <summary>
/// Reads render data from JSON: an object whose properties are slots. An array of arrays is a table's rows, each an
/// array of cells as in a document (a string, or <c>{ "inlines": [...] }</c>); an object with <c>"base64"</c> is an
/// image. The whole object is also the model that template bindings read. Every problem is collected with a JSON
/// pointer, as for documents.
/// </summary>
internal static class DataJson
{
    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 };

    public static PdfData Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length > DocumentJson.MaxJsonBytes)
        {
            throw Fail(DiagnosticCodes.DocumentTooLarge, $"The data is {utf8Json.Length:N0} bytes; the limit is {DocumentJson.MaxJsonBytes:N0}.", "");
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(utf8Json, Options);
        }
        catch (JsonException ex)
        {
            throw Fail(DiagnosticCodes.InvalidJson, $"The data isn't valid JSON: {ex.Message}", "");
        }

        using (json)
        {
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Fail(DiagnosticCodes.InvalidJson, "Data is an object whose properties are slots: { \"lines\": [[\"1\", \"Consulting\"]], \"logo\": { \"base64\": \"...\" } }.", "");
            }

            PdfData data = PdfData.Empty;
            var problems = new List<PdfDiagnostic>();
            foreach (JsonProperty slot in json.RootElement.EnumerateObject())
            {
                string path = "/" + slot.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                try
                {
                    // Arrays of arrays are table rows, objects with "base64" are images; everything is also model,
                    // for templates to bind (an array of objects, say, is model only).
                    data = slot.Value switch
                    {
                        _ when string.IsNullOrWhiteSpace(slot.Name) => throw new PdfJsonException(DiagnosticCodes.EmptySlot, "A data slot needs a name."),
                        { ValueKind: JsonValueKind.Array } rows when rows.EnumerateArray().All(r => r.ValueKind == JsonValueKind.Array) => data.WithRows(slot.Name, Rows(rows)),
                        { ValueKind: JsonValueKind.Object } image when image.TryGetProperty("base64", out _) || image.TryGetProperty("$data", out _) => data.WithImage(slot.Name, Image(image)),
                        _ => data,
                    };
                }
                catch (JsonException ex)
                {
                    PdfDiagnostic described = DocumentJson.Describe(ex);
                    problems.Add(described with { Path = path + described.Path });
                }
            }

            return problems.Count == 0 ? data.WithModel(json.RootElement) : throw new PdfDocumentException([.. problems]);
        }
    }

    private static List<IReadOnlyList<Cell>> Rows(JsonElement rows)
    {
        ImmutableArray<ImmutableArray<Cell>> parsed = rows.Deserialize(PdfJsonContext.Default.ImmutableArrayImmutableArrayCell);
        var result = new List<IReadOnlyList<Cell>>(parsed.Length);
        foreach (ImmutableArray<Cell> row in parsed)
        {
            result.Add(row);
        }

        return result;
    }

    private static ReadOnlyMemory<byte> Image(JsonElement image) =>
        image.Deserialize(PdfJsonContext.Default.ImageSource) is BytesImageSource bytes
            ? bytes.Bytes
            : throw new PdfJsonException(DiagnosticCodes.InvalidJson, "An image in data is { \"base64\": \"...\" }; a slot can't name another slot.");

    private static PdfDocumentException Fail(string code, string message, string path) =>
        new([new PdfDiagnostic(code, DiagnosticSeverity.Error, message, path)]);
}
