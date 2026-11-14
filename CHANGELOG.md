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

### Removed

- The spike's single-page documents (`HelloDocument`, `FontDocument`, `ParagraphDocument`), replaced by the public render API. The spike's table renderer stays until tables move to the engine.

### Fixed

- A GPOS extension lookup offset above `int.MaxValue` threw `OverflowException` instead of `PdfException`.
