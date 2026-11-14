# Working on Zorg.Pdf

Async-first PDF generation for .NET 10, in C# only. Scaffold stage: the public API is only `PdfException`. Nothing else exists yet, so don't call types from the design notes as if they did.

## Working on the repo

```bash
dotnet build Zorg.Pdf.slnx -nologo -v q
```

```bash
dotnet test --solution Zorg.Pdf.slnx --no-build
```

- `CONTRIBUTING.md` has the conventions and the clean-room rules. The build fails on any warning.
- Commits are signed through `commit.gpgsign`. Never bypass it. If signing fails, stop and tell the maintainer.
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
