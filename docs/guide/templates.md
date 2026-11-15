# Templates

A template is an ordinary document with **bindings** in it. The data comes from a JSON **model** at render time, so one document makes every invoice, letter or report of its kind. Templates are kept as JSON, checked once, and filled many times.

| Part | C# | JSON |
|---|---|---|
| A value | `new Bind("customer.name")` | `{ "bind": "customer.name" }` |
| A formatted value | `new Bind("total") { Format = "N2" }` | `{ "bind": "total", "format": "N2" }` |
| Blocks per array item | `new Repeat("notes", [...])` | `{ "type": "repeat", "items": "notes", "content": [...] }` |
| Blocks only when a value is set | `new Conditional("overdue", [...])` | `{ "type": "when", "test": "overdue", "content": [...] }` |
| Table rows per item | `TableRows.Repeat("lines", [...])` | `"rows": { "repeat": "lines", "cells": [...] }` |

```csharp
var reminder = new PdfDocument("Reminder")
{
    Content =
    [
        new Paragraph(["Dear ", new Bind("customer.name"), ","]),
        new Conditional("overdue", [new Paragraph("Payment is overdue.")]),
        new Conditional("overdue", [new Paragraph("Thank you for paying on time.")]) { Not = true },
        new Repeat("notes", [new Paragraph([new Bind("@number"), ". ", new Bind("text")])]),
        new Paragraph(["Total: ", new Bind("total") { Format = "N2", Style = TextStyle.Bold }]),
    ],
};
using JsonDocument model = JsonDocument.Parse("""{ "customer": { "name": "Blåbær AB" }, "overdue": true, "total": 1250, "notes": [{ "text": "Fee added" }] }""");
await reminder.RenderAsync(output, new RenderOptions { Fonts = fonts, Data = PdfData.Empty.WithModel(model.RootElement) }, cancellationToken);
```

- **Paths** are dotted (`customer.address.city`). Inside a repeat they look in the current item first, then outwards. `@number` is the item's number from 1, `@index` from 0, and `this` is the item itself.
- **Formats** are .NET format strings (`N2`, `P0`, `yyyy-MM-dd`), applied in the document's language, so `N2` gives `1 250,00` in Norwegian and `1,250.00` in English.
- **"Set"** means present and not null, false, 0, empty text or an empty array.
- **Missing values** are warning ZPDF-B001 with the path of the binding, and the text is left empty. Check `RenderResult.HasWarnings`, or pass `--warnaserror` on the command line, to catch them.

With the command line, the model is the `--data` file:

```bash
zorg-pdf render docs/examples/reminder.json --data docs/examples/data/reminder.json
```

Images, PDFs and attachments come in through named **slots** (`{ "$data": "logo" }`), filled with `PdfData.WithImage`, `WithPdf` and `WithFile`, or `--image logo=logo.png`. A document never names files on disk itself, so a template from an untrusted source can't read your files.
