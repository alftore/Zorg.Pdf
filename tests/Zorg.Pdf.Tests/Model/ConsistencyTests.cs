using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Xunit;
using Zorg.Pdf.Json;
using Zorg.Pdf.Validation;

namespace Zorg.Pdf.Tests.Model;

/// <summary>The code, the docs, the schema and the examples must agree.</summary>
public sealed partial class ConsistencyTests
{
    /// <summary>Property names the custom converters read, which the source-generated metadata doesn't list.</summary>
    private static readonly HashSet<string> ConverterProperties =
        ["top", "right", "bottom", "left", "width", "height", "size", "orientation", "margin", "text", "style", "field", "lineBreak", "inlines", "align", "base64", "$data", "$schema", "type"];

    [Fact]
    public void EveryDiagnosticCodeIsDocumentedAndEveryDocumentedCodeExists()
    {
        string[] inCode = [.. typeof(DiagnosticCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).Order(StringComparer.Ordinal)];
        string[] inDocs = [.. DocumentedCode().Matches(File.ReadAllText(RepoFiles.Path("docs", "diagnostics.md"))).Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal)];

        Assert.Equal(inCode.Distinct(), inCode); // No code used twice.
        Assert.Equal(inCode, inDocs);
    }

    [Fact]
    public void TheEmbeddedSchemaIsTheFileAndItsIdMatches()
    {
        string file = File.ReadAllText(RepoFiles.Path("schema", "document-0.1.schema.json"));
        Assert.Equal(file, PdfJsonSchema.Current);
        using JsonDocument schema = JsonDocument.Parse(file);
        Assert.Equal(PdfJsonSchema.Id, schema.RootElement.GetProperty("$id").GetString());
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
    }

    [Fact]
    public void EveryModelPropertyIsInTheSchemaAndNoSchemaPropertyIsMadeUp()
    {
        using JsonDocument schema = JsonDocument.Parse(PdfJsonSchema.Current);
        var inSchema = new HashSet<string>(StringComparer.Ordinal);
        CollectPropertyNames(schema.RootElement, inSchema);

        var inModel = new HashSet<string>(StringComparer.Ordinal);
        foreach (Type type in typeof(PdfDocument).Assembly.GetExportedTypes().Where(t => t.Namespace == "Zorg.Pdf"))
        {
            if (PdfJsonContext.Default.GetTypeInfo(type) is { Kind: JsonTypeInfoKind.Object } info && info.Converter.GetType().Namespace != "Zorg.Pdf.Json")
            {
                foreach (JsonPropertyInfo property in info.Properties)
                {
                    inModel.Add(property.Name);
                }
            }
        }

        Assert.NotEmpty(inModel);
        Assert.Empty(inModel.Except(inSchema).Order(StringComparer.Ordinal));
        Assert.Empty(inSchema.Except(inModel).Except(ConverterProperties).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryExampleParsesAndValidates()
    {
        string[] examples = Directory.GetFiles(RepoFiles.Path("docs", "examples"), "*.json");
        Assert.True(examples.Length >= 3);
        foreach (string example in examples)
        {
            DocumentParseResult parsed = PdfDocument.TryParseJson(File.ReadAllText(example));
            Assert.True(parsed.IsSuccess, $"{Path.GetFileName(example)}: {string.Join("; ", parsed.Diagnostics)}");
            Assert.Equal(PdfJsonSchema.Id, parsed.Document.Schema);
            ValidationResult validation = parsed.Document.Validate();
            Assert.True(validation.Diagnostics.IsEmpty, $"{Path.GetFileName(example)}: {string.Join("; ", validation.Diagnostics)}");
        }
    }

    private static void CollectPropertyNames(JsonElement element, HashSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name == "properties" && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty declared in property.Value.EnumerateObject())
                    {
                        names.Add(declared.Name);
                    }
                }

                // "examples" and "default" hold sample documents, not declarations.
                if (property.Name is not ("examples" or "default"))
                {
                    CollectPropertyNames(property.Value, names);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                CollectPropertyNames(item, names);
            }
        }
    }

    [GeneratedRegex(@"^\| (ZPDF-[A-Z]\d{3}) \|", RegexOptions.Multiline)]
    private static partial Regex DocumentedCode();
}
