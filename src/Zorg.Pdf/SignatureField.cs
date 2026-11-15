using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Zorg.Pdf;

/// <summary>
/// A visible signature field in the flow: a box where a signature shows. The field whose name matches
/// <see cref="Signing.PdfSignatureOptions.FieldName"/> is signed as the document renders and shows who signed and
/// when; the others stay empty for someone to sign later, in Acrobat or another signing tool.
/// </summary>
/// <example>
/// <code>
/// var approver = new SignatureField("approver") { Label = "Approved by the finance manager" };
/// </code>
/// In JSON:
/// <code>
/// { "type": "signatureField", "name": "approver", "label": "Approved by the finance manager" }
/// </code>
/// </example>
public sealed record SignatureField : Block
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal SignatureField(string name, string? label = null, Length? width = null, Length? height = null, TextAlign align = TextAlign.Left, string? style = null, bool keepWithNext = false, string? id = null) =>
        (Name, Label, Width, Height, Align, Style, KeepWithNext, Id) = (name, label, width, height, align, style, keepWithNext, id);

    /// <summary>A field with a name.</summary>
    /// <param name="name">The field's name, unique in the document.</param>
    [SetsRequiredMembers]
    public SignatureField(string name) => Name = name;

    /// <summary>The field's name: letters, digits, <c>-</c> and <c>_</c>, unique in the document. Signing tools show it.</summary>
    [JsonRequired]
    public required string Name { get; init; }

    /// <summary>
    /// What the field is for, such as "Approved by the finance manager": the field's description for screen readers,
    /// and shown in an empty field. Null: "Signature".
    /// </summary>
    public string? Label { get; init; }

    /// <summary>The box's width. Null: 60 mm, or the content width if that's narrower.</summary>
    public Length? Width { get; init; }

    /// <summary>The box's height. Null: 20 mm.</summary>
    public Length? Height { get; init; }

    /// <summary>Where the box sits across the content width.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TextAlign Align { get; init; }
}
