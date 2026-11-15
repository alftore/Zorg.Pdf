using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>
/// A barcode, drawn as vector rectangles so it scans at any print size: a QR code, Code 128, GS1-128 or EAN-13.
/// Like an image it needs <see cref="Alt"/> text (what the code holds or is for) unless it's <see cref="Decorative"/>.
/// The data can come from the template model with <see cref="Bind"/>.
/// In JSON <c>{ "type": "barcode", "kind": "qr", "data": "https://example.com", "size": "3cm", "alt": "Link to our site" }</c>.
/// </summary>
/// <example>
/// <code>
/// var payment = new Barcode(BarcodeKind.Qr, EpcPayment.Create("ACME AS", "NO9386011117947", 1250.00m, "Invoice 2026-0042").ToString())
/// {
///     Size = Length.Cm(3), Alt = "Payment QR code for invoice 2026-0042",
/// };
/// var parcel = new Barcode(BarcodeKind.Gs1_128, "(00)370123456789012342") { Height = Length.Cm(2), Alt = "Parcel label code" };
/// </code>
/// </example>
public sealed record Barcode : Block
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal Barcode(BarcodeKind kind, string? data = null, string? bind = null, Length? size = null, Length? width = null, Length? height = null, QrErrorCorrection errorCorrection = QrErrorCorrection.M, bool showText = true, string? alt = null, bool decorative = false, TextAlign align = TextAlign.Left, string? style = null, bool keepWithNext = false, string? id = null) =>
        (Kind, Data, Bind, Size, Width, Height, ErrorCorrection, ShowText, Alt, Decorative, Align, Style, KeepWithNext, Id) =
        (kind, data, bind, size, width, height, errorCorrection, showText, alt, decorative, align, style, keepWithNext, id);

    /// <summary>A barcode of a kind holding some data.</summary>
    /// <param name="kind">The symbology.</param>
    /// <param name="data">What it holds: text for QR and Code 128, element strings "(01)…" for GS1-128, 12 or 13 digits for EAN-13.</param>
    [SetsRequiredMembers]
    public Barcode(BarcodeKind kind, string data) => (Kind, Data) = (kind, data);

    /// <summary>The symbology.</summary>
    [JsonRequired]
    public required BarcodeKind Kind { get; init; }

    /// <summary>What the code holds. Set this or <see cref="Bind"/>.</summary>
    public string? Data { get; init; }

    /// <summary>A path in the template model whose value is the data, such as "payment.qr". Set this or <see cref="Data"/>.</summary>
    public string? Bind { get; init; }

    /// <summary>A QR code's side, quiet zone included. Null: 3 cm.</summary>
    public Length? Size { get; init; }

    /// <summary>A linear code's width, quiet zones included. Null: 0.33 mm per module, EAN-13's nominal size (37.3 mm with its quiet zones).</summary>
    public Length? Width { get; init; }

    /// <summary>A linear code's bar height. Null: 15 mm.</summary>
    public Length? Height { get; init; }

    /// <summary>A QR code's error correction. Default M (about 15 %), which payment codes use.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public QrErrorCorrection ErrorCorrection { get; init; } = QrErrorCorrection.M;

    /// <summary>Whether a linear code prints its data under the bars. Default true.</summary>
    public bool ShowText { get; init; } = true;

    /// <summary>What the code is for, read by screen readers. Required unless <see cref="Decorative"/>.</summary>
    public string? Alt { get; init; }

    /// <summary>True when the code is no use to someone who can't see it: it's then an artifact screen readers skip.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Decorative { get; init; }

    /// <summary>Where the code sits across the column.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TextAlign Align { get; init; }
}

/// <summary>A barcode symbology. In JSON <c>"qr"</c>, <c>"code128"</c>, <c>"gs1_128"</c> or <c>"ean13"</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<BarcodeKind>))]
public enum BarcodeKind
{
    /// <summary>QR Code: links, payment codes, any text up to about 2,900 bytes.</summary>
    Qr,

    /// <summary>Code 128: ASCII text and numbers, compact for digits.</summary>
    Code128,

    /// <summary>GS1-128: GS1 element strings such as "(01)09501101530003(10)ABC123", for logistics.</summary>
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "The symbology's name has a hyphen.")]
    Gs1_128,

    /// <summary>EAN-13: product numbers (GTIN-13).</summary>
    Ean13,
}

/// <summary>How much of a QR code can be damaged and still read: about 7 % (L), 15 % (M), 25 % (Q) or 30 % (H). In JSON <c>"l"</c>, <c>"m"</c>, <c>"q"</c> or <c>"h"</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<QrErrorCorrection>))]
public enum QrErrorCorrection
{
    /// <summary>About 7 %: the most data.</summary>
    L,

    /// <summary>About 15 %: the usual choice, and what EPC payment codes use.</summary>
    M,

    /// <summary>About 25 %.</summary>
    Q,

    /// <summary>About 30 %: for codes that may get dirty or have a logo over them.</summary>
    H,
}
