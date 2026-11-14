using System;
using System.IO;

namespace Zorg.Pdf;

/// <summary>
/// The JSON Schema (draft 2020-12) of the document format. Give it to an agent or editor that writes documents; the
/// parser accepts exactly what it describes.
/// </summary>
/// <example>
/// <code>
/// string schema = PdfJsonSchema.Current;   // put it in a system prompt or a tool description
/// </code>
/// </example>
public static class PdfJsonSchema
{
    /// <summary>The schema's <c>$id</c>, which documents put in <c>$schema</c>.</summary>
    public const string Id = "https://alftore.github.io/Zorg.Pdf/schema/document-0.1.schema.json";

    private static readonly Lazy<string> Text = new(Load);

    /// <summary>The schema text.</summary>
    public static string Current => Text.Value;

    private static string Load()
    {
        using Stream stream = typeof(PdfJsonSchema).Assembly.GetManifestResourceStream("document.schema.json")
            ?? throw new InvalidOperationException("The schema resource is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
