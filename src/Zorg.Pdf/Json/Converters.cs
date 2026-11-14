using System;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Zorg.Pdf.Validation;

namespace Zorg.Pdf.Json;

/// <summary>A JSON problem with a diagnostic code. The serializer fills in the path when it passes through.</summary>
internal sealed class PdfJsonException : JsonException
{
    public PdfJsonException()
    {
    }

    public PdfJsonException(string message)
        : base(message)
    {
    }

    public PdfJsonException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PdfJsonException(string code, string message)
        : base(message) => Code = code;

    public string Code { get; } = DiagnosticCodes.InvalidJson;

    /// <summary>
    /// A JSON pointer below the serializer's path, for problems inside a value a converter reads in parts. The
    /// serializer only knows the path to the converter's value.
    /// </summary>
    public string Suffix { get; init; } = "";

    /// <summary>Wraps a failure in a nested read, keeping its code, message and pointer under <paramref name="name"/>.</summary>
    public static PdfJsonException Nested(string name, JsonException inner)
    {
        PdfDiagnostic diagnostic = DocumentJson.Describe(inner);
        return new PdfJsonException(diagnostic.Code, diagnostic.Message) { Suffix = (name.Length == 0 ? "" : "/" + name) + diagnostic.Path };
    }

    /// <summary>
    /// Reads a nested value through the serializer. A nested read starts its own path at the root, so a failure is
    /// rethrown with the pointer below <paramref name="name"/>.
    /// </summary>
    public static T Read<T>(ref Utf8JsonReader reader, JsonSerializerOptions options, string name)
    {
        try
        {
            return JsonSerializer.Deserialize(ref reader, (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)))!;
        }
        catch (JsonException ex)
        {
            throw Nested(name, ex);
        }
    }

    public static PdfJsonException UnknownProperty(string name, string expected) =>
        new(DiagnosticCodes.UnknownProperty, $"Unknown property '{name}'. Expected {expected}.");

    public static PdfJsonException Invalid(string message) => new(DiagnosticCodes.InvalidJson, message);
}

/// <summary>Enums as camelCase strings, never numbers.</summary>
internal sealed class CamelCaseEnumConverter<T> : JsonStringEnumConverter<T>
    where T : struct, Enum
{
    public CamelCaseEnumConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

internal sealed class LengthJsonConverter : JsonConverter<Length>
{
    public override Length Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ReadLength(ref reader);

    public override void Write(Utf8JsonWriter writer, Length value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(Math.Round(value.Points, 4));

    internal static Length ReadLength(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return new Length(reader.GetDouble());
        }

        if (reader.TokenType == JsonTokenType.String && Length.TryParse(reader.GetString(), out Length length))
        {
            return length;
        }

        throw PdfJsonException.Invalid("Expected a length: a number of points, or a string such as \"12pt\", \"5mm\", \"2cm\" or \"0.5in\".");
    }
}

internal sealed class ThicknessJsonConverter : JsonConverter<Thickness>
{
    public override Thickness Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return Thickness.All(LengthJsonConverter.ReadLength(ref reader));
        }

        var thickness = default(Thickness);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString()!;
            reader.Read();
            Length length = LengthJsonConverter.ReadLength(ref reader);
            thickness = name switch
            {
                "top" => thickness with { Top = length },
                "right" => thickness with { Right = length },
                "bottom" => thickness with { Bottom = length },
                "left" => thickness with { Left = length },
                _ => throw PdfJsonException.UnknownProperty(name, "top, right, bottom or left"),
            };
        }

        return thickness;
    }

    public override void Write(Utf8JsonWriter writer, Thickness value, JsonSerializerOptions options)
    {
        if (value.Top == value.Right && value.Top == value.Bottom && value.Top == value.Left)
        {
            writer.WriteNumberValue(Math.Round(value.Top.Points, 4));
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("top", Math.Round(value.Top.Points, 4));
        writer.WriteNumber("right", Math.Round(value.Right.Points, 4));
        writer.WriteNumber("bottom", Math.Round(value.Bottom.Points, 4));
        writer.WriteNumber("left", Math.Round(value.Left.Points, 4));
        writer.WriteEndObject();
    }
}

