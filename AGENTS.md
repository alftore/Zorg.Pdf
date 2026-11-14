# Working on Zorg.Pdf

Async-first PDF generation for .NET 10, in C# only. Early 0.1: the public API is the document model (`PdfDocument`, blocks, inlines, styles, units), its JSON form and validation. There's no public render API yet, so don't call `RenderAsync` or other types from the design notes as if they existed.

## Writing code that uses Zorg.Pdf

- A document is an immutable record tree: `new PdfDocument("Title") { Content = [new Heading("..."), new Paragraph("..."), new Table([new Column("A")]) { Rows = ... }] }`. Change it with `with`.
- Blocks with a required part take it in the constructor: `new Heading(text)`, `new Paragraph(text or inlines)`, `new Table(columns)`, `new Image(source)`, `new Spacer(height)`, `new PageBand(content)`. Inlines are strings, `TextRun`, `LineBreak.Value` and `Field.PageNumber`/`Field.PageCount`.
- Lengths: `Length.Pt/Mm/Cm/In`, or strings like `"2cm"` in JSON. Column widths: `ColumnWidth.Star(2)` / `"2*"`, `ColumnWidth.Fixed(...)`.
- Data that changes per render is a slot: `TableRows.Bound("lines")` / `{"$data": "lines"}`, `ImageSource.FromSlot("logo")`. Documents never name files.
- `PdfDocument.TryParseJson` never throws; `Validate()` never throws for a bad document. Both return diagnostics with a code (`docs/diagnostics.md`) and a JSON pointer.
- The JSON Schema is `PdfJsonSchema.Current`; `PdfDocument.ToJson()` writes `$schema`. Unknown JSON properties are errors.

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
