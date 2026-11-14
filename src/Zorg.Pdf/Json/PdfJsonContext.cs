using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zorg.Pdf.Json;

/// <summary>
/// Source-generated serialisation for the document model: no reflection, so it's trim- and AOT-safe. Unknown
/// properties are errors, because a misspelt property silently ignored is the worst outcome for an agent.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    MaxDepth = 64)]
[JsonSerializable(typeof(PdfDocument))]
[JsonSerializable(typeof(ImmutableArray<Inline>))]
[JsonSerializable(typeof(ImmutableArray<ImmutableArray<Cell>>))]
[JsonSerializable(typeof(ImageSource))]
[JsonSerializable(typeof(ImmutableArray<Block>))]
[JsonSerializable(typeof(ListMarker))]
[JsonSerializable(typeof(WatermarkLayer))]
[JsonSerializable(typeof(TextBoxPreset))]
[JsonSerializable(typeof(TextOverflow))]
[JsonSerializable(typeof(PageNumberStyle))]
[JsonSerializable(typeof(Hyphens))]
[JsonSerializable(typeof(TextStyle))]
[JsonSerializable(typeof(FieldKind))]
[JsonSerializable(typeof(TextAlign))]
[JsonSerializable(typeof(PageSize))]
[JsonSerializable(typeof(PageOrientation))]
[JsonSerializable(typeof(Thickness))]
internal sealed partial class PdfJsonContext : JsonSerializerContext;