/// <summary>Page settings by hand: a margin of 0 is valid, so no default value can stand for "not given".</summary>
internal sealed class PageSettingsJsonConverter : JsonConverter<PageSettings>
{
    public override PageSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw PdfJsonException.Invalid("Expected page settings: { \"size\": \"A4\", \"orientation\": \"portrait\", \"margin\": \"2cm\" }.");
        }

        var page = new PageSettings();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString()!;
            reader.Read();
            page = name switch
            {
                "size" => page with { Size = PdfJsonException.Read<PageSize>(ref reader, options, "size") },
                "orientation" => page with { Orientation = PdfJsonException.Read<PageOrientation>(ref reader, options, "orientation") },
                "margin" => page with { Margin = PdfJsonException.Read<Thickness>(ref reader, options, "margin") },
                _ => throw PdfJsonException.UnknownProperty(name, "size, orientation or margin"),
            };
        }

        return page;
    }

    public override void Write(Utf8JsonWriter writer, PageSettings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("size");
        JsonSerializer.Serialize(writer, value.Size, (JsonTypeInfo<PageSize>)options.GetTypeInfo(typeof(PageSize)));
        if (value.Orientation != PageOrientation.Portrait)
        {
            writer.WritePropertyName("orientation");
            JsonSerializer.Serialize(writer, value.Orientation, (JsonTypeInfo<PageOrientation>)options.GetTypeInfo(typeof(PageOrientation)));
        }

        writer.WritePropertyName("margin");
        JsonSerializer.Serialize(writer, value.Margin, (JsonTypeInfo<Thickness>)options.GetTypeInfo(typeof(Thickness)));
        writer.WriteEndObject();
    }
}

internal sealed class ColorJsonConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Color.TryParse(reader.GetString(), out Color color)
            ? color
            : throw PdfJsonException.Invalid("Expected a colour as \"#RRGGBB\", such as \"#1F6FEB\".");

    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

internal sealed class PageSizeJsonConverter : JsonConverter<PageSize>
{
    public override PageSize Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() switch
            {
                "A3" => PageSize.A3,
                "A4" => PageSize.A4,
                "A5" => PageSize.A5,
                "Letter" => PageSize.Letter,
                "Legal" => PageSize.Legal,
                var other => throw PdfJsonException.Invalid($"Unknown page size \"{other}\". Use \"A3\", \"A4\", \"A5\", \"Letter\", \"Legal\", or {{ \"width\": ..., \"height\": ... }}."),
            };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw PdfJsonException.Invalid("Expected a page size: a name such as \"A4\", or { \"width\": ..., \"height\": ... }.");
        }

        Length? width = null;
        Length? height = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString()!;
            reader.Read();
            switch (name)
            {
                case "width":
                    width = LengthJsonConverter.ReadLength(ref reader);
                    break;
                case "height":
                    height = LengthJsonConverter.ReadLength(ref reader);
                    break;
                default:
                    throw PdfJsonException.UnknownProperty(name, "width or height");
            }
        }

        return width is { } w && height is { } h
            ? new PageSize(w, h)
            : throw new PdfJsonException(DiagnosticCodes.MissingProperty, "A page size object needs both width and height.");
    }

    public override void Write(Utf8JsonWriter writer, PageSize value, JsonSerializerOptions options)
    {
        string? name = value == PageSize.A4 ? "A4" : value == PageSize.A3 ? "A3" : value == PageSize.A5 ? "A5"
            : value == PageSize.Letter ? "Letter" : value == PageSize.Legal ? "Legal" : null;
        if (name is not null)
        {
            writer.WriteStringValue(name);
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("width", Math.Round(value.Width.Points, 4));
        writer.WriteNumber("height", Math.Round(value.Height.Points, 4));
        writer.WriteEndObject();
    }
}

