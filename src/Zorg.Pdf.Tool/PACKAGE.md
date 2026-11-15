# zorg-pdf

The command-line tool for [Zorg.Pdf](https://www.nuget.org/packages/Zorg.Pdf): render JSON documents and Markdown to tagged PDF, check them, render again on every save while you edit, and inspect, merge and split existing PDFs.

**Pre-release: the options will change before 1.0.**

```
dotnet tool install --global Zorg.Pdf.Tool
```

```
zorg-pdf render invoice.json --data invoice-data.json --pdfua --pdfa 2a
zorg-pdf validate invoice.json
zorg-pdf watch letter.json --fonts ./fonts
zorg-pdf preview letter.json --page 1 --dpi 96
zorg-pdf render notes.md --pdfua
zorg-pdf render invoice.json --data data.json --facturx factur-x.xml --pdfa 3a
zorg-pdf info contract.pdf
zorg-pdf merge cover.pdf contract.pdf:2-5 -o pack.pdf --title "Signing pack"
zorg-pdf extract report.pdf --pages 1-3 -o summary.pdf
zorg-pdf split scans.pdf -o pages
zorg-pdf schema > document.schema.json
```

- **render** writes the PDF beside the document (or to `--output`, `-` for standard output), replacing the old file only once the new one is complete.
- **validate** checks the document and its data without rendering. Problems are printed as `file:/json/pointer: error CODE: message`, or as JSON with `--json`.
- **preview** draws a page as PNG (`--page all` for every page, `--dpi` for the size), from a document or from a PDF that Zorg.Pdf wrote. `render --preview` and `watch --preview` write PNGs beside the PDF.
- **Markdown**: a document ending in `.md` is read as Markdown (CommonMark with GitHub tables and task lists), its images from beside it.
- **Archiving and e-invoices**: `--pdfa 2a|2u|2b|3a|3u|3b` claims PDF/A; `--facturx invoice.xml` embeds a Factur-X/ZUGFeRD CII XML and claims PDF/A-3.
- **info** shows any PDF's version, pages, sizes and metadata, and whether it's tagged or encrypted (`--json` too). **merge**, **extract** and **split** copy pages into new files, with `--title`, `--author`, `--subject`, `--keywords` and `--lang` for new metadata; files encrypted without a password are read too.
- **watch** renders, then renders again whenever the document, its data, its images or its fonts change. A broken save prints the problem and keeps the last good PDF.
- **Fonts** come from `--fonts <folder>` and `--font <file.ttf>`. Without either, installed fonts are used, with Arial, Liberation Sans or DejaVu Sans as the default; pass `--fonts` for output that's the same on every machine.
- **Images and files** for data slots (images, a letterhead PDF, attachments) come from `--image logo=logo.png` (or `--file`) or from the data JSON. WebP, GIF, BMP and TIFF work, through Zorg.Pdf.Imaging.

Exit codes: 0 done, 1 problems in the document or its data (or warnings, with `--warnaserror`), 2 a wrong command line or a file that couldn't be read or written. Run `zorg-pdf --help` for every option.

## Documentation

- [Document examples](https://github.com/alftore/Zorg.Pdf/tree/main/docs/examples)
- [Diagnostic codes](https://github.com/alftore/Zorg.Pdf/blob/main/docs/diagnostics.md)
- [Known limits](https://github.com/alftore/Zorg.Pdf/blob/main/docs/limits.md)
