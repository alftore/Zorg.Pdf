# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/). Before 1.0, breaking changes are marked **Breaking**.

## [Unreleased]

### Added

- Repository scaffold: build settings, CI and release workflows, AOT smoke test, and `PdfException` as the base exception type.
- Internal spike (no public API yet): PDF object writer, TrueType parsing, subsetting and embedding, GPOS and `kern` kerning, line breaking and paragraph layout, multi-page flow with tables, "Page X of Y", and PDF/UA-1 tagging that passes veraPDF.
- Linux gate (`scripts/test-linux.sh`) and a CI job: tests with fonts and HarfBuzz required, qpdf, MuPDF, poppler and veraPDF checks.
- Benchmark project comparing the spike invoice with MigraDoc 6.2.4 (MIT).
- The document model: `PdfDocument`, `Heading`, `Paragraph`, `Table` (inline or data-bound rows), `Image` (bytes or data slot), `Spacer`, `PageBreak`, header and footer `PageBand`s, inlines (`TextRun`, `LineBreak`, `Field`), `TextStyle` and `StyleSheet`, and the units `Length`, `Thickness`, `Color`, `PageSize`, `PageSettings`.
- JSON: `PdfDocument.ToJson`, `ParseJson` and `TryParseJson`, source-generated (trim- and AOT-safe), with the JSON Schema in `PdfJsonSchema.Current` and `schema/`. Unknown properties are errors.
- `PdfDocument.Validate` and diagnostics with stable codes and JSON pointers (`docs/diagnostics.md`).
- Example documents in `docs/examples/`, validated by the tests and by an independent JSON Schema validator in the Linux gate.

- Rendering: `PdfDocument.RenderAsync` and `Render` draw headings, paragraphs, spacers and page breaks as tagged PDF, streaming pages as they finish. Headings and `KeepWithNext` blocks stay with the next block's first line. Sync and async renders give the same bytes.
- `RenderOptions` (fonts, `PdfConformance.PdfUA1`, `PdfLimits`, creation date, progress) and `RenderResult`. Fonts come from `FontCollection`, `FontFamily` and `FontSource`; there are no system fonts.
- Checks before writing: no fonts (ZPDF-F001), missing glyphs (F002), unknown families (F004), unreadable fonts (F005) and control characters (T002) stop a render before the first byte. Parts not drawn yet are warnings (R001).
- The Info dictionary and XMP now carry the same title, language, producer and optional dates; the PDF/UA identifier is written only when `PdfUA1` is claimed.

- Fonts: bold, italic and bold-italic faces on `FontFamily`, synthesised (thickened or slanted, ZPDF-F003 Info) when missing; styled text runs (face, size, colour) within a line; fallback families for missing characters; `IFontResolver` and `FontResolvers.System()` as an opt-in; parsed fonts shared across renders.
- A character no font has is now drawn as an empty box with warning ZPDF-F002 (an error under `PdfUA1`), not a failed render.

- Tables: star and fixed columns, a header row that repeats on each page (as an artifact, so it is read once), header fill, rules and cell padding through `TableStyle`, cell alignment, tagged as Table, TR, TH (with column scope) and TD.
- Table rows from data: `RenderOptions.Data = PdfData.Empty.WithRows("lines", rows)`, from `IEnumerable` or `IAsyncEnumerable`, read as the table is laid out. `PdfLimits.MaxTableRows` bounds them.
- Table warnings: a row taller than a page (ZPDF-L001), a data row with the wrong number of cells (L002), fixed columns wider than the page (L003).

- Headers and footers: `PdfDocument.Header` and `Footer` are drawn on every page as pagination artifacts, with room kept for them 12 pt from the content. `Field.PageNumber` and `Field.PageCount` show the real numbers, while pages still stream out as they finish. Built-in `header` and `footer` styles (8 pt). Bands that leave less than 36 pt for the content are an error (ZPDF-L004).

### Removed

- The spike (`Spike/`), replaced by the public render API.

### Fixed

- A GPOS extension lookup offset above `int.MaxValue` threw `OverflowException` instead of `PdfException`.
