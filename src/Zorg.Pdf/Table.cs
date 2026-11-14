using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>
/// A table with a header row of column titles. Rows never split across pages; on a new page the header row repeats.
/// Rows are either in the document (<see cref="TableRows.Inline(ImmutableArray{ImmutableArray{Cell}})"/>) or bound to
/// a data slot filled at render time (<see cref="TableRows.Bound(string)"/>), so a million rows never sit in memory.
/// </summary>
/// <example>
/// <code>
/// var lines = new Table([new Column("Description") { Width = ColumnWidth.Star(5) }, new Column("Amount") { Align = TextAlign.Right }])
/// {
///     Rows = TableRows.Bound("lines"),
/// };
/// </code>
/// </example>
public sealed record Table : Block
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal Table(ImmutableArray<Column> columns, TableRows? rows = null, bool repeatHeader = true, string? style = null, bool keepWithNext = false) =>
        (Columns, Rows, RepeatHeader, Style, KeepWithNext) = (columns, rows ?? TableRows.Empty, repeatHeader, style, keepWithNext);

    /// <summary>A table with the given columns.</summary>
    /// <param name="columns">The columns, left to right.</param>
    [SetsRequiredMembers]
    public Table(ImmutableArray<Column> columns) => Columns = columns;

    /// <summary>The columns, left to right. Their headers make the header row.</summary>
    [JsonRequired]
    public required ImmutableArray<Column> Columns { get; init; }

    /// <summary>The body rows.</summary>
    public TableRows Rows { get; init; } = TableRows.Empty;

    /// <summary>Repeats the header row at the top of every page the table continues on. On by default.</summary>
    public bool RepeatHeader { get; init; } = true;
}

/// <summary>A table column.</summary>
/// <example>
/// <code>
/// var amount = new Column("Amount") { Width = ColumnWidth.Fixed(Length.Cm(3)), Align = TextAlign.Right };
/// </code>
/// </example>
public sealed record Column
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal Column(string header, ColumnWidth width = default, TextAlign align = TextAlign.Left) =>
        (Header, Width, Align) = (header, width == default ? ColumnWidth.Star() : width, align); // A fixed width of 0 can't come from JSON.

    /// <summary>A column with a header.</summary>
    /// <param name="header">The header text.</param>
    [SetsRequiredMembers]
    public Column(string header) => Header = header;

    /// <summary>The header text, tagged TH with column scope.</summary>
    [JsonRequired]
    public required string Header { get; init; }

    /// <summary>The width. A share of the free width (<c>"1*"</c>) by default.</summary>
    public ColumnWidth Width { get; init; } = ColumnWidth.Star();

    /// <summary>Alignment of the header and the cells in the column.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TextAlign Align { get; init; }
}

/// <summary>
/// A column width: a fixed length, or a share of what the fixed columns leave. In JSON <c>"2*"</c> (two shares),
/// <c>"*"</c> (one share), or a length such as <c>"3cm"</c>.
/// </summary>
[JsonConverter(typeof(ColumnWidthJsonConverter))]
public readonly record struct ColumnWidth
{
    private ColumnWidth(bool star, double value) => (IsStar, Value) = (star, value);

    /// <summary>True for a share of the free width; false for a fixed length.</summary>
    public bool IsStar { get; }

    /// <summary>The number of shares, or the fixed length in points.</summary>
    public double Value { get; }

    /// <summary>A share of the width the fixed columns leave.</summary>
    /// <param name="share">Shares, more than 0.</param>
    /// <returns>The width.</returns>
    public static ColumnWidth Star(double share = 1) => new(true, share);

    /// <summary>A fixed width.</summary>
    /// <param name="length">The width.</param>
    /// <returns>The width.</returns>
    public static ColumnWidth Fixed(Length length) => new(false, length.Points);
}

/// <summary>
/// A table cell. A string converts to a cell. In JSON a cell is a string, or <c>{ "inlines": [...], "align": "right" }</c>.
/// </summary>
[JsonConverter(typeof(CellJsonConverter))]
public sealed record Cell
{
    /// <summary>An empty cell, for deserialisation and object initialisers.</summary>
    public Cell()
    {
    }

    /// <summary>A cell of plain text.</summary>
    /// <param name="text">The text.</param>
    [SetsRequiredMembers]
    public Cell(string text) => Inlines = [new TextRun(text)];

    /// <summary>The text.</summary>
    public required ImmutableArray<Inline> Inlines { get; init; }

    /// <summary>Alignment, overriding the column's.</summary>
    public TextAlign? Align { get; init; }

    /// <summary>A cell of plain text.</summary>
    /// <param name="text">The text.</param>
    public static implicit operator Cell(string text) => new(text);

    /// <summary>A cell of plain text, for languages that can't use the implicit conversion.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The cell.</returns>
    public static Cell FromString(string text) => new(text);
}

/// <summary>
/// Where a table's rows come from. In JSON an array of rows (each an array of cells), or <c>{ "$data": "slot" }</c> for
/// rows supplied when rendering.
/// </summary>
[JsonConverter(typeof(TableRowsJsonConverter))]
public abstract record TableRows
{
    /// <summary>No rows: only the header.</summary>
    public static TableRows Empty { get; } = new InlineTableRows([]);

    /// <summary>Rows written in the document.</summary>
    /// <param name="rows">The rows, each with one cell per column.</param>
    /// <returns>The rows.</returns>
    public static TableRows Inline(ImmutableArray<ImmutableArray<Cell>> rows) => new InlineTableRows(rows);

    /// <summary>Rows supplied when rendering, under a slot name.</summary>
    /// <param name="slot">The slot name.</param>
    /// <returns>The rows.</returns>
    public static TableRows Bound(string slot) => new BoundTableRows(slot);
}

/// <summary>Rows written in the document.</summary>
/// <param name="Rows">The rows, each with one cell per column.</param>
public sealed record InlineTableRows(ImmutableArray<ImmutableArray<Cell>> Rows) : TableRows;

/// <summary>Rows supplied when rendering, under a slot name.</summary>
/// <param name="Slot">The slot name.</param>
public sealed record BoundTableRows(string Slot) : TableRows;
