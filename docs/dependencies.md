# Dependencies

The core library has no runtime dependencies, and that is a design rule. Every package used to build, test or benchmark is reviewed for licence and for future risk before it's added: a package can change licence, add fees, or lose its maintainer. PDFsharp's move from MIT to AGPL in 6.5.0 (October 2026) is a recent example. Add a row here in the same change as the package.

Rules:

- No runtime package in `src/Zorg.Pdf`. If one ever seems needed, it's a design decision first.
- Add-on packages take only what their name says (for example `Zorg.Pdf.Extensions.AI` takes `Microsoft.Extensions.AI.Abstractions`), each with a row here.
- No package with a fee, a commercial tier gating needed features, or a licence declaration step, even in tests or benchmarks.
- Check the licence in the `.nuspec` of the exact version. Don't rely on memory.
- Fonts and hyphenation patterns used in tests or shipped with the library need their licence recorded here too.

## Build only (not shipped)

| Package | Licence | Why | Risk |
|---|---|---|---|
| Microsoft.CodeAnalysis.PublicApiAnalyzers 5.6.0 | MIT | Tracks the public API | Low. Removing it changes nothing at run time. |

## Tests (not shipped)

| Package | Licence | Why | Risk |
|---|---|---|---|
| xunit.v3 4.0.2 | Apache-2.0 | Test framework | Low. |
| coverlet.MTP 10.1.0 | MIT | Coverage | Low. |

## Benchmarks (not shipped)

| Package | Licence | Why | Risk |
|---|---|---|---|
| BenchmarkDotNet 0.15.8 | MIT (same version and review as Zorg.Imaging) | Measures speed and allocations | Low. Its dependencies stay in the benchmark project; nothing reaches `src/`. |
| PDFsharp-MigraDoc 6.2.4 (and PDFsharp 6.2.4) | MIT (checked in both `.nuspec` files, 2026-11-14) | The managed baseline: the same invoice through MigraDoc's layout | Medium, contained. 6.2.4 is the **last MIT release**; 6.5.0 and later are AGPL-3.0 or commercial, so Dependabot ignores `>= 6.5.0` and the version must never be raised. Benchmarks only, never referenced from `src/` or `tests/`, and its source is never read (clean-room rule). |
| System.Security.Cryptography.Pkcs 10.0.15 | MIT | Pinned up from the 8.0.1 MigraDoc asks for, which has advisories GHSA-2fmv-rv2c-59x7 and GHSA-846f-f764-76mx | Low. Benchmarks only. |

## Considered and rejected

| Package | Reason |
|---|---|
| QuestPDF | Source-available Community licence with a revenue gate. Not usable as a dependency. As a benchmark baseline it needs a licence decision first. |
| PDFsharp / MigraDoc 6.5+ | AGPL-3.0 or commercial, with a runtime licence check. |
| iText | AGPL-3.0 or commercial. |
| FluentAssertions, Moq, Verify | Licence changes or a licence declaration step in recent versions (same decision as Zorg.Ssh and Zorg.Imaging). |
| Microsoft.Testing.Extensions.CodeCoverage | Proprietary Microsoft terms (same decision as Zorg.Ssh); `coverlet.MTP` instead. |
