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

### Fixed

- A GPOS extension lookup offset above `int.MaxValue` threw `OverflowException` instead of `PdfException`.
