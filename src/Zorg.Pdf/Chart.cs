using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>
/// A chart, drawn as vectors: bars side by side or stacked, lines, or a pie. Bar columns stand up by default;
/// <see cref="Horizontal"/> lays them down, which suits long category names. Values come from the document
/// or, with <see cref="Bind"/>, from the template model. Like an image it needs <see cref="Alt"/> text that says what
/// the chart shows, unless it's <see cref="Decorative"/>.
/// In JSON <c>{ "type": "chart", "kind": "bar", "categories": ["Q1", "Q2"], "series": [{ "name": "2026", "values": [12, 15] }], "alt": "…" }</c>.
/// </summary>
/// <example>
/// <code>
/// var sales = new Chart(ChartKind.Bar, ["Q1", "Q2", "Q3", "Q4"], [new ChartSeries("2025", [12, 15, 14, 18]), new ChartSeries("2026", [14, 17, 19, 22])])
/// {
///     Title = "Sales, MNOK", ValueFormat = "N0", Alt = "Sales rose every quarter in 2026, from 14 to 22 million kroner, above 2025's.",
/// };
/// </code>
/// </example>
public sealed record Chart : Block
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal Chart(ChartKind kind = ChartKind.Bar, ImmutableArray<string> categories = default, ImmutableArray<ChartSeries> series = default, string? bind = null, string? title = null, bool horizontal = false, Length? width = null, Length? height = null, bool? legend = null, string? valueFormat = null, double? minimum = null, double? maximum = null, string? alt = null, bool decorative = false, string? style = null, bool keepWithNext = false, string? id = null) =>
        (Kind, Categories, Series, Bind, Title, Horizontal, Width, Height, Legend, ValueFormat, Minimum, Maximum, Alt, Decorative, Style, KeepWithNext, Id) =
        (kind, categories.IsDefault ? [] : categories, series.IsDefault ? [] : series, bind, title, horizontal, width, height, legend, valueFormat, minimum, maximum, alt, decorative, style, keepWithNext, id);

    /// <summary>A chart of the categories' values in each series.</summary>
    /// <param name="kind">Bars side by side, or stacked.</param>
    /// <param name="categories">The labels along the category axis.</param>
    /// <param name="series">The series, each with one value per category.</param>
    [SetsRequiredMembers]
    public Chart(ChartKind kind, ImmutableArray<string> categories, ImmutableArray<ChartSeries> series) =>
        (Kind, Categories, Series) = (kind, categories, series);

    /// <summary>Bars side by side, or stacked.</summary>
    public required ChartKind Kind { get; init; }

    /// <summary>The labels along the category axis, in order.</summary>
    public ImmutableArray<string> Categories { get; init; } = [];

    /// <summary>The series; each has a value for every category.</summary>
    public ImmutableArray<ChartSeries> Series { get; init; } = [];

    /// <summary>
    /// A path in the template model to an object holding <c>categories</c> (strings) and <c>series</c> (objects with
    /// <c>name</c> and <c>values</c>), in place of <see cref="Categories"/> and <see cref="Series"/>.
    /// </summary>
    public string? Bind { get; init; }

    /// <summary>A title above the chart. Null: none.</summary>
    public string? Title { get; init; }

    /// <summary>Bars lying down, categories down the left. Default false: columns standing up.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Horizontal { get; init; }

    /// <summary>The width. Null: the column's width.</summary>
    public Length? Width { get; init; }

    /// <summary>The height, title and legend included. Null: 7 cm.</summary>
    public Length? Height { get; init; }

    /// <summary>Whether a legend names the series under the chart. Null: when there's more than one series.</summary>
    public bool? Legend { get; init; }

    /// <summary>A .NET number format for the value axis, such as "N0", "P0" or "0.0"; in the document's language. Null: general.</summary>
    public string? ValueFormat { get; init; }

    /// <summary>The value axis's lowest value. Null: 0, or lower to fit negative values.</summary>
    public double? Minimum { get; init; }

    /// <summary>The value axis's highest value. Null: fitted to the data, rounded up to a tick.</summary>
    public double? Maximum { get; init; }

    /// <summary>What the chart shows, for screen readers: its point, not every number. Required unless <see cref="Decorative"/>.</summary>
    public string? Alt { get; init; }

    /// <summary>True when the chart adds nothing the text doesn't say: it's then an artifact screen readers skip.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Decorative { get; init; }
}

/// <summary>A series of a <see cref="Chart"/>: a name for the legend and one value per category.</summary>
/// <param name="Name">The name, shown in the legend.</param>
/// <param name="Values">One value per category.</param>
public sealed record ChartSeries(string Name, ImmutableArray<double> Values)
{
    /// <summary>The bars' colour. Null: the next from a palette people with colour blindness can tell apart.</summary>
    public Color? Color { get; init; }
}

/// <summary>A chart's kind. In JSON <c>"bar"</c>, <c>"stackedBar"</c>, <c>"line"</c> or <c>"pie"</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ChartKind>))]
public enum ChartKind
{
    /// <summary>Each series' bar beside the others in its category.</summary>
    Bar,

    /// <summary>Each category's bars stacked into one; values must not be negative.</summary>
    StackedBar,

    /// <summary>A line through each series' values, with a marker of its own shape at every category.</summary>
    Line,

    /// <summary>One series' values as slices of a circle, clockwise from the top; values must not be negative.</summary>
    Pie,
}
