# Getting started

Zorg.Pdf targets .NET 10. Add the package:

```bash
dotnet add package Zorg.Pdf
```

## Fonts

Zorg.Pdf never looks for installed fonts on its own: what a document renders with is what you give it, so a server and a laptop produce the same bytes. Give it TrueType files (`.ttf`, or a face of a `.ttc`), one family at a time or a whole folder:

```csharp
FontCollection fonts = FontCollection.FromFolder("fonts").WithDefault("Inter");
```

A family's bold and italic faces come from the files in the folder. A missing face is synthesised, which the render reports as diagnostic ZPDF-F003.

## A first document

A document is a title, optional page settings, and a list of blocks. It renders to any stream, and pages are written as soon as they're laid out:

```csharp
var document = new PdfDocument("Hello")
{
    Content = [new Heading("Hello, PDF"), new Paragraph("Tagged, accessible and the same on every machine.")],
};
await using FileStream output = File.Create("hello.pdf");
RenderResult result = await document.RenderAsync(output, new RenderOptions { Fonts = fonts });
Console.WriteLine($"{result.PageCount} pages, {result.BytesWritten} bytes");
```

## The same document as JSON

Every document can also be written as JSON, which suits templates, tests and AI agents. The [JSON Schema](../../schema/document-0.1.schema.json) describes it, and editors that read `$schema` complete and check it as you type:

```json
{
  "$schema": "https://alftore.github.io/Zorg.Pdf/schema/document-0.1.schema.json",
  "title": "Hello",
  "content": [
    { "type": "heading", "inlines": ["Hello, PDF"] },
    { "type": "paragraph", "inlines": ["Tagged, accessible and the same on every machine."] }
  ]
}
```

Load JSON with `PdfDocument.ParseJson`. Check a document before rendering it: each problem has a stable code and a JSON pointer to the part it's about, listed in [diagnostics](../diagnostics.md).

```csharp
PdfDocument fromJson = PdfDocument.ParseJson(await File.ReadAllTextAsync("hello.json"));
foreach (PdfDiagnostic problem in fromJson.Validate().Diagnostics)
{
    Console.WriteLine($"{problem.Code} at {problem.Path}: {problem.Message}");
}
```

## From the command line

The `zorg-pdf` tool renders JSON and Markdown without writing code. `--preview` also draws each page as a PNG, which is the quickest way to check a layout:

```bash
dotnet tool install --global Zorg.Pdf.Tool
```

```bash
zorg-pdf render hello.json --fonts ./fonts --pdfua --preview
```

## Accessibility from the start

Output is tagged by default: headings, paragraphs, lists, tables and figures carry their structure for screen readers. Set `Conformance = PdfConformance.PdfUA1` to claim PDF/UA-1. Gaps such as an image without alt text then stop the render instead of passing as warnings. `Archive = PdfArchive.PdfA2a` adds PDF/A for long-term storage, and both can be claimed at once.

## Next

- [Design and layout](design.md): colour, cover pages, cards and striped tables.
- [docs/examples](../examples): fifteen documents to copy from, rendered and checked by the tests.
