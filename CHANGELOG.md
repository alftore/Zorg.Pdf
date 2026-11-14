# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/). Before 1.0, breaking changes are marked **Breaking**.

## [Unreleased]

### Added

- Repository scaffold: build settings, CI and release workflows, AOT smoke test, and `PdfException` as the base exception type.
- Internal spike (no public API yet): PDF object writer, TrueType parsing, subsetting and embedding, GPOS and `kern` kerning, line breaking and paragraph layout, multi-page flow with tables, "Page X of Y", and PDF/UA-1 tagging that passes veraPDF.
- Linux gate (`scripts/test-linux.sh`) and a CI job: tests with fonts and HarfBuzz required, qpdf, MuPDF, poppler and veraPDF checks.
- Benchmark project comparing the spike invoice with MigraDoc 6.2.4 (MIT).
