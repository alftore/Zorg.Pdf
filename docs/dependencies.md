# Dependencies

The core library has no runtime dependencies, and that is a design rule. Every package used to build, test or benchmark is reviewed for licence and for future risk before it's added: a package can change licence, add fees, or lose its maintainer. PDFsharp's move from MIT to AGPL in 6.5.0 (October 2026) is a recent example. Add a row here in the same change as the package.

Rules:

- No runtime package in `src/Zorg.Pdf`. If one ever seems needed, it's a design decision first.
- Add-on packages take only what their name says (for example `Zorg.Pdf.Extensions.AI` takes `Microsoft.Extensions.AI.Abstractions`), each with a row here. The `zorg-pdf` tool (`Zorg.Pdf.Tool`) takes only Zorg.Pdf and its add-on packages, and parses its own command line.
- No package with a fee, a commercial tier gating needed features, or a licence declaration step, even in tests or benchmarks.
- Check the licence in the `.nuspec` of the exact version. Don't rely on memory.
- Fonts and hyphenation patterns used in tests or shipped with the library need their licence recorded here too.

## Add-on packages (shipped)

| Package | Licence | Why | Risk |
|---|---|---|---|
| Zorg.Imaging 0.4.0 | MIT (checked in the `.nuspec`, 2026-11-14); no dependencies on `net10.0` | `Zorg.Pdf.Imaging` only: decoding, orientation, colour conversion and resizing of images, and its experimental canvas (`ZORGDRAW`) for page previews | Low. The maintainer's own library, under the same rules (managed only, AOT-safe, no dependencies). First-party packages don't wait out the 7-day age rule. |
| ICC sRGB profile `sRGB2014.icc` (embedded in `Zorg.Pdf` as a resource, not a package) | The ICC's terms as Qt's and Debian's attributions quote them: the profile may be "copied, distributed, embedded, made, used, and sold without restriction"; altered versions must drop the original identification. Copyright tag: "Copyright International Color Consortium, 2015". From registry.color.org/rgb-registry/profiles/sRGB2014.icc, 3,024 bytes, SHA-256 `384b832d…6c0a`, checked by a test so it stays unchanged | The output intent of PDF/A files (`RenderOptions.Archive`) | Low. 3 KB of data, no code. The ICC's general-terms page itself wasn't found; the registry page refers to it |
| hyph-utf8 hyphenation patterns (in `Zorg.Pdf.Hyphenation`, as files, not packages): hyph-en-us, hyph-en-gb, hyph-de-1996, hyph-fr, hyph-no, hyph-nb, hyph-nn, hyph-da `.tex` | Read in each file's header, 2026-11-14: en-gb, de-1996 and fr MIT; da MIT or LPPL 1.3+; en-us, no, nb and nn "copying and distribution … with or without modification, are permitted … provided the copyright notice and this notice are preserved". Shipped unchanged (`-text` in `.gitattributes`), notices intact. From github.com/hyphenation/tex-hyphen master, 2026-11-14; SHA-256 recorded in `.tmp`-free form in `src/Zorg.Pdf.Hyphenation/Patterns/SHA256SUMS` | Hyphenating words in text with `hyphens: auto` | Low. Data, no code; the licences ask only that the notices stay. Swedish (LPPL 1.2) is left out until decided |
| Microsoft.Extensions.AI.Abstractions 10.11.0 | MIT (checked in the `.nuspec`, 2026-11-14); no dependencies on `net10.0` | `Zorg.Pdf.Extensions.AI` only: the `AIFunction` type its tools are | Low. Microsoft's abstraction for AI tools, used by every .NET AI client. 11.0.0 was four days old when this was added, so it waits. |
| Markdig 1.6.0 | BSD-2-Clause (checked in the `.nuspec`, 2026-11-14; published 2026-11-04); no dependencies on `net8.0` and later | `Zorg.Pdf.Markdown` only, and the `zorg-pdf` tool through it: parsing CommonMark and GitHub-flavoured Markdown | Low. The standard .NET Markdown parser, permissive licence, managed only, no dependencies. Only its syntax tree is used, never its HTML output. |

## Build only (not shipped)

| Package | Licence | Why | Risk |
|---|---|---|---|
| Microsoft.CodeAnalysis.PublicApiAnalyzers 5.6.0 | MIT | Tracks the public API | Low. Removing it changes nothing at run time. |

## Tests (not shipped)

| Package | Licence | Why | Risk |
|---|---|---|---|
| xunit.v3 4.0.2 | Apache-2.0 | Test framework | Low. |
| coverlet.MTP 10.1.0 | MIT | Coverage | Low. |

## Fonts (not shipped)

No font file is committed. Tests and the smoke run use a font installed on the machine (Arial on Windows, DejaVu Sans on Linux, Noto Sans Devanagari in the Linux gate) and record nothing of it.

| Font | Licence | Why | Risk |
|---|---|---|---|
| Liberation Sans 2.1.5 (`liberation-fonts-ttf-2.1.5.tar.gz`) | SIL Open Font License 1.1 | The CI job that checks output is byte-identical on Windows and Linux needs the same font on both. Downloaded in CI from the upstream release and checked against SHA-256 `7191c669bf38899f73a2094ed00f7b800553364f90e2637010a69c0e268f25d0`, published separately in Homebrew's `font-liberation` cask | Low. A changed file fails the job rather than changing what it checks. |

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