internal sealed class ColumnWidthJsonConverter : JsonConverter<ColumnWidth>
{
    public override ColumnWidth Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } text && text.EndsWith('*'))
        {
            string share = text[..^1];
            return share.Length == 0
                ? ColumnWidth.Star()
                : double.TryParse(share, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
                    ? ColumnWidth.Star(value)
                    : throw PdfJsonException.Invalid($"\"{text}\" isn't a column width. Use \"*\", \"2*\" or a length such as \"3cm\".");
        }

        return ColumnWidth.Fixed(LengthJsonConverter.ReadLength(ref reader));
    }

    public override void Write(Utf8JsonWriter writer, ColumnWidth value, JsonSerializerOptions options)
    {
        if (value.IsStar)
        {
            writer.WriteStringValue(value.Value == 1 ? "*" : Math.Round(value.Value, 4).ToString(System.Globalization.CultureInfo.InvariantCulture) + "*");
        }
        else
        {
            writer.WriteNumberValue(Math.Round(value.Value, 4));
        }
    }
}

/// <summary>Inlines: a bare string, <c>{ "text", "style" }</c>, <c>{ "lineBreak": true }</c> or <c>{ "field" }</c>.</summary>
internal sealed class InlineJsonConverter : JsonConverter<Inline>
{
    public override Inline Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new TextRun(reader.GetString()!);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw PdfJsonException.Invalid("Expected an inline: a string, { \"text\": ... }, { \"bind\": \"path\" }, { \"field\": \"pageNumber\" } or { \"lineBreak\": true }.");
        }

        string? bind = null, format = null;
        string? text = null;
        TextStyle? style = null;
        FieldKind? field = null;
        bool lineBreak = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString()!;
            reader.Read();
            switch (name)
            {
                case "text":
                    text = reader.TokenType == JsonTokenType.String ? reader.GetString() : throw PdfJsonException.Invalid("\"text\" must be a string.");
                    break;
                case "style":
                    style = PdfJsonException.Read<TextStyle>(ref reader, options, "style");
                    break;
                case "field":
                    field = PdfJsonException.Read<FieldKind>(ref reader, options, "field");
                    break;
                case "lineBreak":
                    lineBreak = reader.TokenType == JsonTokenType.True ? true : throw PdfJsonException.Invalid("\"lineBreak\" must be true.");
                    break;
                case "bind":
                    bind = reader.TokenType == JsonTokenType.String ? reader.GetString() : throw PdfJsonException.Invalid("\"bind\" must be a path, such as \"customer.name\".");
                    break;
                case "format":
                    format = reader.TokenType == JsonTokenType.String ? reader.GetString() : throw PdfJsonException.Invalid("\"format\" must be a format string, such as \"N2\".");
                    break;
                default:
                    throw PdfJsonException.UnknownProperty(name, "text, bind, format, style, field or lineBreak");
            }
        }

        int kinds = (text is not null ? 1 : 0) + (bind is not null ? 1 : 0) + (field is not null ? 1 : 0) + (lineBreak ? 1 : 0);
        if (kinds != 1)
        {
            throw PdfJsonException.Invalid("An inline object needs exactly one of \"text\", \"bind\", \"field\" or \"lineBreak\".");
        }

        if (style is not null && text is null && bind is null)
        {
            throw PdfJsonException.Invalid("Only text and bind inlines take a \"style\".");
        }

        if (format is not null && bind is null)
        {
            throw PdfJsonException.Invalid("Only bind inlines take a \"format\".");
        }

        return text is not null ? new TextRun(text) { Style = style }
            : bind is not null ? new Bind(bind) { Format = format, Style = style }
            : field is { } kind ? new Field(kind)
            : LineBreak.Value;
    }

    public override void Write(Utf8JsonWriter writer, Inline value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case TextRun { Style: null } run:
                writer.WriteStringValue(run.Text);
                break;
            case TextRun run:
                writer.WriteStartObject();
                writer.WriteString("text", run.Text);
                writer.WritePropertyName("style");
                JsonSerializer.Serialize(writer, run.Style, (JsonTypeInfo<TextStyle>)options.GetTypeInfo(typeof(TextStyle)));
                writer.WriteEndObject();
                break;
            case Bind bind:
                writer.WriteStartObject();
                writer.WriteString("bind", bind.Path);
                if (bind.Format is { } format)
                {
                    writer.WriteString("format", format);
                }

                if (bind.Style is { } style)
                {
                    writer.WritePropertyName("style");
                    JsonSerializer.Serialize(writer, style, (JsonTypeInfo<TextStyle>)options.GetTypeInfo(typeof(TextStyle)));
                }

                writer.WriteEndObject();
                break;
            case Field field:
                writer.WriteStartObject();
                writer.WritePropertyName("field");
                JsonSerializer.Serialize(writer, field.Kind, (JsonTypeInfo<FieldKind>)options.GetTypeInfo(typeof(FieldKind)));
                writer.WriteEndObject();
                break;
            case LineBreak:
                writer.WriteStartObject();
                writer.WriteBoolean("lineBreak", true);
                writer.WriteEndObject();
                break;
            default:
                throw new NotSupportedException($"Inline type {value?.GetType().Name ?? "null"} can't be written as JSON.");
        }
    }
}

