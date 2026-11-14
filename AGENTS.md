# Working on Zorg.Pdf

Async-first PDF generation for .NET 10, in C# only. Early 0.1: the public API is the document model (`PdfDocument`, blocks, inlines, styles, units), its JSON form, validation, and rendering of headings, paragraphs, spacers, page breaks, tables, images, headers and footers (`RenderAsync`, `Render`). Don't call types from the design notes that don't exist yet.

## Writing code that uses Zorg.Pdf

- A document is an immutable record tree: `new PdfDocument("Title") { Content = [new Heading("..."), new Paragraph("..."), new Table([new Column("A")]) { Rows = ... }] }`. Change it with `with`.
- Lists: `new List(["One", new ListItem([new Paragraph("Two"), new List(["Two a"])])]) { Marker = ListMarker.Number }`; items hold paragraphs and nested lists only.
- Blocks with a required part take it in the constructor: `new Heading(text)`, `new Paragraph(text or inlines)`, `new Table(columns)`, `new Image(source)`, `new Spacer(height)`, `new PageBand(content)`. Inlines are strings, `TextRun`, `LineBreak.Value` and `Field.PageNumber`/`Field.PageCount` (drawn in headers and footers only: `Footer = new PageBand([new Paragraph(["Page ", Field.PageNumber, " of ", Field.PageCount])])`).
- Templates: `new Paragraph(["Dear ", new Bind("customer.name")])`, `new Repeat("notes", [...])`, `new Conditional("overdue", [...])` (JSON `"type": "when"`), with `Data = PdfData.Empty.WithModel(json)` or `PdfData.ParseJson(dataJson)`. A list item whose only content is a `Repeat` repeats the item. Table rows from a model array: `Rows = TableRows.Repeat("lines", [new Cell([new Bind("item")]), new Cell([new Bind("amount") { Format = "N2" }])])` (JSON `"rows": { "repeat": "lines", "cells": [{ "bind": "item" }, ...] }`). No `{{ }}` in text.
- Watermarks: `document with { Watermark = new Watermark("DRAFT") { Opacity = 0.15, Layer = WatermarkLayer.Behind } }`, or `new Watermark(ImageSource.FromSlot("logo"))`.
- Table cells span with `new Cell("Total") { ColumnSpan = 3 }` or `RowSpan`; a row counts each cell's span, and leaves out columns a row span from above covers.
- Lengths: `Length.Pt/Mm/Cm/In`, or strings like `"2cm"` in JSON. Column widths: `ColumnWidth.Star(2)` / `"2*"`, `ColumnWidth.Fixed(...)`.
- Data that changes per render is a slot: `TableRows.Bound("lines")` / `{"$data": "lines"}`, `ImageSource.FromSlot("logo")` with `Data = PdfData.Empty.WithImage("logo", bytes)`. Documents never name files. Rows and images can also come from JSON: `PdfData.ParseJson("""{ "lines": [["1", "Consulting"]], "logo": { "base64": "..." } }""")`. Rows are filled at render time: `Data = PdfData.Empty.WithRows("lines", items, item => [item.Name, item.Amount.ToString("N2")])`, from `IEnumerable<T>` or `IAsyncEnumerable<T>`.
- `PdfDocument.TryParseJson` never throws; `Validate()` never throws for a bad document. Both return diagnostics with a code (`docs/diagnostics.md`) and a JSON pointer.
- The JSON Schema is `PdfJsonSchema.Current`; `PdfDocument.ToJson()` writes `$schema`. Unknown JSON properties are errors.
- Rendering needs fonts: `new RenderOptions { Fonts = FontCollection.Empty.Add(new FontFamily("Inter") { Regular = FontSource.FromFile("Inter-Regular.ttf"), Bold = ..., Italic = ..., Fallbacks = ["Noto Sans Symbols"] }) }`. No system fonts unless `FontResolver = FontResolvers.System()`. A folder of fonts: `Fonts = FontCollection.FromFolder("fonts").WithDefault("Inter")`. `await document.RenderAsync(stream, options, ct)`; `Render(stream, options)` for sync callers.
- Styles: `new TextRun("bold") { Style = TextStyle.Bold }`; a style sets font family, size, weight, style, colour, line height. A missing face is synthesised (ZPDF-F003 Info).
- Agent tools: `PdfTools.Create(new PdfToolOptions { Render = renderOptions, SaveAsync = ... })` in `Zorg.Pdf.Extensions.AI` gives `get_document_schema`, `validate_document` and `render_document` as `AIFunction`s. The host owns fonts and storage; the model sends document and data JSON.
- Command line: `zorg-pdf render doc.json --data data.json --fonts fonts/ --pdfua`, `zorg-pdf validate doc.json --json`, `zorg-pdf watch doc.json`, `zorg-pdf schema` (`src/Zorg.Pdf.Tool`, package `Zorg.Pdf.Tool`). Exit codes: 0 done, 1 document problems, 2 command line or files.
- Images: JPEG and PNG are read by Zorg.Pdf itself. Anything more (other formats, EXIF orientation, colour conversion, shrinking photos) goes through `RenderOptions.ImageProcessor`; `ImageProcessor = new ImagingProcessor()` from `Zorg.Pdf.Imaging` (built on Zorg.Imaging) does all of that. The core library takes no image dependency.
- A render checks everything it can before writing (validation, fonts, glyphs, control characters) and throws `PdfDocumentException` with every problem; warnings come back in `RenderResult.Diagnostics`. Output is always tagged; `Conformance = PdfConformance.PdfUA1` also claims PDF/UA-1.

## Working on the repo

```bash
dotnet build Zorg.Pdf.slnx -nologo -v q
```

```bash
dotnet test --solution Zorg.Pdf.slnx --no-build
```

- `CONTRIBUTING.md` has the conventions and the clean-room rules. The build fails on any warning.
- Commits are signed through `commit.gpgsign`. Never bypass it. If signing fails, stop and tell the maintainer.
- Early development: the repository is private and signed commits go straight to `main`, no pull requests needed. That changes before the first release, when `.github/rulesets/` is applied.
- Commit messages and pull requests have no `Co-Authored-By` or "Generated with" lines. The README states that agents help build the library.
- `.tmp/research/` and `.tmp/design/` hold the research and the agreed design, where they exist on your machine. Read `.tmp/HANDOFF.md` first. Plans, spikes and scratch files go under `.tmp/` too (gitignored), never in `docs/` or the repository root.
- `docs/dotnet-versions.md` records what each new .NET version offers the library. Update it when one ships.
- The core library takes no runtime packages. Test and benchmark packages need a row in `docs/dependencies.md`, with the licence checked in the `.nuspec`.
- Never read the source of QuestPDF (source-available licence), PDFsharp/MigraDoc 6.5 or later, or iText (both AGPL). Work from the specifications.

## The guarantees

Each will have tests. A change that weakens one needs a design decision, not a test edit.

1. **The same input gives byte-identical output.**
2. **Output is tagged by default.** Output that claims PDF/UA or PDF/A passes veraPDF.
3. **Templates can't run code or do I/O.** Bindings are expressions with bounded cost, so templates from agents or users are safe to render.
4. **Problems are diagnostics with a path.** A missing glyph or content that overflows is reported with where it happened, never silently dropped or clipped.
5. **No native code unless the caller adds a native package.**
