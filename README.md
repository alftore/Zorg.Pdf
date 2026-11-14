# Zorg.Pdf

Async-first PDF generation for .NET 10, written in C# only. Documents and reports from code, from a JSON document model, or from templates and data, in the spirit of jsreport, with tagged (accessible) output, no native binaries and no NuGet dependencies.

**Status: 0.1 in development, not released.** Build a document in C# or JSON (schema in [schema/](schema/document-0.1.schema.json), ten examples with data in [docs/examples/](docs/examples)), validate it (codes in [docs/diagnostics.md](docs/diagnostics.md)), and render headings, paragraphs with bold, italic and fallback fonts, bulleted and numbered lists, tables with rows from `IAsyncEnumerable`, JPEG and PNG images, and headers and footers with "Page X of Y" as compact tagged PDF. Every example passes veraPDF PDF/UA-1 and comes out byte for byte the same on Windows and Linux. `Zorg.Pdf.Imaging` adds WebP, GIF, BMP and TIFF, EXIF orientation, sRGB conversion and downsampling through Zorg.Imaging. A tagged 1,000-row invoice renders in 30 ms (MigraDoc 6.2.4: 260 ms). What isn't supported is in [docs/limits.md](docs/limits.md).

```csharp
var document = new PdfDocument("Invoice 2026-0042")
{
    Footer = new PageBand([new Paragraph(["Page ", Field.PageNumber, " of ", Field.PageCount]) { Align = TextAlign.Right }]),
    Content = [new Heading("Invoice 2026-0042"), new Table([new Column("Item"), new Column("Amount")]) { Rows = TableRows.Bound("lines") }],
};
var options = new RenderOptions
{
    Fonts = FontCollection.Empty.Add(new FontFamily("Inter") { Regular = FontSource.FromFile("Inter-Regular.ttf") }),
    Data = PdfData.Empty.WithRows("lines", linesAsync, line => [line.Item, line.Amount.ToString("N2")]),
    Conformance = PdfConformance.PdfUA1,
};
await document.RenderAsync(output, options, cancellationToken);
```

## Roadmap

0.1 is built in nine slices; all nine are done, and it is being reviewed before its first preview release.

| Version | Scope | State |
|---|---|---|
| 0.1 | The document model and its JSON Schema; rendering headings, paragraphs, tables, images, headers and footers with page numbers; bold and italic; tagged output with PDF/UA-1; object streams for small files; agent tools as a stretch goal | Complete, in review: the library and `Zorg.Pdf.Extensions.AI` |
| 0.2 | Templates and data binding, lists, cell spans, PNG page previews, a `zorg-pdf` command-line tool | Planned |
| 0.3 | Markdown, tables of contents and links, hyphenation, right-to-left and Arabic text, PDF/A | Planned |
| 0.4 | Reading and merging PDFs, page groups, attachments, ZUGFeRD/Factur-X invoices | Planned |
| 0.5 | Charts, barcodes, ASP.NET Core report jobs, signing | Planned |

## Goals

- **Async first:** fonts, images and data load asynchronously, table rows can come from `IAsyncEnumerable<T>`, pages stream to the output while layout continues, and every operation can be cancelled.
- **Easy for agents:** one document model described by a published JSON Schema, validation errors that point at the exact path, PNG page previews for visual self-checks, and ready-made `AIFunction` tools.
- **Easy reports:** templates plus data, with headers and footers, page X of Y, repeating table headers, tables of contents, page groups and charts.
- **Accessible and archivable:** tagged PDF by default, with PDF/UA and PDF/A checked against veraPDF.
- MIT licensed, with no runtime dependencies in the core package, NativeAOT and trimming clean.
- Native code only as an opt-in add-on, never a requirement.

## How this is built

Zorg.Pdf is written with the help of AI agents, mainly Claude Code from Anthropic. The maintainer sets the direction and makes the design decisions, and agents do most of the implementation. This note covers the whole project, so individual commits don't carry a co-author line.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Report security problems as [SECURITY.md](SECURITY.md) describes. Local scratch files go in `.tmp/`, which is gitignored.

## License

MIT. See [LICENSE](LICENSE). Package choices are recorded in [docs/dependencies.md](docs/dependencies.md).
