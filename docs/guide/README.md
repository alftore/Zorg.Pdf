# The Zorg.Pdf guide

Each page covers one topic, with C# and JSON side by side where both apply. The C# snippets are copied from [GuideSnippets.cs](../../tests/Zorg.Pdf.Tests/Guide/GuideSnippets.cs), which the tests compile. A test also fails if a snippet here stops matching it, so the code you read here builds against the current API.

| Topic | What it covers | State |
|---|---|---|
| [Getting started](getting-started.md) | Install, fonts, a first document in C# and in JSON, validation, previews | Written |
| [Design and layout](design.md) | Styles, colour, cover bands and cards with text boxes, striped tables; the annual report example | Written |
| Document model | Blocks, inlines, lengths, ids, keep-with-next | Planned |
| Tables and data | Columns, spans, rows from `IAsyncEnumerable`, bound rows | Planned |
| Templates | Binding, repeat, when, formats | Planned |
| Headers, footers and sections | Page X of Y, numbering, per-section bands | Planned |
| Links, bookmarks and contents | Links, ids, outlines, tables of contents | Planned |
| Accessibility and archiving | PDF/UA-1, alt text, PDF/A-2 and -3, attachments | Planned |
| Images and previews | Formats, `Zorg.Pdf.Imaging`, PNG previews | Planned |
| Letters | Window envelopes, letterhead backgrounds | Planned |
| Barcodes and payments | QR, Code 128, GS1-128, EAN-13, EPC, KID | Planned |
| Charts | Bar, stacked, line and pie; alt text | Planned |
| Existing PDFs | Info, merge, extract, split, encrypted input | Planned |
| Invoices | Factur-X and ZUGFeRD | Planned |
| The command line | `zorg-pdf` render, watch, preview and the file commands | Planned |
| Agents | The schema, diagnostics and `AIFunction` tools | Planned |

The reference lists are elsewhere in `docs/`: [diagnostics](../diagnostics.md), [limits](../limits.md), [dependencies](../dependencies.md) and [.NET versions](../dotnet-versions.md).
