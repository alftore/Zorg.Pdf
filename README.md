# Zorg.Pdf

Async-first PDF for .NET 10, written in C# only. You can build documents, reports, letters and invoices from code, from a JSON document model, or from templates and data. Output is tagged and accessible, and the core has no native binaries and no NuGet dependencies. It can also read, merge and split existing PDFs.

**Status: 0.5.0 is ready for its first release.** Versions 0.1 to 0.5 are complete; a preview (0.1.0-preview.1) is on NuGet. The API may still change before 1.0. Read [the guide](docs/guide/README.md) to get started, and [docs/limits.md](docs/limits.md) for what isn't supported.

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

The same document can be written as JSON (described by [the schema](schema/document-0.1.schema.json)) and rendered with the `zorg-pdf` tool:

```bash
zorg-pdf render docs/examples/annual-report.json --pdfua --pdfa 2a --preview
```

## What it does

- **Documents:** headings, paragraphs with styled runs and fallback fonts, lists, tables with repeating headers, spans, stripes, and rows streamed from `IAsyncEnumerable`. It also handles images, text boxes at fixed places, watermarks, and headers and footers with "Page X of Y".
- **Reports:** tables of contents, bookmarks, links, sections with their own page numbering, headers and footers, and bar, stacked, line and pie charts drawn as vectors.
- **Templates:** values, repeats and conditions bound from a JSON model, so one document renders many invoices.
- **Letters:** address boxes for DIN 5008 and Norwegian window envelopes, letterhead from an existing PDF behind the page, QR codes, Code 128, GS1-128 and EAN-13 barcodes, EPC payment codes, Swiss QR-bills (not yet verified with a Swiss banking app: see [the guide](docs/guide/barcodes.md#swiss-qr-bills)) and KID check digits.
- **Accessible and archivable:** tagged PDF by default, with PDF/UA-1 and PDF/A-2 and -3 (a, u and b) checked against veraPDF. Attachments, and Factur-X/ZUGFeRD e-invoices.
- **Signed:** PAdES baseline signatures (B-B, and B-T with an RFC 3161 timestamp) as the document renders, with a certificate's key or one in an HSM or key vault through `IPdfSigner`. The CMS is built in, so there's still no dependency.
- **Existing PDFs:** page count, sizes and metadata of any PDF, including damaged ones and ones encrypted without a password; merge, extract and split; sign, keeping earlier signatures valid.
- **For agents:** one document model with a published JSON Schema, diagnostics with stable codes and JSON pointers ([docs/diagnostics.md](docs/diagnostics.md)), PNG previews to check a layout by eye, and ready-made `AIFunction` tools.
- **Fast and small:** a tagged 1,000-row invoice renders in 30 ms (MigraDoc 6.2.4: 260 ms). Output is byte for byte the same on Windows and Linux. The library is clean under NativeAOT and trimming.

## Packages

Most apps need `Zorg.Pdf` alone. The others each bring a dependency, a framework or a lot of data that not everyone needs, so they're separate.

| Package | You need it when | What it adds |
|---|---|---|
| `Zorg.Pdf` | Always | The document model, JSON, rendering, signing, reading and merging, barcodes and payment codes, Factur-X/ZUGFeRD. No dependencies |
| `Zorg.Pdf.Imaging` | You use images other than JPEG and PNG, want them shrunk or turned upright, or want PNG previews of pages | WebP, GIF, BMP and TIFF, EXIF orientation, sRGB conversion, downsampling and page previews, through Zorg.Imaging |
| `Zorg.Pdf.Markdown` | Your content is written in Markdown | Markdown to PDF, on Markdig |
| `Zorg.Pdf.Hyphenation` | You justify text or set narrow columns | Hyphenation for English, German, French, Norwegian and Danish (600 KB of patterns) |
| `Zorg.Pdf.AspNetCore` | You serve PDFs from ASP.NET Core | `PdfResults.Pdf`: streams a PDF response, with a 422 ProblemDetails for a document that can't render |
| `Zorg.Pdf.Extensions.AI` | An AI agent writes your documents | Tools that let an agent validate and render documents |
| `Zorg.Pdf.Tool` | You work from the command line or a build script | The `zorg-pdf` tool: render, watch, preview, info, merge, extract, split, sign |

## Examples

[docs/examples/](docs/examples) has fifteen documents, from a receipt to a staff handbook. Each one is rendered by the tests and checked with veraPDF. [annual-report.json](docs/examples/annual-report.json) shows the most at once: a cover with a colour band and key-figure cards, a table of contents, line, bar and pie charts, striped tables and a QR code.

## Roadmap

| Version | Scope | State |
|---|---|---|
| 0.1 | The document model and JSON Schema; headings, paragraphs, tables, images, headers and footers; PDF/UA-1; agent tools | Done |
| 0.2 | Templates and data binding, lists, cell spans, watermarks, text boxes, PNG previews, the `zorg-pdf` tool | Done |
| 0.3 | PDF/A-2, bookmarks and links, tables of contents, sections and page labels, hyphenation, Markdown | Done |
| 0.4 | Reading PDFs, letterhead backgrounds, merging and splitting, attachments and PDF/A-3, Factur-X, encrypted input | Done |
| 0.5 | Barcodes and payment codes, charts, digital signatures (PAdES), an ASP.NET Core result | Done |

## Goals

- **Async first:** fonts, images and data load asynchronously, and table rows can come from `IAsyncEnumerable<T>`. Pages stream to the output while layout continues, and every operation can be cancelled.
- **Easy for agents:** one document model, a published schema, precise diagnostics and previews for visual self-checks.
- **Accessible and archivable by default,** checked by veraPDF rather than claimed.
- MIT licensed, with no runtime dependencies in the core package, and clean under NativeAOT and trimming.
- Native code only as an opt-in add-on, never a requirement.

## How this is built

Zorg.Pdf is written with the help of AI agents, mainly Claude Code from Anthropic. The maintainer sets the direction and makes the design decisions, and agents do most of the implementation. This note covers the whole project, so individual commits don't carry a co-author line.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Report security problems as [SECURITY.md](SECURITY.md) describes. Local scratch files go in `.tmp/`, which is gitignored.

## License

MIT. See [LICENSE](LICENSE). Package choices are recorded in [docs/dependencies.md](docs/dependencies.md).
