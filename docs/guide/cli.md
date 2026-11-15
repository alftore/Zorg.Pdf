# The command line

`zorg-pdf` renders JSON documents and Markdown, checks them, draws previews, and reads, merges and splits PDFs. Install it as a .NET tool:

```bash
dotnet tool install --global Zorg.Pdf.Tool
```

| Command | Does |
|---|---|
| `zorg-pdf render doc.json` | Renders to `doc.pdf`; `-o` names the output, `-` for standard output |
| `zorg-pdf validate doc.json` | Checks a document and its data without rendering |
| `zorg-pdf watch doc.json` | Renders, then again on every change |
| `zorg-pdf preview doc.json` | Draws a page as PNG (`--page 2`, `--page all`, `--dpi 150`) |
| `zorg-pdf info file.pdf` | Pages, sizes and metadata of any PDF |
| `zorg-pdf merge a.pdf b.pdf:2-4 -o out.pdf` | Puts pages of several PDFs into one |
| `zorg-pdf extract in.pdf --pages 1-3 -o out.pdf` | Takes pages out, or rewrites with new metadata |
| `zorg-pdf split in.pdf -o folder` | Writes each page as its own PDF |
| `zorg-pdf schema` | Prints the JSON Schema |

## Common options

- `-d, --data data.json`: table rows, images and the template model.
- `--image slot=file`: fills a slot with an image, a PDF (for a background) or any file (for an attachment). Repeatable.
- `--fonts folder`, `--font file.ttf` and `--default-font name`: the fonts to use. Without them the tool uses installed fonts, so output depends on the machine.
- `--pdfua`, `--pdfa 2a`: claim PDF/UA-1 and PDF/A.
- `--facturx invoice.xml`: make a Factur-X invoice.
- `--title`, `--author`, `--subject`, `--keywords` and `--lang`: metadata for Markdown, merge and extract.
- `--warnaserror`: exit with 1 on warnings, for build pipelines.
- `--json`: results as JSON lines, for scripts and agents.
- `--preview`: with render and watch, also writes PNG previews beside the PDF.

## Examples

```bash
zorg-pdf render docs/examples/invoice.json --data docs/examples/data/invoice.json --fonts ./fonts --pdfua
```

```bash
zorg-pdf watch docs/examples/annual-report.json --preview --page all
```

```bash
zorg-pdf render letter.json --image letterhead=letterhead.pdf --pdfa 2a
```

Diagnostics are printed as `error ZPDF-J002 at /content/1/colour: ...`, with the code and the JSON pointer, and they're listed in [diagnostics](../diagnostics.md).
