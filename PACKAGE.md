# Zorg.Pdf

Async-first PDF generation for .NET 10, written in C# only, with no native binaries and no dependencies. Native AOT and trimming work.

**Pre-release: the API will change before 1.0.**

- Build documents in C# or as JSON (with a published JSON Schema), validate them, and render them as tagged PDF that passes veraPDF PDF/UA-1, and PDF/A-2 or PDF/A-3 for archiving.
- Headings, paragraphs with bold, italic and fallback fonts, lists, tables with rows from `IAsyncEnumerable<T>`, JPEG and PNG images, headers and footers with "Page X of Y", watermarks, and text boxes for window-envelope addresses (DIN 5008, Norwegian).
- Documents that navigate: bookmarks from headings, links, tables of contents, sections with roman or lettered page numbers.
- Templates: values, repeated blocks and table rows bound from a JSON model.
- Charts (bar, stacked, line, pie) and barcodes (QR, Code 128, GS1-128, EAN-13) drawn as vectors; EPC payment QR payloads and Norwegian KID check digits.
- Existing PDFs: read any PDF's pages and metadata, put a page of letterhead behind every page, merge, extract and split, attach files (PDF/A-3), and read files that are encrypted but open without a password.
- Pages stream to the output while later pages are laid out. Output is deterministic: the same document, data and font give the same bytes on every OS.
- Every problem is a diagnostic with a stable code and a JSON pointer, and errors stop a render before the first byte.

Add-on packages: Zorg.Pdf.Imaging (more image formats, page previews), Zorg.Pdf.Hyphenation, Zorg.Pdf.Markdown, Zorg.Pdf.Invoicing (Factur-X and ZUGFeRD), Zorg.Pdf.Extensions.AI (tools for AI agents), and the `zorg-pdf` command-line tool (Zorg.Pdf.Tool).

## Quick start

```csharp
using Zorg.Pdf;
using Zorg.Pdf.Fonts;

var invoice = new PdfDocument("Invoice 2026-0042")
{
    Footer = new PageBand([new Paragraph(["Page ", Field.PageNumber, " of ", Field.PageCount]) { Align = TextAlign.Right }]),
    Content =
    [
        new Heading("Invoice 2026-0042"),
        new Paragraph(["Payment is due within ", new TextRun("30 days") { Style = TextStyle.Bold }, "."]),
        new Table([new Column("Description") { Width = ColumnWidth.Star(4) }, new Column("Amount") { Align = TextAlign.Right }])
        {
            Rows = TableRows.Bound("lines"),
        },
    ],
};

var options = new RenderOptions
{
    Fonts = FontCollection.Empty.Add(new FontFamily("Inter")
    {
        Regular = FontSource.FromFile("Inter-Regular.ttf"),
        Bold = FontSource.FromFile("Inter-Bold.ttf"),
    }),
    Data = PdfData.Empty.WithRows("lines", repository.StreamLinesAsync(id), line => [line.Description, line.Amount.ToString("N2")]),
    Conformance = PdfConformance.PdfUA1,
};

await invoice.RenderAsync(response.Body, options, cancellationToken);
```

The same document as JSON is `invoice.ToJson()`; read one with `PdfDocument.ParseJson`, and data with `PdfData.ParseJson`.

Existing files:

```csharp
PdfFileInfo info = PdfFileInfo.Read(bytes);                       // pages, sizes, title, tagged, encrypted
await PdfFiles.MergeAsync([new PdfPageSource(cover), new PdfPageSource(contract) { Pages = "2-" }], output);
var letter = document with { Background = new PageBackground(ImageSource.FromSlot("letterhead")) { OtherPages = 2 } };
```

## Documentation

- [Example documents](https://github.com/alftore/Zorg.Pdf/tree/main/docs/examples), with their data
- [Diagnostic codes](https://github.com/alftore/Zorg.Pdf/blob/main/docs/diagnostics.md)
- [Known limits](https://github.com/alftore/Zorg.Pdf/blob/main/docs/limits.md)
- [Changelog](https://github.com/alftore/Zorg.Pdf/blob/main/CHANGELOG.md)
- [Report a security problem](https://github.com/alftore/Zorg.Pdf/security/advisories/new)
