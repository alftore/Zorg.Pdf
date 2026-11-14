# Contributing

## How to contribute

Issues are welcome: bug reports, documents that render differently in other viewers or fail validation, feature requests, and questions about the documentation. Please use one of the issue forms. A bug report with the document definition (or template and data) and a few lines of code usually gets fixed fastest. Remove anything private first.

Pull requests are open to collaborators only. Templates and, later, PDF files are untrusted input for anyone who renders documents on a server, and one maintainer can't review a stranger's change well enough. That also keeps one person responsible for every change, its tests and its signed history. If you have a fix in mind, describe it or paste the patch in the issue, and it will be credited in the changelog.

Report security problems privately, as [SECURITY.md](SECURITY.md) describes, not in an issue. Check [docs/limits.md](docs/limits.md) for what isn't supported yet.

## Build and test

You need the .NET SDK version in `global.json`.

```
dotnet build Zorg.Pdf.slnx
dotnet test --solution Zorg.Pdf.slnx
```

The build treats warnings as errors, so it has to finish with 0 warnings.

## The Linux gate

Output is checked against reference tools in a Linux container, as in Zorg.Imaging. It needs Podman or Docker, and installs nothing on your machine.

```
scripts/test-linux.sh                 # build, test, check every PDF, validate PDF/UA-1 with veraPDF
scripts/test-linux.sh --keep out/     # also copy the PDFs the smoke sample wrote to out/
```

- **Tests** run with DejaVu Sans installed and `ZORG_REQUIRE_FONTS=1` and `ZORG_REQUIRE_ORACLES=1`, so a missing font or `hb-shape` fails instead of skipping. A run that passes by skipping the oracle tests proves nothing.
- **Every PDF** the smoke sample writes must pass `qpdf --check`, render in MuPDF without errors, have every font embedded, subset and mapped to Unicode (`pdffonts`), and give its text back through `pdftotext`.
- **The tagged invoice** must be PDF/UA-1 compliant in veraPDF, which runs from its own image, pinned by digest.

The tree goes in as a tar stream of the files git knows, so uncommitted edits are tested. NuGet packages are cached in the volume `zorg-pdf-nuget`. Set `ZORG_PDF_CONTAINER_CLI` if Podman or Docker isn't on `PATH`.

Font tests on Windows use Arial and skip without a font; `hb-shape` isn't usually installed there, so the HarfBuzz kerning test skips. The gate is where both run for certain.

## Clean-room rules

- Write from the specifications: PDF 2.0 (ISO 32000-2), PDF/UA (ISO 14289), PDF/A (ISO 19005), OpenType and TrueType, the Unicode algorithms (UAX #9 bidi, UAX #14 line breaking, UAX #29 segmentation), and JSON Schema.
- **Never read the source of QuestPDF** (source-available licence), **PDFsharp/MigraDoc 6.5 or later**, or **iText** (both AGPL).
- Permissively licensed projects (HarfBuzz, PdfPig, pdf.js, Typst) may be read for ideas. Say so in the file, and keep their notices if anything is ported.
- Reference tools are black boxes: compare outputs, never copy code.

## AOT smoke test

`samples/Zorg.Pdf.AotSmoke` is published with Native AOT and run. Trimming keeps only what's used, so add each new feature to it. It must publish with no trim or AOT warnings.

```
dotnet publish samples/Zorg.Pdf.AotSmoke -r win-x64 -c Release
samples/Zorg.Pdf.AotSmoke/bin/Release/net10.0/win-x64/publish/Zorg.Pdf.AotSmoke.exe
```

Windows needs the Visual Studio C++ workload. Use `linux-x64` on Linux (needs `clang` and `zlib1g-dev`).

## Conventions

- **XML docs on all public members**, with an `<example>`. Agents read them as much as people do.
- **No runtime dependencies** in the core package. The library references nothing but the BCL. Add-on packages take only what their name says. Test and benchmark packages go through the review in `docs/dependencies.md`.
- **Async first.** Anything that can do I/O is async, with `CancellationToken cancellationToken = default` as the last parameter, and `ConfigureAwait(false)` on every await. A sync overload exists only where the work is CPU-bound or in memory.
- **Untrusted input is normal input.** Templates, data and (later) PDF files are checked against limits before anything is allocated. Failures are `PdfException`-family types, never `IndexOutOfRangeException` and the like.
- **Errors say where and what next.** Diagnostics carry a JSON path or element path and a stable code.
- **Public API changes** update `src/Zorg.Pdf/PublicAPI.Unshipped.txt`. The build fails otherwise.
- **Vertical slices.** Tests and docs land in the same commit as the code.

## Commits

Signed, with no co-author or generator lines. The README covers how the library is built.
