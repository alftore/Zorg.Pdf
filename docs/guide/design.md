# Design and layout

A document gets its look from three things: **named styles** for text, **table styles** for tables, and **text boxes** for anything placed at a fixed spot, such as a cover band or a row of key-figure cards. [annual-report.json](../examples/annual-report.json) uses all three. Render it to see the result:

```bash
zorg-pdf render docs/examples/annual-report.json --pdfua --pdfa 2a --preview --page all
```

## Named styles

Every block has a named style: `body`, `heading1` to `heading6`, `tableHeader`, `tableCell`, `header`, `footer`, `toc1` to `toc6`, or any name you add. A style sets the font family, size, weight, slant, colour, line height and language. Anything it leaves out comes from the style underneath, and in the end from `body`.

```json
"styles": {
  "styles": {
    "body": { "size": "10pt", "lineHeight": 1.45, "color": "#1E293B" },
    "heading1": { "size": "22pt", "weight": "bold", "color": "#0F2A3D" },
    "coverTitle": { "size": "40pt", "weight": "bold", "color": "#FFFFFF" }
  }
}
```

In C#, add styles to the defaults:

```csharp
var styles = StyleSheet.Default with
{
    Styles = StyleSheet.Default.Styles
        .SetItem("heading1", new TextStyle { Size = Length.Pt(22), Weight = FontWeight.Bold, Color = Color.Parse("#0F2A3D") })
        .SetItem("coverTitle", new TextStyle { Size = Length.Pt(40), Weight = FontWeight.Bold, Color = Color.White }),
};
```

A few habits make a page look designed rather than default:

- **One dark colour for structure** (headings, table headers, the cover) and **one accent** used sparingly. The example uses navy `#0F2A3D` and amber `#F2A541`.
- **Body text that isn't pure black,** such as slate `#1E293B`, with a line height of 1.4 to 1.5.
- **Muted small text** for notes, sources and page furniture: `#64748B` at 8 to 8.5 pt.
- **Contrast for reading:** keep body text at 4.5:1 or more against its background, as WCAG asks. Colour fills are artifacts, so screen readers skip them, but sighted readers don't.

## Cover bands and cards

A text box sits at a fixed place on the page and takes no room in the flow. With a `fill` it becomes a coloured band or card. Its fill and border are artifacts, and its text is tagged as paragraphs like any other.

```json
{
  "type": "textBox",
  "x": "0mm", "y": "0mm", "width": "210mm", "height": "110mm",
  "padding": { "top": "34mm", "left": "20mm", "right": "20mm", "bottom": "0mm" },
  "fill": "#0F2A3D",
  "content": [{ "type": "paragraph", "style": "coverTitle", "inlines": ["Fjordlys AS"] }]
}
```

```csharp
var cover = new TextBox([new Paragraph("Fjordlys AS") { Style = "coverTitle" }])
{
    X = Length.Zero,
    Y = Length.Zero,
    Width = Length.Mm(210),
    Height = Length.Mm(110),
    Padding = new Thickness(Length.Mm(34), Length.Mm(20), Length.Zero, Length.Mm(20)),
    Fill = Color.Parse("#0F2A3D"),
};
```

Because text boxes take no room, follow them with a `spacer` as tall as the space they cover, so the flowing text starts below them. The example's four cards are text boxes 39.5 mm wide with 4 mm gaps, each holding a value, a label and a change.

## Tables

`tableStyle` sets the header fill, the rules between rows, the cell padding and `stripeFill`, a background on every second body row:

```json
"tableStyle": { "headerFill": "#0F2A3D", "stripeFill": "#F4F6F9", "ruleColor": "#D9DEE5", "ruleWidth": "0.4pt", "cellPadding": "4.5pt" }
```

```csharp
var table = new Table([new Column("Measure"), new Column("2026") { Align = TextAlign.Right }])
{
    Rows = TableRows.Inline([["Lamps taken back", "11 200"], ["Parts reused", "93 %"]]),
    TableStyle = new TableStyle { HeaderFill = Color.Parse("#0F2A3D"), StripeFill = Color.Parse("#F4F6F9"), CellPadding = Length.Pt(4.5) },
};
```

A dark header needs light header text: set `tableHeader` to white. Align numbers right, and set totals in bold with a styled run.

## Charts

Give each series its own `color` to match the document: a muted grey for last year and the dark or accent colour for this year reads at a glance. Without colours, charts use a palette that people with colour blindness can tell apart. Line charts also give each series its own marker shape.

## What isn't there yet

Fills go on text boxes and table rows only, not on paragraphs or arbitrary shapes. Text boxes are fixed in place, not side by side in the flow, and there are no multi-column layouts. [Limits](../limits.md) lists the rest.
