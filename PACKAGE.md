# Zorg.Pdf

Async-first PDF generation for .NET 10, written in C# only, with no native binaries and no dependencies. Native AOT and trimming work.

**Pre-release: the API will change before 1.0.**

- Build documents in C# or as JSON (with a published JSON Schema), validate them, and render them as tagged PDF that passes veraPDF PDF/UA-1.
- Headings, paragraphs with bold, italic and fallback fonts, tables with rows from `IAsyncEnumerable<T>`, JPEG and PNG images, and headers and footers with "Page X of Y".
- Pages stream to the output while later pages are laid out. Output is deterministic: the same document, data and font give the same bytes on every OS.
- Every problem is a diagnostic with a stable code and a JSON pointer, and errors stop a render before the first byte.

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

## Documentation

- [Example documents](https://github.com/alftore/Zorg.Pdf/tree/main/docs/examples), with their data
- [Diagnostic codes](https://github.com/alftore/Zorg.Pdf/blob/main/docs/diagnostics.md)
- [Known limits](https://github.com/alftore/Zorg.Pdf/blob/main/docs/limits.md)
- [Changelog](https://github.com/alftore/Zorg.Pdf/blob/main/CHANGELOG.md)
- [Report a security problem](https://github.com/alftore/Zorg.Pdf/security/advisories/new)
