# Working on Zorg.Pdf

Async-first PDF generation for .NET 10, in C# only. Early 0.1: the public API is the document model (`PdfDocument`, blocks, inlines, styles, units), its JSON form, validation, and rendering of headings, paragraphs, spacers, page breaks, tables, images, headers and footers (`RenderAsync`, `Render`). Don't call types from the design notes that don't exist yet.

## Writing code that uses Zorg.Pdf

- A document is an immutable record tree: `new PdfDocument("Title") { Content = [new Heading("..."), new Paragraph("..."), new Table([new Column("A")]) { Rows = ... }] }`. Change it with `with`.
- Lists: `new List(["One", new ListItem([new Paragraph("Two"), new List(["Two a"])])]) { Marker = ListMarker.Number }`; items hold paragraphs and nested lists only.
- Blocks with a required part take it in the constructor: `new Heading(text)`, `new Paragraph(text or inlines)`, `new Table(columns)`, `new Image(source)`, `new Spacer(height)`, `new PageBand(content)`. Inlines are strings, `TextRun`, `LineBreak.Value` and `Field.PageNumber`/`Field.PageCount` (drawn in headers and footers only: `Footer = new PageBand([new Paragraph(["Page ", Field.PageNumber, " of ", Field.PageCount])])`).
- Templates: `new Paragraph(["Dear ", new Bind("customer.name")])`, `new Repeat("notes", [...])`, `new Conditional("overdue", [...])` (JSON `"type": "when"`), with `Data = PdfData.Empty.WithModel(json)` or `PdfData.ParseJson(dataJson)`. A list item whose only content is a `Repeat` repeats the item. Table rows from a model array: `Rows = TableRows.Repeat("lines", [new Cell([new Bind("item")]), new Cell([new Bind("amount") { Format = "N2" }])])` (JSON `"rows": { "repeat": "lines", "cells": [{ "bind": "item" }, ...] }`). No `{{ }}` in text.
- Text boxes: `new TextBox([new Paragraph("Kari Nordmann"), ...]) { Preset = TextBoxPreset.Din5008B }` puts paragraphs at a fixed place (from the paper's top-left; or `X`, `Y`, `Width`, `Height`), outside the flow, on the page the content has reached. `Overflow`: `ShrinkToFit` (default, down to `MinSize`, Info L005), `Clip` (whole lines, warning L006), `Visible`. Paragraphs in a box have no space after them unless set. Follow it with a `Spacer` so the flow starts below it.
- Links: `new Link(["our terms"]) { Target = "https://example.com/terms", Description = "Terms of sale" }`; targets `https://`, `http://`, `mailto:` or `#id` (a block's `Id`). In headings, paragraphs and list items, not in table cells, text boxes or bands yet. JSON `{ "link": "#appendix", "inlines": ["Appendix"] }`.
- Page numbering: `document with { Numbering = PageNumberStyle.LowerRoman }` for the first section; `new SectionBreak { Numbering = PageNumberStyle.Number, StartAt = 1, Prefix = "A-" }` starts a page with a new numbering, and with `Header`/`Footer` its own bands (null keeps them, `PageBand.None` removes them: a cover without a footer is the document without one, then `new SectionBreak { StartAt = 1, Footer = footer }`). Fields and the table of contents show the labels; the table of contents lists headings after it.
- Table of contents: `new TableOfContents { Title = "Contents", MaxLevel = 2 }` anywhere at the top level (usually after the title); one per document; entries from top-level headings, numbers filled in at the end.
- Bookmarks come from headings (`OutlineLevels`, default 3). `new Heading("Appendix B") { Id = "appendix-b" }` makes a named destination; ids go on top-level blocks only, unique.
- Watermarks: `document with { Watermark = new Watermark("DRAFT") { Opacity = 0.15, Layer = WatermarkLayer.Behind } }`, or `new Watermark(ImageSource.FromSlot("logo"))`.
- Table cells span with `new Cell("Total") { ColumnSpan = 3 }` or `RowSpan`; a row counts each cell's span, and leaves out columns a row span from above covers.
- Lengths: `Length.Pt/Mm/Cm/In`, or strings like `"2cm"` in JSON. Column widths: `ColumnWidth.Star(2)` / `"2*"`, `ColumnWidth.Fixed(...)`.
- Data that changes per render is a slot: `TableRows.Bound("lines")` / `{"$data": "lines"}`, `ImageSource.FromSlot("logo")` with `Data = PdfData.Empty.WithImage("logo", bytes)`. Documents never name files. Rows and images can also come from JSON: `PdfData.ParseJson("""{ "lines": [["1", "Consulting"]], "logo": { "base64": "..." } }""")`. Rows are filled at render time: `Data = PdfData.Empty.WithRows("lines", items, item => [item.Name, item.Amount.ToString("N2")])`, from `IEnumerable<T>` or `IAsyncEnumerable<T>`.
- `PdfDocument.TryParseJson` never throws; `Validate()` never throws for a bad document. Both return diagnostics with a code (`docs/diagnostics.md`) and a JSON pointer.
- The JSON Schema is `PdfJsonSchema.Current`; `PdfDocument.ToJson()` writes `$schema`. Unknown JSON properties are errors.
- Rendering needs fonts: `new RenderOptions { Fonts = FontCollection.Empty.Add(new FontFamily("Inter") { Regular = FontSource.FromFile("Inter-Regular.ttf"), Bold = ..., Italic = ..., Fallbacks = ["Noto Sans Symbols"] }) }`. No system fonts unless `FontResolver = FontResolvers.System()`. A folder of fonts: `Fonts = FontCollection.FromFolder("fonts").WithDefault("Inter")`. A face of a collection: `FontSource.FromFile("YuGothR.ttc", face: 0)`. `await document.RenderAsync(stream, options, ct)`; `Render(stream, options)` for sync callers.
- Styles: `new TextRun("bold") { Style = TextStyle.Bold }`; a style sets font family, size, weight, style, colour, line height and language (`Language = "de"` for a passage in another language: `/Lang` on the block, or a Span for a run). A missing face is synthesised (ZPDF-F003 Info).
- Agent tools: `PdfTools.Create(new PdfToolOptions { Render = renderOptions, SaveAsync = ... })` in `Zorg.Pdf.Extensions.AI` gives `get_document_schema`, `validate_document`, `render_document` and `get_capabilities` as `AIFunction`s, and `render_preview` (a page as PNG) when `PreviewAsync` is set, usually to `PdfPreview.RenderPagePngAsync` from `Zorg.Pdf.Imaging`. The host owns fonts and storage; the model sends document and data JSON.
- Command line: `zorg-pdf render doc.json --data data.json --fonts fonts/ --pdfua`, `zorg-pdf validate doc.json --json`, `zorg-pdf watch doc.json --preview`, `zorg-pdf preview doc.json --page 1`, `zorg-pdf schema` (`src/Zorg.Pdf.Tool`, package `Zorg.Pdf.Tool`). Exit codes: 0 done, 1 document problems, 2 command line or files.
- Factur-X/ZUGFeRD: `FacturXInvoice fx = FacturX.Read(xml)`, then `fx.ApplyTo(document)` and `fx.ApplyTo(options)` (`Zorg.Pdf.Invoicing`); CLI `--facturx invoice.xml`. The XML comes from the invoicing system; Zorg.Pdf doesn't make it. Extra XMP: `RenderOptions.MetadataSchemas`.
- Attachments: `PdfDocument.Attachments = [new Attachment("invoice.xml", ImageSource.FromSlot("xml")) { MimeType = "application/xml", Relationship = AttachmentRelationship.Data }]` with `PdfData.WithFile`; under PDF/A use `PdfArchive.PdfA3b`/`3u`/`3a`.
- Merge and split: `PdfFiles.MergeAsync(sources, output, options)` and `ExtractAsync(file, "1-3", output)`; pages are copied by `Reading.PageCopier`. The output isn't tagged. CLI: `zorg-pdf merge`, `extract`, `split`.
- Letterhead: `PdfDocument.Background = new PageBackground(ImageSource.FromSlot("letterhead")) { OtherPages = 2 }` and `PdfData.WithPdf("letterhead", bytes)`; the page margins must leave room for it. Copied by `Reading.CosImporter`, drawn by `Rendering.BackgroundSet`.
- Reading PDFs: `PdfFileInfo.Read(bytes)` (any PDF: pages, sizes, metadata, tagged, encrypted; damaged files are recovered with ZPDF-P diagnostics; `PdfReadLimits` bounds the work). Internally `Zorg.Pdf.Reading.CosFile` is the object reader the coming letterhead and merge features build on. `zorg-pdf info file.pdf`.
- Markdown: `MarkdownDocument.Parse(text)` in `Zorg.Pdf.Markdown` gives a `PdfDocument` and diagnostics (ZPDF-M*); images become slots named by their path (`result.Images` lists them). `zorg-pdf render notes.md` works too, and `ParseMarkdown = m => MarkdownDocument.Parse(m).ToParseResult()` on `PdfToolOptions` adds the `render_markdown` agent tool. Raw HTML is dropped.
- Hyphenation: a style with `Hyphens = Hyphens.Auto` and `RenderOptions.Hyphenator = PatternHyphenator.Default` (`Zorg.Pdf.Hyphenation`: en, de, fr, nb, nn, da); the language is the text's or the document's. Without a hyphenator only soft hyphens (U+00AD) break words.
- Images: JPEG and PNG are read by Zorg.Pdf itself. Anything more (other formats, EXIF orientation, colour conversion, shrinking photos) goes through `RenderOptions.ImageProcessor`; `ImageProcessor = new ImagingProcessor()` from `Zorg.Pdf.Imaging` (built on Zorg.Imaging) does all of that. The core library takes no image dependency.
- A render checks everything it can before writing (validation, fonts, glyphs, control characters) and throws `PdfDocumentException` with every problem; warnings come back in `RenderResult.Diagnostics`. Output is always tagged; `Conformance = PdfConformance.PdfUA1` also claims PDF/UA-1, and `Archive = PdfArchive.PdfA2a` (or `PdfA2u`, `PdfA2b`) claims PDF/A-2 for archiving; both can be set.

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
