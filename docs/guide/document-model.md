# The document model

A `PdfDocument` is a title, page settings, optional header, footer and watermark, and a list of **blocks**. Blocks flow down the page and onto new pages. Text inside a block is a list of **inlines**: plain strings, styled runs, fields, links and template bindings. Everything is an immutable record, so `with` makes variants.

```csharp
var document = new PdfDocument("Invoice 2026-0042")
{
    Language = "en-GB",
    Page = PageSettings.A4 with { Margin = Thickness.All(Length.Cm(2)) with { Top = Length.Cm(3) } },
    Content =
    [
        new Heading("Invoice 2026-0042") { Id = "top" },
        new Paragraph(["Due: ", new TextRun("14 December") { Style = TextStyle.Bold }]) { Align = TextAlign.Justify },
        new List(["Check the amounts", "Pay by transfer"]) { Marker = ListMarker.Number },
        new Spacer(Length.Mm(10)),
        new Heading("Payment terms") { Level = 2, KeepWithNext = true },
        new Paragraph("Payment is due within 30 days."),
        new PageBreak(),
    ],
};
var draft = document with { Watermark = new Watermark("DRAFT") };
```

The same in JSON:

```json
{
  "$schema": "https://alftore.github.io/Zorg.Pdf/schema/document-0.1.schema.json",
  "title": "Invoice 2026-0042",
  "language": "en-GB",
  "page": { "size": "A4", "margin": { "top": "3cm", "right": "2cm", "bottom": "2cm", "left": "2cm" } },
  "watermark": { "text": "DRAFT" },
  "content": [
    { "type": "heading", "id": "top", "inlines": ["Invoice 2026-0042"] },
    { "type": "paragraph", "align": "justify", "inlines": ["Due: ", { "text": "14 December", "style": { "weight": "bold" } }] },
    { "type": "list", "marker": "number", "items": ["Check the amounts", "Pay by transfer"] },
    { "type": "spacer", "height": "10mm" },
    { "type": "heading", "level": 2, "keepWithNext": true, "inlines": ["Payment terms"] },
    { "type": "paragraph", "inlines": ["Payment is due within 30 days."] },
    { "type": "pageBreak" }
  ]
}
```

## Blocks

| Block | JSON `type` | Notes |
|---|---|---|
| `Heading` | `heading` | Levels 1 to 6, tagged H1 to H6. Don't skip levels (ZPDF-A004) |
| `Paragraph` | `paragraph` | Alignment left, center, right or justify; `spaceAfter` |
| `List` | `list` | Bullets, numbers, letters or roman numerals; items can nest |
| `Table` | `table` | See [tables and data](tables-and-data.md) |
| `Image` | `image` | See [images and previews](images.md) |
| `Spacer`, `PageBreak` | `spacer`, `pageBreak` | Vertical space, and a new page |
| `TextBox` | `textBox` | Fixed in place, outside the flow. See [design](design.md) and [letters](letters.md) |
| `Chart`, `Barcode` | `chart`, `barcode` | See [charts](charts.md) and [barcodes](barcodes.md) |
| `TableOfContents`, `SectionBreak` | `tableOfContents`, `sectionBreak` | See [links and contents](links-and-contents.md) and [sections](sections.md) |
| `Repeat`, `Conditional` | `repeat`, `when` | See [templates](templates.md) |

Every block has `Style` (a named style), `KeepWithNext` (stay on the same page as the next block's first line; headings do this already) and `Id` (a name that links and bookmarks can point to).

## Lengths

Lengths take units: `Length.Mm(10)`, `Length.Cm(2)`, `Length.Pt(12)`, or in JSON `"10mm"`, `"2cm"`, `"12pt"`, `"1in"`. Page sizes are `A3`, `A4`, `A5`, `Letter` and `Legal`, or any width and height.

## Text, fonts and hyphenation

Text is shaped with kerning, wraps at word boundaries and soft hyphens, and falls back to other families for characters the main font lacks. A character no font has is an error under PDF/UA (ZPDF-F002), so it never silently turns into a box.

Long words in justified text look better hyphenated. Add `Zorg.Pdf.Hyphenation` and turn on `Hyphens.Auto` in the styles. It knows English, German, French, Norwegian (Bokmål and Nynorsk) and Danish:

```csharp
var options = new RenderOptions { Fonts = fonts, Hyphenator = PatternHyphenator.Default };
var styles = new StyleSheet { Styles = StyleSheet.Default.Styles.SetItem("body", new TextStyle { Size = Length.Pt(10), Hyphens = Hyphens.Auto }) };
```

A run in another language gets its own language tag, so screen readers switch voice and hyphenation follows that language:

```csharp
var mixed = new Paragraph(["The German word ", new TextRun("Fernweh") { Style = new TextStyle { Language = "de" } }, " has no English equal."]);
```
