using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
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
    internal PdfDocument(string title, string? schema = null, string language = "en", PageSettings? page = null, PageBand? header = null, PageBand? footer = null, Watermark? watermark = null, StyleSheet? styles = null, ImmutableArray<Block> content = default, int outlineLevels = 3) =>
        (Title, Schema, Language, Page, Header, Footer, Watermark, Styles, Content, OutlineLevels) = (title, schema, language, page ?? PageSettings.A4, header, footer, watermark, styles, content.IsDefault ? [] : content, outlineLevels);

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

    /// <summary>Text or an image across every page, such as DRAFT; an artifact screen readers skip.</summary>
    public Watermark? Watermark { get; init; }

    /// <summary>
    /// How deep the outline (the viewer's bookmarks) goes: headings of level 1 to this one become bookmarks, nested by
    /// level. 3 by default; 0 for no outline.
    /// </summary>
    /// <example>
    /// <code>
    /// var report = document with { OutlineLevels = 2 };
    /// </code>
    /// </example>
    public int OutlineLevels { get; init; } = 3;

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
    /// Renders the document as PDF to <paramref name="output"/>, streaming pages as they're finished.
    /// </summary>
    /// <remarks>
    /// Everything that can be checked before writing is checked first (the document, fonts, every character's glyph);
    /// any error throws <see cref="PdfDocumentException"/> with all of them before a byte is written. After that, only
    /// cancellation, a <see cref="PdfLimits"/> limit or an I/O error can stop the render, leaving a partial file in
    /// <paramref name="output"/>; render to a <see cref="System.IO.MemoryStream"/> first when that matters. The output
    /// is always tagged for accessibility.
    /// </remarks>
    /// <param name="output">Where the PDF goes. It's flushed, not closed.</param>
    /// <param name="options">Fonts, conformance and limits. Fonts are required.</param>
    /// <param name="cancellationToken">Stops the render between blocks and pages.</param>
    /// <returns>The page count, the size and any warnings, such as words broken because they didn't fit.</returns>
    /// <exception cref="PdfDocumentException">The document, its fonts or its text have errors; nothing was written.</exception>
    /// <exception cref="PdfLimitException">A limit in <see cref="RenderOptions.Limits"/> was exceeded mid-render.</exception>
    /// <example>
    /// <code>
    /// var options = new RenderOptions
    /// {
    ///     Fonts = FontCollection.Empty.Add(new FontFamily("Inter") { Regular = FontSource.FromFile("Inter-Regular.ttf") }),
    /// };
    /// await using FileStream file = File.Create("invoice.pdf");
    /// RenderResult result = await Invoice.RenderAsync(file, options, cancellationToken);
    /// </code>
    /// </example>
    public ValueTask<RenderResult> RenderAsync(System.IO.Stream output, RenderOptions? options = null, System.Threading.CancellationToken cancellationToken = default) =>
        Rendering.RenderEngine.RenderAsync(this, output, options, synchronous: false, cancellationToken);

    /// <summary>
    /// Renders the document synchronously, writing with <see cref="System.IO.Stream.Write(ReadOnlySpan{byte})"/>. Prefer
    /// <see cref="RenderAsync"/> for network streams; this one suits files, memory and command-line tools.
    /// </summary>
    /// <param name="output">Where the PDF goes. It's flushed, not closed.</param>
    /// <param name="options">Fonts, conformance and limits. Fonts are required.</param>
    /// <returns>The page count, the size and any warnings.</returns>
    /// <exception cref="PdfDocumentException">The document, its fonts or its text have errors; nothing was written.</exception>
    /// <exception cref="PdfLimitException">A limit in <see cref="RenderOptions.Limits"/> was exceeded mid-render.</exception>
    public RenderResult Render(System.IO.Stream output, RenderOptions? options = null)
    {
        ValueTask<RenderResult> render = Rendering.RenderEngine.RenderAsync(this, output, options, synchronous: true, System.Threading.CancellationToken.None);

        // In synchronous mode nothing awaits a pending operation, so the task has always completed here.
        return render.IsCompleted
            ? render.GetAwaiter().GetResult()
            : throw new InvalidOperationException("A synchronous render left a pending operation. This is a bug in Zorg.Pdf; please report it.");
    }

    /// <summary>
    /// Checks the document without rendering it: shape, values, accessibility basics. It never throws for a bad
    /// document; every problem is a diagnostic with a JSON pointer.
    /// </summary>
    /// <returns>The diagnostics.</returns>
    public ValidationResult Validate() => new(DocumentValidator.Validate(this));
}
