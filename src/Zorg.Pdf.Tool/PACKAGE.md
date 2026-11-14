# zorg-pdf

The command-line tool for [Zorg.Pdf](https://www.nuget.org/packages/Zorg.Pdf): render JSON documents to tagged PDF, check them, and render again on every save while you edit.

**Pre-release: the options will change before 1.0.**

```
dotnet tool install --global Zorg.Pdf.Tool
```

```
zorg-pdf render invoice.json --data invoice-data.json --pdfua --pdfa 2a
zorg-pdf validate invoice.json
zorg-pdf watch letter.json --fonts ./fonts
zorg-pdf preview letter.json --page 1 --dpi 96
zorg-pdf schema > document.schema.json
```

- **render** writes the PDF beside the document (or to `--output`, `-` for standard output), replacing the old file only once the new one is complete.
- **validate** checks the document and its data without rendering. Problems are printed as `file:/json/pointer: error CODE: message`, or as JSON with `--json`.
- **preview** draws a page as PNG (`--page all` for every page, `--dpi` for the size), from a document or from a PDF that Zorg.Pdf wrote. `render --preview` and `watch --preview` write PNGs beside the PDF.
- **watch** renders, then renders again whenever the document, its data, its images or its fonts change. A broken save prints the problem and keeps the last good PDF.
- **Fonts** come from `--fonts <folder>` and `--font <file.ttf>`. Without either, installed fonts are used, with Arial, Liberation Sans or DejaVu Sans as the default; pass `--fonts` for output that's the same on every machine.
- **Images** for data slots come from `--image logo=logo.png` or from the data JSON. WebP, GIF, BMP and TIFF work, through Zorg.Pdf.Imaging.

Exit codes: 0 done, 1 problems in the document or its data (or warnings, with `--warnaserror`), 2 a wrong command line or a file that couldn't be read or written. Run `zorg-pdf --help` for every option.

## Documentation

- [Document examples](https://github.com/alftore/Zorg.Pdf/tree/main/docs/examples)
- [Diagnostic codes](https://github.com/alftore/Zorg.Pdf/blob/main/docs/diagnostics.md)
- [Known limits](https://github.com/alftore/Zorg.Pdf/blob/main/docs/limits.md)