/// <summary>Cells: a string, or <c>{ "inlines": [...], "align": ... }</c>.</summary>
internal sealed class CellJsonConverter : JsonConverter<Cell>
{
    public override Cell Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new Cell(reader.GetString()!);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw PdfJsonException.Invalid("Expected a cell: a string, or { \"inlines\": [...], \"align\": ..., \"columnSpan\": 2 }.");
        }

        ImmutableArray<Inline>? inlines = null;
        TextAlign? align = null;
        string? bind = null, format = null;
        int columnSpan = 1, rowSpan = 1;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString()!;
            reader.Read();
            switch (name)
            {
                case "inlines":
                    inlines = PdfJsonException.Read<ImmutableArray<Inline>>(ref reader, options, "inlines");
                    break;
                case "align":
                    align = PdfJsonException.Read<TextAlign>(ref reader, options, "align");
                    break;
                case "columnSpan" when reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int columns):
                    columnSpan = columns;
                    break;
                case "rowSpan" when reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int rows):
                    rowSpan = rows;
                    break;
                case "columnSpan" or "rowSpan":
                    throw new PdfJsonException(DiagnosticCodes.InvalidJson, $"\"{name}\" must be a whole number.") { Suffix = "/" + name };
                case "bind" or "format":
                    string text = reader.TokenType == JsonTokenType.String ? reader.GetString()! : throw new PdfJsonException(DiagnosticCodes.InvalidJson, $"\"{name}\" must be a string.") { Suffix = "/" + name };
                    (bind, format) = name == "bind" ? (text, format) : (bind, text);
                    break;
                default:
                    throw PdfJsonException.UnknownProperty(name, "inlines, bind, format, align, columnSpan or rowSpan");
            }
        }

        if (bind is not null || format is not null)
        {
            // Shorthand for a cell holding one binding: { "bind": "amount", "format": "N2" }.
            inlines = inlines is null && bind is not null
                ? [new Bind(bind) { Format = format }]
                : throw new PdfJsonException(DiagnosticCodes.InvalidJson, bind is null ? "\"format\" goes with \"bind\"." : "A cell has \"inlines\" or \"bind\", not both.");
        }

        return inlines is { } found
            ? new Cell { Inlines = found, Align = align, ColumnSpan = columnSpan, RowSpan = rowSpan }
            : throw new PdfJsonException(DiagnosticCodes.MissingProperty, "A cell object needs \"inlines\".");
    }

    public override void Write(Utf8JsonWriter writer, Cell value, JsonSerializerOptions options)
    {
        if (value.Align is null && value.ColumnSpan == 1 && value.RowSpan == 1 && value.Inlines is [TextRun { Style: null } run])
        {
            writer.WriteStringValue(run.Text);
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("inlines");
        JsonSerializer.Serialize(writer, value.Inlines, (JsonTypeInfo<ImmutableArray<Inline>>)options.GetTypeInfo(typeof(ImmutableArray<Inline>)));
        if (value.Align is { } align)
        {
            writer.WritePropertyName("align");
            JsonSerializer.Serialize(writer, align, (JsonTypeInfo<TextAlign>)options.GetTypeInfo(typeof(TextAlign)));
        }

        if (value.ColumnSpan != 1)
        {
            writer.WriteNumber("columnSpan", value.ColumnSpan);
        }

        if (value.RowSpan != 1)
        {
            writer.WriteNumber("rowSpan", value.RowSpan);
        }

        writer.WriteEndObject();
    }
}

