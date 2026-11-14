using System.Collections.Immutable;

namespace Zorg.Pdf;

/// <summary>
/// Extra document metadata in the PDF's XMP, under a namespace of its own: what e-invoice standards such as Factur-X
/// and ZUGFeRD require, or a company's own document properties. Under PDF/A the schema is also described in the PDF/A
/// extension schema, as ISO 19005 requires for anything but the predefined schemas.
/// </summary>
/// <example>
/// <code>
/// var archive = new XmpSchema("ACME archive", "https://acme.example/ns/archive/1.0/", "acme")
/// {
///     Properties = [new XmpProperty("CaseNumber", "2026-0042") { Description = "The case the document belongs to" }],
/// };
/// var options = new RenderOptions { Fonts = fonts, Archive = PdfArchive.PdfA3b, MetadataSchemas = [archive] };
/// </code>
/// </example>
/// <param name="Name">A human name for the schema, such as "Factur-X PDFA Extension Schema".</param>
/// <param name="Namespace">The namespace URI, as the standard defines it, compared as exact text.</param>
/// <param name="Prefix">The namespace prefix, such as "fx". Letters, digits, '-', '_' and '.', starting with a letter.</param>
public sealed record XmpSchema(string Name, string Namespace, string Prefix)
{
    /// <summary>The properties and their values.</summary>
    public ImmutableArray<XmpProperty> Properties { get; init; } = [];
}

/// <summary>A property of an <see cref="XmpSchema"/>: a name and a text value.</summary>
/// <param name="Name">The property name, such as "ConformanceLevel".</param>
/// <param name="Value">Its value.</param>
public sealed record XmpProperty(string Name, string Value)
{
    /// <summary>What the property means, for the PDF/A extension schema.</summary>
    public string Description { get; init; } = "";

    /// <summary>"external" (the default: set by people or tools from outside) or "internal" (derived from the document).</summary>
    public string Category { get; init; } = "external";
}
