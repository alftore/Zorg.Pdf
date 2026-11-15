using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Zorg.Pdf.Validation;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// Turns a template into a plain document before anything else runs: <see cref="Bind"/> inlines become text from the
/// model, <see cref="Repeat"/> blocks are expanded once per item and <see cref="Conditional"/> blocks kept or dropped.
/// Everything after (preflight, fonts, layout) sees an ordinary document. Missing values are warnings (ZPDF-B001,
/// B002) at the template's path; numbers and dates are formatted in the culture of the document's language.
/// </summary>
internal sealed class TemplateBinder
{
    private readonly JsonElement? _model;
    private readonly CultureInfo _culture;
    private readonly DiagnosticSink _sink;
    private readonly List<Scope> _scopes = [];
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    private TemplateBinder(JsonElement? model, CultureInfo culture, DiagnosticSink sink) => (_model, _culture, _sink) = (model, culture, sink);

    /// <summary>True when the document uses any template feature, so plain documents skip binding.</summary>
    public static bool IsTemplate(PdfDocument document) =>
        Blocks(document).Any(b => b is Repeat or Conditional or Table { Rows: RepeatedTableRows } or Barcode { Bind: not null } or Chart { Bind: not null } or SwissQrBill { Bind: not null } || Inlines(b).Any(HasBind) || (b is Table { Rows: InlineTableRows { Rows.IsDefault: false } rows } && rows.Rows.Any(r => !r.IsDefault && r.Any(c => c is not null && !c.Inlines.IsDefault && c.Inlines.Any(i => i is Bind)))));

    public static PdfDocument Bind(PdfDocument template, JsonElement? model, DiagnosticSink sink)
    {
        var binder = new TemplateBinder(model, Culture(template.Language), sink);
        var content = new List<Block>();
        for (int i = 0; i < (template.Content.IsDefault ? 0 : template.Content.Length); i++)
        {
            binder.Expand(template.Content[i], $"/content/{i}", content);
        }

        PdfDocument bound = template with
        {
            Content = [.. content],
            Header = binder.Band(template.Header, "/header"),
            Footer = binder.Band(template.Footer, "/footer"),
        };
        return bound;
    }

