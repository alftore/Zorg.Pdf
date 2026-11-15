# Charts

Charts are drawn as vectors, so they stay sharp at any zoom and add little to the file. There are four kinds:

| Kind | JSON `kind` | Use it for |
|---|---|---|
| `ChartKind.Bar` | `bar` | Comparing values across categories; several series side by side |
| `ChartKind.StackedBar` | `stackedBar` | Parts of a whole per category (values can't be negative) |
| `ChartKind.Line` | `line` | Change over time; each series has its own marker shape |
| `ChartKind.Pie` | `pie` | Shares of one whole, a few slices at most (one series) |

```csharp
var sales = new Chart(ChartKind.Bar, ["Q1", "Q2", "Q3", "Q4"], [new ChartSeries("2025", [12, 15, 14, 18]), new ChartSeries("2026", [14, 17, 19, 22])])
{
    Title = "Sales, MNOK",
    ValueFormat = "N0",
    Height = Length.Cm(6),
    Alt = "Sales rose every quarter in 2026, from 14 to 22 million kroner, above 2025's.",
};
```

```json
{
  "type": "chart", "kind": "line",
  "categories": ["Q1", "Q2", "Q3", "Q4"],
  "series": [{ "name": "2025", "values": [74, 81, 79, 115], "color": "#94A3B8" }, { "name": "2026", "values": [86, 97, 92, 137], "color": "#0F2A3D" }],
  "valueFormat": "N0", "height": "6cm",
  "alt": "Revenue was higher in every quarter of 2026, peaking at 137 in the fourth."
}
```

- **Alt text is required,** unless the chart is `Decorative`. Write the point the chart makes, not every number: the numbers belong in a table next to it if readers need them.
- **Axes** get round steps (1, 2, 2.5 or 5 times a power of ten) and fit the data. `Minimum` and `Maximum` fix them. Values are formatted in the document's language with `ValueFormat`.
- **Horizontal** bars (`Horizontal = true`) suit long category names.
- **Colours** default to a palette people with colour blindness can tell apart. Set `Color` on a series to match a brand, and keep enough contrast between neighbours.
- **Legends** appear when there's more than one series, or for pies. `Legend` turns them on or off.
- **From data:** `Bind = "sales"` reads `{ "categories": [...], "series": [...] }` from the [template](templates.md) model.

A chart is one Figure with its alt text, and its labels are inside it, so screen readers announce it once. Bad data, such as a series with the wrong number of values, is ZPDF-J033 with the path of the value.