/// <summary>A list item: a string (one paragraph), or <c>{ "content": [blocks] }</c>.</summary>
internal sealed class ListItemJsonConverter : JsonConverter<ListItem>
{
    public override ListItem Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new ListItem(reader.GetString()!);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw PdfJsonException.Invalid("Expected a list item: a string, or { \"content\": [paragraphs and lists] }.");
        }

        ImmutableArray<Block>? content = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString()!;
            reader.Read();
            content = name == "content"
                ? PdfJsonException.Read<ImmutableArray<Block>>(ref reader, options, "content")
                : throw PdfJsonException.UnknownProperty(name, "content");
        }

        return content is { } found
            ? new ListItem(found)
            : throw new PdfJsonException(DiagnosticCodes.MissingProperty, "A list item object needs \"content\".");
    }

    public override void Write(Utf8JsonWriter writer, ListItem value, JsonSerializerOptions options)
    {
        if (value.Content is [Paragraph { Inlines: [TextRun { Style: null } run], Align: TextAlign.Left, SpaceAfter: null, Style: null, KeepWithNext: false }])
        {
            writer.WriteStringValue(run.Text);
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("content");
        JsonSerializer.Serialize(writer, value.Content, (JsonTypeInfo<ImmutableArray<Block>>)options.GetTypeInfo(typeof(ImmutableArray<Block>)));
        writer.WriteEndObject();
    }
}

/// <summary>Table rows: an array of rows, <c>{ "$data": "slot" }</c>, or <c>{ "repeat": "lines", "cells": [...] }</c>.</summary>
internal sealed class TableRowsJsonConverter : JsonConverter<TableRows>
{
    private const string Expected = "Expected rows: an array of rows, { \"$data\": \"slot\" }, or { \"repeat\": \"lines\", \"cells\": [...] }.";

