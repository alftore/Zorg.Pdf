# Letters

## Address boxes for window envelopes

A `TextBox` with a preset puts the address where an envelope's window will show it, whatever the rest of the page does:

| Preset | JSON | Place |
|---|---|---|
| `Din5008A` | `din5008A` | 85 × 45 mm, 20 mm from the left and 27 mm from the top (short letterhead) |
| `Din5008B` | `din5008B` | 85 × 45 mm, 20 mm from the left and 45 mm from the top |
| `NorwegianWindow` | `norwegianWindow` | 90 × 40 mm at 20 mm from the left and 40 mm from the top, text from 21.27 mm, for a C5/E65 envelope with the sheet folded in three |

```csharp
var address = new TextBox([new Paragraph([new Bind("customer.name")]), new Paragraph([new Bind("customer.street")]), new Paragraph([new Bind("customer.city")])])
{
    Preset = TextBoxPreset.NorwegianWindow,
};
```

Text too long for the box is made smaller, down to `MinSize`, then cut (diagnostic ZPDF-L006). With `norwegianWindow`, Posten's advice for machine-read addresses is checked too. An address of fewer than two lines or with a blank line is warning ZPDF-L008. A font or size outside Posten's list (Arial or Courier New at 10 to 12 pt, Verdana at 9 to 12 pt), or bold or italic, is a note (ZPDF-L009). The measurements follow common Norwegian practice and Posten's guidance. Zorg.Pdf doesn't claim NS 4129 conformance, so print a test on your own paper and envelopes.

Other text boxes put the date, reference or sender block at a fixed place. Their `X`, `Y`, `Width` and `Height` are measured from the paper's top-left corner, and setting any of them overrides the preset.

```json
{ "type": "textBox", "preset": "norwegianWindow", "content": [
  { "type": "paragraph", "inlines": [{ "bind": "customer.name" }] },
  { "type": "paragraph", "inlines": [{ "bind": "customer.street" }] },
  { "type": "paragraph", "inlines": [{ "bind": "customer.city" }] }
] }
```

[brev.json](../examples/brev.json) is a complete Norwegian letter, and [letter.json](../examples/letter.json) a DIN 5008 one.

## Letterhead

Many companies have their letterhead as a PDF from a designer. `Background` draws a page of an existing PDF behind every page: one page for the first page, and optionally another, such as a quieter one, for the rest. It is drawn as an artifact, so tagging and PDF/UA stay valid, and its own tags are removed.

```csharp
var letter = document with { Background = new PageBackground(ImageSource.FromSlot("letterhead")) { OtherPages = 2 } };
var options = new RenderOptions { Fonts = fonts, Data = PdfData.Empty.WithPdf("letterhead", await File.ReadAllBytesAsync("letterhead.pdf", cancellationToken)) };
```

```json
"background": { "source": { "$data": "letterhead" }, "firstPage": 1, "otherPages": 2 }
```

On the command line, pass the file for the slot: `--image letterhead=letterhead.pdf`. Pages of another size are scaled to fit, and rotated pages are turned upright.