    private static CultureInfo Culture(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private PageBand? Band(PageBand? band, string path)
    {
        if (band is null || band.Content.IsDefault)
        {
            return band;
        }

        var content = new List<Block>();
        for (int i = 0; i < band.Content.Length; i++)
        {
            Expand(band.Content[i], $"{path}/content/{i}", content);
        }

        return band with { Content = [.. content] };
    }

    /// <summary>Adds the block to <paramref name="output"/>, expanded and bound.</summary>
    private void Expand(Block block, string path, List<Block> output)
    {
        switch (block)
        {
            case Repeat repeat:
                JsonElement? items = Resolve(repeat.Items, $"{path}/items", out bool found);
                if (items is not { ValueKind: JsonValueKind.Array } array)
                {
                    if (found && items is { ValueKind: not JsonValueKind.Null })
                    {
                        Report(DiagnosticCodes.RepeatNotArray, $"\"{repeat.Items}\" is a {Kind(items.Value)}, not an array, so the repeated content is left out.", $"{path}/items");
                    }

                    return;
                }

                int index = 0;
                foreach (JsonElement item in array.EnumerateArray())
                {
                    _scopes.Add(new Scope(item, index++));
                    foreach ((Block inner, int b) in repeat.Content.IsDefault ? [] : repeat.Content.Select((x, b) => (x, b)))
                    {
                        Expand(inner, $"{path}/content/{b}", output);
                    }

                    _scopes.RemoveAt(_scopes.Count - 1);
                }

                return;

            case Conditional conditional:
                JsonElement? value = Resolve(conditional.Test, $"{path}/test", out _, warn: false);
                if (Truthy(value) != conditional.Not)
                {
                    for (int b = 0; b < (conditional.Content.IsDefault ? 0 : conditional.Content.Length); b++)
                    {
                        Expand(conditional.Content[b], $"{path}/content/{b}", output);
                    }
                }

                return;

            case Heading heading:
                output.Add(heading with { Inlines = BindInlines(heading.Inlines, $"{path}/inlines") });
                return;

            case Paragraph paragraph:
                output.Add(paragraph with { Inlines = BindInlines(paragraph.Inlines, $"{path}/inlines") });
                return;

            case List list when !list.Items.IsDefault:
                var listItems = new List<ListItem>();
                for (int i = 0; i < list.Items.Length; i++)
                {
                    ListItem item = list.Items[i];
                    if (item?.Content is [Repeat repeat])
                    {
                        // An item that is only a repeat becomes one item per array element: how a list repeats.
                        ExpandItems(repeat, $"{path}/items/{i}/content/0", listItems);
                    }
                    else
                    {
                        listItems.Add(item is null || item.Content.IsDefault ? item! : new ListItem(ExpandAll(item.Content, $"{path}/items/{i}/content")));
                    }
                }

                if (listItems.Count > 0)
                {
                    output.Add(list with { Items = [.. listItems] }); // A list whose repeat found nothing is left out.
                }

                return;

            case Chart { Bind: { } chartPath } chart:
                output.Add(BindChart(chart, chartPath, path));
                return;

            case SwissQrBill { Bind: { } billPath } bill:
                output.Add(BindBill(bill, billPath, path));
                return;

            case Barcode { Bind: { } bindPath } barcode:
                output.Add(barcode with { Data = Text(new Bind(bindPath), path), Bind = null });
                return;

            case SectionBreak section:
                output.Add(section with { Header = Band(section.Header, $"{path}/header"), Footer = Band(section.Footer, $"{path}/footer") });
                return;

            case TextBox box when !box.Content.IsDefault:
                output.Add(box with { Content = ExpandAll(box.Content, $"{path}/content") });
                return;

            case Table { Rows: RepeatedTableRows repeated } table:
                output.Add(table with { Rows = TableRows.Inline(RepeatRows(repeated, $"{path}/rows")) });
                return;

            case Table { Rows: InlineTableRows { Rows.IsDefault: false } rows } table:
                output.Add(table with
                {
                    Rows = TableRows.Inline([.. rows.Rows.Select((row, r) => row.IsDefault ? row : [.. row.Select((cell, c) => cell is null || cell.Inlines.IsDefault ? cell! : cell with { Inlines = BindInlines(cell.Inlines, $"{path}/rows/{r}/{c}/inlines") })])]),
                });
                return;

            default:
                output.Add(block);
                return;
        }
    }

    /// <summary>One list item per element of the repeat's array, each holding the repeat's content.</summary>
    private void ExpandItems(Repeat repeat, string path, List<ListItem> items)
    {
        JsonElement? found = Resolve(repeat.Items, $"{path}/items", out bool exists);
        if (found is not { ValueKind: JsonValueKind.Array } array)
        {
            if (exists && found is { ValueKind: not JsonValueKind.Null })
            {
                Report(DiagnosticCodes.RepeatNotArray, $"\"{repeat.Items}\" is a {Kind(found.Value)}, not an array, so the repeated items are left out.", $"{path}/items");
            }

            return;
        }

        int index = 0;
        foreach (JsonElement element in array.EnumerateArray())
        {
            _scopes.Add(new Scope(element, index++));
            items.Add(new ListItem(ExpandAll(repeat.Content.IsDefault ? [] : repeat.Content, $"{path}/content")));
            _scopes.RemoveAt(_scopes.Count - 1);
        }
    }

    /// <summary>One row per element of the array, each made from the row's cells with the element as scope.</summary>
    private ImmutableArray<ImmutableArray<Cell>> RepeatRows(RepeatedTableRows repeated, string path)
    {
        JsonElement? found = Resolve(repeated.Items, $"{path}/repeat", out bool exists);
        if (found is not { ValueKind: JsonValueKind.Array } array)
        {
            if (exists && found is { ValueKind: not JsonValueKind.Null })
            {
                Report(DiagnosticCodes.RepeatNotArray, $"\"{repeated.Items}\" is a {Kind(found.Value)}, not an array, so the table has no rows.", $"{path}/repeat");
            }

            return [];
        }

        ImmutableArray<Cell> cells = repeated.Cells.IsDefault ? [] : repeated.Cells;
        var rows = ImmutableArray.CreateBuilder<ImmutableArray<Cell>>(array.GetArrayLength());
        int index = 0;
        foreach (JsonElement element in array.EnumerateArray())
        {
            _scopes.Add(new Scope(element, index++));
            rows.Add([.. cells.Select((cell, c) => cell is null || cell.Inlines.IsDefault ? cell! : cell with { Inlines = BindInlines(cell.Inlines, $"{path}/cells/{c}/inlines") })]);
            _scopes.RemoveAt(_scopes.Count - 1);
        }

        return rows.MoveToImmutable();
    }

    private ImmutableArray<Block> ExpandAll(ImmutableArray<Block> blocks, string path)
    {
        var output = new List<Block>();
        for (int b = 0; b < blocks.Length; b++)
        {
            Expand(blocks[b], $"{path}/{b}", output);
        }

        return [.. output];
    }

    private ImmutableArray<Inline> BindInlines(ImmutableArray<Inline> inlines, string path)
    {
        if (inlines.IsDefault || !inlines.Any(HasBind))
        {
            return inlines;
        }

        var bound = ImmutableArray.CreateBuilder<Inline>(inlines.Length);
        for (int i = 0; i < inlines.Length; i++)
        {
            bound.Add(inlines[i] switch
            {
                Bind bind => new TextRun(Text(bind, $"{path}/{i}")) { Style = bind.Style },
                Link link when !link.Inlines.IsDefault => link with { Inlines = BindInlines(link.Inlines, $"{path}/{i}/inlines") },
                Inline other => other,
            });
        }

        return bound.MoveToImmutable();
    }

    /// <summary>A bound value as text: formatted when it's a number or date and a format is given.</summary>
    private string Text(Bind bind, string path)
    {
        JsonElement? value = Resolve(bind.Path, $"{path}/bind", out _);
        if (value is not { } element)
        {
            return "";
        }

        try
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Number when element.TryGetDecimal(out decimal number):
                    return number.ToString(bind.Format, _culture);
                case JsonValueKind.Number:
                    return element.GetDouble().ToString(bind.Format, _culture);
                case JsonValueKind.String when bind.Format is { } format && element.TryGetDateTimeOffset(out DateTimeOffset date):
                    return date.ToString(format, _culture);
                case JsonValueKind.String:
                    return element.GetString()!;
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                case JsonValueKind.Null:
                    return "";
                default:
                    Report(DiagnosticCodes.BindingMissing, $"\"{bind.Path}\" is a {Kind(element)}, which can't be shown as text; bind one of its values instead.", $"{path}/bind");
                    return "";
            }
        }
        catch (FormatException)
        {
            Report(DiagnosticCodes.BindingMissing, $"\"{bind.Format}\" isn't a format string for \"{bind.Path}\"; the value is shown unformatted.", $"{path}/format");
            return element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText();
        }
    }

    /// <summary>
    /// The value at <paramref name="path"/>: its first name looked up in the current repetition's item, then outwards
    /// to the model; the rest followed down. <c>@index</c> and <c>@number</c> give the repetition's position.
    /// </summary>
    private JsonElement? Resolve(string path, string pointer, out bool found, bool warn = true)
    {
        found = false;
        string[] segments = path.Split('.', StringSplitOptions.TrimEntries);
        if (segments.Length == 1 && segments[0] is "@index" or "@number" && _scopes.Count > 0)
        {
            found = true;
            int index = _scopes[^1].Index + (segments[0] == "@number" ? 1 : 0);
            using JsonDocument number = JsonDocument.Parse(index.ToString(CultureInfo.InvariantCulture));
            return number.RootElement.Clone();
        }

        JsonElement? current = null;
        for (int s = _scopes.Count - 1; s >= -1 && current is null; s--)
        {
            JsonElement? scope = s >= 0 ? _scopes[s].Item : _model;
            if (scope is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(segments[0], out JsonElement first))
            {
                current = first;
            }
            else if (s >= 0 && segments[0] == "this")
            {
                current = scope;
            }
        }

        for (int i = 1; current is not null && i < segments.Length; i++)
        {
            JsonElement value = current.Value;
            current = value.ValueKind switch
            {
                JsonValueKind.Object when value.TryGetProperty(segments[i], out JsonElement child) => child,
                JsonValueKind.Array when int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < value.GetArrayLength() => value[index],
                _ => null,
            };
        }

        found = current is not null;
        if (!found && warn)
        {
            Report(DiagnosticCodes.BindingMissing, _model is null
                ? $"The document binds \"{path}\", but the render has no model. Supply one: RenderOptions.Data = PdfData.Empty.WithModel(json), or a data object in PdfData.ParseJson."
                : $"The model has no value at \"{path}\", so nothing is shown.", pointer);
        }

        return current;
    }

    private static bool Truthy(JsonElement? value) => value is { } v && v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => false,
        JsonValueKind.String => v.GetString()!.Length > 0,
        JsonValueKind.Number => v.GetDouble() != 0,
        JsonValueKind.Array => v.GetArrayLength() > 0,
        _ => true,
    };

    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "true/false value",
        _ => "null",
    };

    /// <summary>Reports a problem once per template path, however many repetitions hit it.</summary>
    /// <summary>A chart's categories and series from <c>{ "categories": [...], "series": [{ "name": ..., "values": [...] }] }</c> in the model.</summary>
    private Chart BindChart(Chart chart, string bindPath, string path)
    {
        var empty = chart with { Bind = null, Categories = [], Series = [] };
        if (Resolve(bindPath, $"{path}/bind", out _) is not { ValueKind: JsonValueKind.Object } value)
        {
            Report(DiagnosticCodes.BindingMissing, $"\"{bindPath}\" isn't an object with \"categories\" and \"series\"; the chart is left out.", $"{path}/bind");
            return empty;
        }

        var categories = ImmutableArray.CreateBuilder<string>();
        var series = ImmutableArray.CreateBuilder<ChartSeries>();
        if (value.TryGetProperty("categories", out JsonElement c) && c.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in c.EnumerateArray())
            {
                categories.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText());
            }
        }

        if (value.TryGetProperty("series", out JsonElement s) && s.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in s.EnumerateArray())
            {
                string name = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
                var values = ImmutableArray.CreateBuilder<double>();
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("values", out JsonElement v) && v.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement number in v.EnumerateArray())
                    {
                        values.Add(number.ValueKind == JsonValueKind.Number ? number.GetDouble() : double.NaN);
                    }
                }

                series.Add(new ChartSeries(name, values.ToImmutable()));
            }
        }

        Chart bound = chart with { Bind = null, Categories = categories.ToImmutable(), Series = series.ToImmutable() };
        bool bad = false;
        Validation.DocumentValidator.ChartData(bound, $"{path}/bind", (code, message, at) =>
        {
            bad = true;
            _sink.Error(code, message, at);
        });
        return bad ? empty : bound;
    }

    /// <summary>A QR-bill with the model's fields filled in, then checked against the guidelines.</summary>
    private SwissQrBill BindBill(SwissQrBill bill, string bindPath, string path)
    {
        SwissQrBill bound = bill with { Bind = null };
        if (Resolve(bindPath, $"{path}/bind", out _) is not { ValueKind: JsonValueKind.Object } value)
        {
            Report(DiagnosticCodes.BindingMissing, $"\"{bindPath}\" isn't an object with the bill's fields; the bill keeps the ones it has.", $"{path}/bind");
        }
        else
        {
            static string? Text(JsonElement o, string name, string? current) =>
                o.TryGetProperty(name, out JsonElement e) ? e.ValueKind switch { JsonValueKind.String => e.GetString(), JsonValueKind.Null => null, _ => e.GetRawText() } : current;

            SwissQrAddress? Address(JsonElement o, string name, SwissQrAddress? current)
            {
                if (!o.TryGetProperty(name, out JsonElement a))
                {
                    return current;
                }

                if (a.ValueKind == JsonValueKind.Null)
                {
                    return null;
                }

                if (a.ValueKind != JsonValueKind.Object)
                {
                    _sink.Error(DiagnosticCodes.BadSwissQrBill, $"The bound \"{name}\" must be an object with name, postalCode and town, not {a.ValueKind switch { JsonValueKind.String => "a string", JsonValueKind.Number => "a number", JsonValueKind.Array => "an array", _ => "true or false" }}.", $"{path}/bind");
                    return current;
                }

                SwissQrAddress start = current ?? new SwissQrAddress("", "", "");
                return new SwissQrAddress(Text(a, "name", start.Name) ?? "", Text(a, "postalCode", start.PostalCode) ?? "", Text(a, "town", start.Town) ?? "")
                {
                    Street = Text(a, "street", start.Street),
                    HouseNumber = Text(a, "houseNumber", start.HouseNumber),
                    Country = Text(a, "country", start.Country) ?? "CH",
                };
            }

            decimal? amount = bound.Amount;
            if (value.TryGetProperty("amount", out JsonElement number))
            {
                // A decimal point only: "793,00" isn't 79 300, and an amount that can't be read isn't a blank one.
                const NumberStyles Plain = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
                if (number.ValueKind == JsonValueKind.Null)
                {
                    amount = null;
                }
                else if (number.ValueKind == JsonValueKind.Number && number.TryGetDecimal(out decimal d))
                {
                    amount = d;
                }
                else if (number.ValueKind == JsonValueKind.String && decimal.TryParse(number.GetString(), Plain, CultureInfo.InvariantCulture, out decimal parsed))
                {
                    amount = parsed;
                }
                else
                {
                    _sink.Error(DiagnosticCodes.BadSwissQrBill, $"The bound amount {number.GetRawText()} isn't a number like 793.00 (a decimal point, no thousands separators or currency).", $"{path}/bind");
                }
            }

            bound = bound with
            {
                Account = Text(value, "account", bound.Account) ?? "",
                Creditor = Address(value, "creditor", bound.Creditor)!,
                Amount = amount,
                Currency = Text(value, "currency", bound.Currency) ?? "CHF",
                Debtor = Address(value, "debtor", bound.Debtor),
                Reference = Text(value, "reference", bound.Reference),
                Message = Text(value, "message", bound.Message),
                BillInformation = Text(value, "billInformation", bound.BillInformation),
                Language = Text(value, "language", bound.Language),
            };
        }

        foreach ((string message, string at) in SwissQrRules.Check(bound))
        {
            _sink.Error(DiagnosticCodes.BadSwissQrBill, message, $"{path}{at}");
        }

        return bound;
    }

    private void Report(string code, string message, string path)
    {
        if (_reported.Add(code + path))
        {
            _sink.Warning(code, message, path);
        }
    }

    private static IEnumerable<Block> Blocks(PdfDocument document)
    {
        IEnumerable<Block> all = document.Content.IsDefault ? [] : document.Content;
        all = all.Concat(document.Header?.Content.IsDefault == false ? document.Header.Content : []);
        all = all.Concat(document.Footer?.Content.IsDefault == false ? document.Footer.Content : []);
        foreach (SectionBreak section in document.Content.IsDefault ? [] : document.Content.OfType<SectionBreak>())
        {
            all = all.Concat(section.Header?.Content.IsDefault == false ? section.Header.Content : []);
            all = all.Concat(section.Footer?.Content.IsDefault == false ? section.Footer.Content : []);
        }
        foreach (Block block in all)
        {
            foreach (Block inner in Walk(block))
            {
                yield return inner;
            }
        }
    }

    private static IEnumerable<Block> Walk(Block? block)
    {
        if (block is null)
        {
            yield break;
        }

        yield return block;
        IEnumerable<Block> children = block switch
        {
            Repeat r when !r.Content.IsDefault => r.Content,
            Conditional c when !c.Content.IsDefault => c.Content,
            List l when !l.Items.IsDefault => l.Items.Where(i => i is not null && !i.Content.IsDefault).SelectMany(i => i.Content),
            TextBox t when !t.Content.IsDefault => t.Content,
            _ => [],
        };
        foreach (Block child in children)
        {
            foreach (Block inner in Walk(child))
            {
                yield return inner;
            }
        }
    }

    private static bool HasBind(Inline inline) => inline is Bind || (inline is Link { Inlines.IsDefault: false } link && link.Inlines.Any(HasBind));

    private static ImmutableArray<Inline> Inlines(Block block) => block switch
    {
        Heading { Inlines.IsDefault: false } h => h.Inlines,
        Paragraph { Inlines.IsDefault: false } p => p.Inlines,
        _ => [],
    };

    /// <summary>A repetition: its item and its position.</summary>
    private readonly record struct Scope(JsonElement Item, int Index);
}