    public override TableRows Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            return new InlineTableRows(PdfJsonException.Read<ImmutableArray<ImmutableArray<Cell>>>(ref reader, options, ""));
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw PdfJsonException.Invalid(Expected);
        }

        string? slot = null, items = null;
        ImmutableArray<Cell>? cells = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString()!;
            reader.Read();
            switch (name)
            {
                case "$data" or "repeat":
                    string text = reader.TokenType == JsonTokenType.String ? reader.GetString()! : throw new PdfJsonException(DiagnosticCodes.InvalidJson, $"\"{name}\" must be a string.") { Suffix = "/" + name };
                    (slot, items) = name == "$data" ? (text, items) : (slot, text);
                    break;
                case "cells":
                    cells = PdfJsonException.Read<ImmutableArray<Cell>>(ref reader, options, "cells");
                    break;
                default:
                    throw PdfJsonException.UnknownProperty(name, "$data, or repeat and cells");
            }
        }

        return (slot, items, cells) switch
        {
            ({ } found, null, null) => new BoundTableRows(found),
            (null, { } path, { } template) => new RepeatedTableRows(path, template),
            (null, { }, null) => throw new PdfJsonException(DiagnosticCodes.MissingProperty, "Repeated rows need \"cells\": the cells of each row.") { Suffix = "/cells" },
            (null, null, { }) => throw new PdfJsonException(DiagnosticCodes.MissingProperty, "Repeated rows need \"repeat\": the path of an array in the model.") { Suffix = "/repeat" },
            (null, null, null) => throw new PdfJsonException(DiagnosticCodes.MissingProperty, "A data slot needs \"$data\"."),
            _ => throw PdfJsonException.Invalid("Rows come from a slot (\"$data\") or a repeat, not both."),
        };
    }

    public override void Write(Utf8JsonWriter writer, TableRows value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case InlineTableRows rows:
                JsonSerializer.Serialize(writer, rows.Rows, (JsonTypeInfo<ImmutableArray<ImmutableArray<Cell>>>)options.GetTypeInfo(typeof(ImmutableArray<ImmutableArray<Cell>>)));
                break;
            case BoundTableRows bound:
                SlotJson.Write(writer, bound.Slot);
                break;
            case RepeatedTableRows repeated:
                writer.WriteStartObject();
                writer.WriteString("repeat", repeated.Items);
                writer.WritePropertyName("cells");
                JsonSerializer.Serialize(writer, repeated.Cells, (JsonTypeInfo<ImmutableArray<Cell>>)options.GetTypeInfo(typeof(ImmutableArray<Cell>)));
                writer.WriteEndObject();
                break;
            default:
                throw new NotSupportedException($"Row source {value?.GetType().Name ?? "null"} can't be written as JSON.");
        }
    }
}

/// <summary>Image sources: <c>{ "base64": "..." }</c> or <c>{ "$data": "slot" }</c>.</summary>
internal sealed class ImageSourceJsonConverter : JsonConverter<ImageSource>
{
    public override ImageSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw PdfJsonException.Invalid("Expected an image source: { \"base64\": \"...\" } or { \"$data\": \"slot\" }.");
        }

        ImageSource? source = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString()!;
            reader.Read();
            source = name switch
            {
                "base64" when reader.TokenType == JsonTokenType.String && reader.TryGetBytesFromBase64(out byte[]? bytes) => new BytesImageSource(bytes),
                "base64" => throw PdfJsonException.Invalid("\"base64\" must be a base64 string."),
                "$data" when reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 } slot => new SlotImageSource(slot),
                "$data" => throw PdfJsonException.Invalid("\"$data\" must be a slot name."),
                _ => throw PdfJsonException.UnknownProperty(name, "base64 or $data"),
            };
        }

        return source ?? throw new PdfJsonException(DiagnosticCodes.MissingProperty, "An image source needs \"base64\" or \"$data\".");
    }

    public override void Write(Utf8JsonWriter writer, ImageSource value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case BytesImageSource bytes:
                writer.WriteBase64String("base64", bytes.Bytes.Span);
                break;
            case SlotImageSource slot:
                writer.WriteString("$data", slot.Slot);
                break;
            default:
                throw new NotSupportedException($"Image source {value?.GetType().Name ?? "null"} can't be written as JSON.");
        }

        writer.WriteEndObject();
    }
}

/// <summary><c>{ "$data": "slot" }</c>: a value supplied when rendering.</summary>
internal static class SlotJson
{
    public static string Read(ref Utf8JsonReader reader, string what)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw PdfJsonException.Invalid($"Expected {what}: an array, or {{ \"$data\": \"slot\" }}.");
        }

        string? slot = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString()!;
            reader.Read();
            slot = name == "$data"
                ? reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 } found ? found : throw PdfJsonException.Invalid("\"$data\" must be a slot name.")
                : throw PdfJsonException.UnknownProperty(name, "$data");
        }

        return slot ?? throw new PdfJsonException(DiagnosticCodes.MissingProperty, "A data slot needs \"$data\".");
    }

    public static void Write(Utf8JsonWriter writer, string slot)
    {
        writer.WriteStartObject();
        writer.WriteString("$data", slot);
        writer.WriteEndObject();
    }
}
