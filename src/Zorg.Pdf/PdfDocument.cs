using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization;
using Zorg.Pdf.Json;
using Zorg.Pdf.Validation;

namespace Zorg.Pdf;

/// <summary>
/// A document: title, language, page settings, optional header and footer bands, styles, and the blocks that flow
/// down the pages. It's an immutable value: build it with object initialisers and collection expressions, change it
/// with <c>with</c>, keep it in a <c>static readonly</c> field, and serialise it to and from JSON losslessly.
/// </summary>
/// <remarks>
/// The JSON form is described by the schema in <see cref="PdfJsonSchema"/>. Data that changes per render, such as
/// table rows, isn't in the document: tables and images name a slot that's filled when rendering.
/// </remarks>
/// <example>
/// <code>
/// static readonly PdfDocument Invoice = new("Invoice 2026-0042")
/// {
///     Footer = new PageBand([new Paragraph(["Page ", Field.PageNumber, " of ", Field.PageCount]) { Align = TextAlign.Right }]),
///     Content =
///     [
///         new Heading("Invoice 2026-0042"),
///         new Paragraph("Thank you for your order. Payment is due within 30 days."),
///         new Table([new Column("Description") { Width = ColumnWidth.Star(5) }, new Column("Amount") { Align = TextAlign.Right }])
///         {
///             Rows = TableRows.Bound("lines"),
///         },
///     ],
/// };
///
/// ValidationResult check = Invoice.Validate();
/// string json = Invoice.ToJson(indented: true);
/// </code>
/// </example>
public sealed record PdfDocument
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal PdfDocument(string title, string? schema = null, string language = "en", PageSettings? page = null, PageBand? header = null, PageBand? footer = null, StyleSheet? styles = null, ImmutableArray<Block> content = default) =>
        (Title, Schema, Language, Page, Header, Footer, Styles, Content) = (title, schema, language, page ?? PageSettings.A4, header, footer, styles, content.IsDefault ? [] : content);

    /// <summary>A document with a title.</summary>
    /// <param name="title">The title, shown by viewers in place of the file name and required for accessibility.</param>
    [SetsRequiredMembers]
    public PdfDocument(string title) => Title = title;

    /// <summary>The schema URL in JSON (<c>$schema</c>). Written by <see cref="ToJson"/>; checked by the parser.</summary>
    [JsonPropertyName("$schema")]
    [JsonPropertyOrder(-1)]
    public string? Schema { get; init; }

    /// <summary>The title. Viewers show it in place of the file name, and PDF/UA requires one.</summary>
    [JsonRequired]
    public required string Title { get; init; }

    /// <summary>The language of the text, as a BCP 47 tag such as <c>en</c>, <c>nb-NO</c> or <c>de-CH</c>.</summary>
    public string Language { get; init; } = "en";

    /// <summary>Size, orientation and margins.</summary>
    public PageSettings Page { get; init; } = PageSettings.A4;

    /// <summary>Content at the top of every page, above the content area.</summary>
    public PageBand? Header { get; init; }

    /// <summary>Content at the bottom of every page, below the content area.</summary>
    public PageBand? Footer { get; init; }

    /// <summary>Named styles, on top of <see cref="StyleSheet.Default"/>. Null: the defaults only.</summary>
    public StyleSheet? Styles { get; init; }

    /// <summary>The blocks, in reading order.</summary>
    public ImmutableArray<Block> Content { get; init; } = [];

    /// <summary>Parses a JSON document, throwing on any problem.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The document. Call <see cref="Validate"/> before rendering it.</returns>
    /// <exception cref="PdfDocumentException">The JSON isn't a document; the diagnostics say where.</exception>
    public static PdfDocument ParseJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return ParseJson(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Parses UTF-8 JSON, throwing on any problem.</summary>
    /// <param name="utf8Json">The UTF-8 bytes.</param>
    /// <returns>The document. Call <see cref="Validate"/> before rendering it.</returns>
    /// <exception cref="PdfDocumentException">The JSON isn't a document; the diagnostics say where.</exception>
    public static PdfDocument ParseJson(ReadOnlySpan<byte> utf8Json)
    {
        DocumentParseResult result = TryParseJson(utf8Json);
        return result.IsSuccess ? result.Document : throw new PdfDocumentException(result.Diagnostics);
    }

    /// <summary>Parses a JSON document without throwing.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The document, or diagnostics with JSON pointers.</returns>
    /// <example>
    /// <code>
    /// DocumentParseResult parsed = PdfDocument.TryParseJson(json);
    /// if (!parsed.IsSuccess)
    /// {
    ///     return parsed.Diagnostics;   // e.g. "error ZPDF-J002 at /content/1/colour: Unknown property 'colour'..."
    /// }
    /// </code>
    /// </example>
    public static DocumentParseResult TryParseJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return TryParseJson(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Parses UTF-8 JSON without throwing.</summary>
    /// <param name="utf8Json">The UTF-8 bytes.</param>
    /// <returns>The document, or diagnostics with JSON pointers.</returns>
    public static DocumentParseResult TryParseJson(ReadOnlySpan<byte> utf8Json) => DocumentJson.TryParse(utf8Json);

    /// <summary>The document as JSON, with <c>$schema</c> set to the current schema. Default values are left out.</summary>
    /// <param name="indented">Indent for reading.</param>
    /// <returns>The JSON text.</returns>
    public string ToJson(bool indented = false) => DocumentJson.Write(this with { Schema = PdfJsonSchema.Id }, indented);

    /// <summary>
    /// Checks the document without rendering it: shape, values, accessibility basics. It never throws for a bad
    /// document; every problem is a diagnostic with a JSON pointer.
    /// </summary>
    /// <returns>The diagnostics.</returns>
    public ValidationResult Validate() => new(DocumentValidator.Validate(this));
}
