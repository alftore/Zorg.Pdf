using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>
/// A file carried inside the PDF, such as the XML of an e-invoice, the spreadsheet a report was made from, or a
/// signed original. Viewers list it in their attachments panel. Under PDF/A it needs PDF/A-3 (<see cref="PdfArchive.PdfA3b"/>
/// and up), which records how the file relates to the document. The bytes come from a data slot or are given here.
/// In JSON <c>"attachments": [{ "name": "invoice.xml", "source": { "$data": "xml" }, "mimeType": "application/xml", "relationship": "data" }]</c>.
/// </summary>
/// <example>
/// <code>
/// var invoice = document with { Attachments = [new Attachment("factur-x.xml", ImageSource.FromSlot("xml")) { MimeType = "text/xml", Relationship = AttachmentRelationship.Data }] };
/// var options = new RenderOptions { Fonts = fonts, Archive = PdfArchive.PdfA3b, Data = PdfData.Empty.WithFile("xml", xmlBytes) };
/// </code>
/// </example>
public sealed record Attachment
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    internal Attachment(string name, ImageSource source, string mimeType = "application/octet-stream", string? description = null, AttachmentRelationship relationship = AttachmentRelationship.Unspecified) =>
        (Name, Source, MimeType, Description, Relationship) = (name, source, mimeType, description, relationship);

    /// <summary>An attachment.</summary>
    /// <param name="name">The file name viewers show and save it as, such as "invoice.xml". No folders.</param>
    /// <param name="source">The bytes, or a data slot that holds them.</param>
    public Attachment(string name, ImageSource source) => (Name, Source) = (name, source);

    /// <summary>The file name, such as "invoice.xml". No folders; unique in the document.</summary>
    public string Name { get; init; }

    /// <summary>The file: its bytes, or a data slot (<see cref="PdfData.WithFile(string, System.ReadOnlyMemory{byte})"/>).</summary>
    public ImageSource Source { get; init; }

    /// <summary>The media type, such as "application/xml" or "text/csv". Default "application/octet-stream".</summary>
    public string MimeType { get; init; } = "application/octet-stream";

    /// <summary>What the file is, for the attachments panel and screen readers. Null: none.</summary>
    public string? Description { get; init; }

    /// <summary>How the file relates to the document; PDF/A-3 records it. Default <see cref="AttachmentRelationship.Unspecified"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public AttachmentRelationship Relationship { get; init; }
}

/// <summary>How an <see cref="Attachment"/> relates to the document (PDF/A-3's <c>/AFRelationship</c>). In JSON camel case: <c>"data"</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<AttachmentRelationship>))]
public enum AttachmentRelationship
{
    /// <summary>Not said.</summary>
    Unspecified,

    /// <summary>The original the document was made from, such as a word-processor file.</summary>
    Source,

    /// <summary>The data the document shows, such as an e-invoice's XML or a report's figures.</summary>
    Data,

    /// <summary>Another form of the same content, such as an audio reading.</summary>
    Alternative,

    /// <summary>Something that adds to the document without being its source or data.</summary>
    Supplement,
}
