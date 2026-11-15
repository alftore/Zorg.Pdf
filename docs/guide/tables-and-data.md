# Tables and data

A table has columns and rows. Columns are star-sized (sharing the free width) or fixed. The header row repeats on every page, and rows split across pages when they must. Tables are tagged with TH and TD cells, so screen readers announce each cell with its column header.

```csharp
var lines = new Table(
[
    new Column("Description") { Width = ColumnWidth.Star(5) },
    new Column("Hours") { Width = ColumnWidth.Fixed(Length.Cm(2)), Align = TextAlign.Right },
    new Column("Amount") { Width = ColumnWidth.Fixed(Length.Cm(3)), Align = TextAlign.Right },
])
{
    Rows = TableRows.Inline(
    [
        ["Consulting", "7.5", "9 375.00"],
        ["Travel", "", "1 200.00"],
        [new Cell("Total") { ColumnSpan = 2, Align = TextAlign.Right }, new Cell([new TextRun("10 575.00") { Style = TextStyle.Bold }])],
    ]),
};
```

```json
{
  "type": "table",
  "columns": [
    { "header": "Description", "width": "5*" },
    { "header": "Hours", "width": "2cm", "align": "right" },
    { "header": "Amount", "width": "3cm", "align": "right" }
  ],
  "rows": [
    ["Consulting", "7.5", "9 375.00"],
    ["Travel", "", "1 200.00"],
    [{ "inlines": ["Total"], "columnSpan": 2, "align": "right" }, { "inlines": [{ "text": "10 575.00", "style": { "weight": "bold" } }] }]
  ]
}
```

Cells can also span rows (`RowSpan`). The look (header fill, rules, padding, stripes) comes from `TableStyle`, described in [design](design.md).

## Rows from data

For long tables, don't build the rows up front: name a slot with `TableRows.Bound` and supply the rows when rendering. Rows can come from an `IAsyncEnumerable`, such as a database query, and are read as the table is laid out. Pages stream to the output as they finish, so a 100,000-row report never sits in memory.

```csharp
var invoice = new PdfDocument("Invoice")
{
    Content = [new Table([new Column("Description"), new Column("Amount") { Align = TextAlign.Right }]) { Rows = TableRows.Bound("lines") }],
};
var data = PdfData.Empty.WithRows("lines", linesAsync, line => [line.Description, line.Amount.ToString("N2")]);
await invoice.RenderAsync(output, new RenderOptions { Fonts = fonts, Data = data }, cancellationToken);
```

In JSON the slot is `"rows": { "$data": "lines" }`. The command line and `PdfData.ParseJson` read the rows from a data file:

```json
{ "lines": [["Consulting", "9 375.00"], ["Travel", "1 200.00"]] }
```

`PdfLimits.MaxTableRows` bounds how many rows a table takes. A data row with the wrong number of cells is warning ZPDF-L002, and a row taller than a page is ZPDF-L001.

## Rows from a template model

When the data is a JSON model rather than ready-made rows, `TableRows.Repeat` makes one row per array item, with bindings for the cells. [Templates](templates.md) explains models.

```csharp
var items = new Table([new Column("Description"), new Column("Amount") { Align = TextAlign.Right }])
{
    Rows = TableRows.Repeat("lines", [new Cell([new Bind("description")]), new Cell([new Bind("amount") { Format = "N2" }])]),
};
```
