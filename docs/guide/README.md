# The Zorg.Pdf guide

Each page covers one topic, with C# and JSON side by side where both apply. The C# snippets are copied from [the GuideSnippets files](../../tests/Zorg.Pdf.Tests/Guide), which the tests compile. A test also fails if a snippet here stops matching it, so the code you read here builds against the current API.

| Topic | What it covers | State |
|---|---|---|
| [Getting started](getting-started.md) | Install, fonts, a first document in C# and in JSON, validation, previews | Written |
| [Design and layout](design.md) | Styles, colour, cover bands and cards with text boxes, striped tables; the annual report example | Written |
| [Document model](document-model.md) | Blocks, inlines, lengths, ids, keep-with-next | Written |
| [Tables and data](tables-and-data.md) | Columns, spans, rows from `IAsyncEnumerable`, bound rows | Written |
| [Templates](templates.md) | Binding, repeat, when, formats | Written |
| [Headers, footers and sections](sections.md) | Page X of Y, numbering, per-section bands | Written |
| [Links, bookmarks and contents](links-and-contents.md) | Links, ids, outlines, tables of contents | Written |
| [Accessibility and archiving](accessibility.md) | PDF/UA-1, alt text, PDF/A-2 and -3, attachments | Written |
| [Images and previews](images.md) | Formats, `Zorg.Pdf.Imaging`, PNG previews | Written |
| [Letters](letters.md) | Window envelopes, letterhead backgrounds | Written |
| [Barcodes and payments](barcodes.md) | QR, Code 128, GS1-128, EAN-13, EPC, KID | Written |
| [Charts](charts.md) | Bar, stacked, line and pie; alt text | Written |
| [Digital signatures](signatures.md) | PAdES B-B and B-T, certificates, timestamps, keys in an HSM or key vault | Written |
| [Existing PDFs](existing-pdfs.md) | Info, merge, extract, split, encrypted input | Written |
| [Markdown](markdown.md) | Markdown to PDF, images, diagnostics | Written |
| [Invoices](invoices.md) | Factur-X and ZUGFeRD | Written |
| [The command line](cli.md) | `zorg-pdf` render, watch, preview and the file commands | Written |
| [Agents](agents.md) | The schema, diagnostics and `AIFunction` tools | Written |

The reference lists are elsewhere in `docs/`: [diagnostics](../diagnostics.md), [limits](../limits.md), [dependencies](../dependencies.md) and [.NET versions](../dotnet-versions.md).
