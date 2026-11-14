using System;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zorg.Pdf.Validation;

namespace Zorg.Pdf.Json;

/// <summary>
/// Reads and writes documents, and turns serializer failures into diagnostics an agent can act on: a stable code, a
/// JSON pointer, and a message in the document's terms, never .NET type names.
/// </summary>
internal static partial class DocumentJson
{
    /// <summary>The largest document accepted, before parsing starts.</summary>
    internal const int MaxJsonBytes = 16 * 1024 * 1024;

    public static DocumentParseResult TryParse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxJsonBytes)
        {
            return Failed(DiagnosticCodes.DocumentTooLarge, $"The document is {utf8Json.Length:N0} bytes; the limit is {MaxJsonBytes:N0}.", "");
        }

        PdfDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(utf8Json, PdfJsonContext.Default.PdfDocument);
        }
        catch (JsonException ex)
        {
            return Failed(Describe(ex));
        }
        catch (NotSupportedException ex) when (MissingTypePath().Match(ex.Message) is { Success: true } match)
        {
            // The serializer can't make an abstract block without a "type"; it reports that as unsupported.
            return Failed(DiagnosticCodes.UnknownType, "A block needs a \"type\": \"heading\", \"paragraph\", \"list\", \"table\", \"image\", \"spacer\" or \"pageBreak\".", Pointer(match.Groups[1].Value));
        }

        if (document is null)
        {
            return Failed(DiagnosticCodes.InvalidJson, "The document is null. Expected an object with at least \"title\".", "");
        }

        if (document.Schema is { } schema && schema != PdfJsonSchema.Id)
        {
            return Failed(DiagnosticCodes.SchemaVersion, $"The document is written for the schema \"{schema}\"; this version of Zorg.Pdf reads \"{PdfJsonSchema.Id}\".", "/$schema");
        }

        return new DocumentParseResult(document, []);
    }

    public static string Write(PdfDocument document, bool indented)
    {
        if (!indented)
        {
            return JsonSerializer.Serialize(document, PdfJsonContext.Default.PdfDocument);
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            JsonSerializer.Serialize(writer, document, PdfJsonContext.Default.PdfDocument);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Converts a serializer path such as <c>$.content[2]['my key']</c> to a JSON pointer <c>/content/2/my key</c>.</summary>
    internal static string Pointer(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "$")
        {
            return "";
        }

        var pointer = new StringBuilder();
        foreach (Match segment in PathSegment().Matches(path))
        {
            string name = segment.Groups["name"].Success ? segment.Groups["name"].Value
                : segment.Groups["quoted"].Success ? segment.Groups["quoted"].Value
                : segment.Groups["index"].Value;
            pointer.Append('/').Append(name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
        }

        return pointer.ToString();
    }

    /// <summary>A serializer failure as a diagnostic: code, message in the document's terms, JSON pointer.</summary>
    internal static PdfDiagnostic Describe(JsonException ex)
    {
        string path = Pointer(ex.Path);
        if (ex is PdfJsonException own)
        {
            return Error(own.Code, own.Message, path + own.Suffix);
        }

        string message = ex.Message;
        if (UnmappedProperty().Match(message) is { Success: true } unmapped)
        {
            // The serializer's path stops at the object; the pointer names the property itself.
            string name = unmapped.Groups[1].Value;
            string at = path.EndsWith("/" + name, StringComparison.Ordinal) ? path : path + "/" + name;
            return Error(DiagnosticCodes.UnknownProperty, $"Unknown property '{name}'. Check the spelling against the schema (PdfJsonSchema.Current); properties are camelCase.", at);
        }

        if (MissingRequired().Match(message) is { Success: true } missing)
        {
            return Error(DiagnosticCodes.MissingProperty, $"Missing required {missing.Groups[1].Value}.", path);
        }

        if (UnknownDiscriminator().Match(message) is { Success: true } discriminator)
        {
            return Error(DiagnosticCodes.UnknownType, $"Unknown block type \"{discriminator.Groups[1].Value}\". Use \"heading\", \"paragraph\", \"list\", \"table\", \"image\", \"spacer\" or \"pageBreak\".", path);
        }

        // Syntax errors come from the reader, wrapped by the serializer.
        if (ex.InnerException?.GetType().Name == "JsonReaderException" || ex.GetType().Name == "JsonReaderException")
        {
            string detail = (ex.InnerException ?? ex).Message;
            int cut = detail.IndexOf(" LineNumber:", StringComparison.Ordinal);
            return Error(DiagnosticCodes.InvalidJson, $"The text isn't valid JSON at line {ex.LineNumber + 1}, byte {ex.BytePositionInLine + 1}: {(cut > 0 ? detail[..cut] : detail)}", path);
        }

        // Anything else is a value of the wrong kind; keep the serializer's wording for the token, minus .NET type names.
        string cleaned = DotNetTypeName().Replace(message, "a value of this kind");
        int pathNote = cleaned.IndexOf(" Path:", StringComparison.Ordinal);
        return Error(DiagnosticCodes.InvalidJson, (pathNote > 0 ? cleaned[..pathNote] : cleaned).Trim(), path);
    }

    private static PdfDiagnostic Error(string code, string message, string path) => new(code, DiagnosticSeverity.Error, message, path);

    private static DocumentParseResult Failed(PdfDiagnostic diagnostic) => new(null, [diagnostic]);

    private static DocumentParseResult Failed(string code, string message, string path) => Failed(Error(code, message, path));

    [GeneratedRegex(@"\.(?<name>[^.\[\]']+)|\['(?<quoted>(?:[^']|'')*)'\]|\[(?<index>\d+)\]")]
    private static partial Regex PathSegment();

    [GeneratedRegex(@"The JSON property '([^']*)' could not be mapped")]
    private static partial Regex UnmappedProperty();

    [GeneratedRegex(@"missing required properties including: (.*?)\.?$", RegexOptions.Singleline)]
    private static partial Regex MissingRequired();

    [GeneratedRegex(@"unrecognized type discriminator id '([^']*)'", RegexOptions.IgnoreCase)]
    private static partial Regex UnknownDiscriminator();

    [GeneratedRegex(@"Path: (\$[^\s|]*)")]
    private static partial Regex MissingTypePath();

    [GeneratedRegex(@"'?(System|Zorg)\.[A-Za-z0-9_.`\[\], ]+'?")]
    private static partial Regex DotNetTypeName();
}
