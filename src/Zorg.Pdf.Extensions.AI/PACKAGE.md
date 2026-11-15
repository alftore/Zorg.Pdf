# Zorg.Pdf.Extensions.AI

Tools for AI agents that write PDF documents with [Zorg.Pdf](https://www.nuget.org/packages/Zorg.Pdf), as `Microsoft.Extensions.AI` functions. Native AOT and trimming work: the tools use no reflection.

**Pre-release: the API will change before 1.0.**

| Tool | Does |
|---|---|
| `get_document_schema` | Returns the JSON Schema of a document, a short guide, and an example |
| `validate_document` | Checks a document and its data; returns every problem with a code and a JSON pointer |
| `render_document` | Renders a document with its data as tagged PDF; returns pages, size, warnings and where the file went |
| `get_capabilities` | Says what the host renders with: font families, conformance, PDF/A level, limits, and which optional tools exist |
| `render_preview` | Renders and returns one page as a PNG picture, so the model can check its layout by looking (when the host sets `PreviewAsync`, for example to Zorg.Pdf.Imaging's `PdfPreview`) |
| `render_markdown` | Renders Markdown as tagged PDF, for notes and reports that need no layout of their own (when the host sets `ParseMarkdown`, for example to Zorg.Pdf.Markdown) |

The model writes the document and its data. The host decides everything else: fonts, conformance, limits, and where rendered files go. The tools never read or write files themselves.

```csharp
using Microsoft.Extensions.AI;
using Zorg.Pdf;
using Zorg.Pdf.Extensions.AI;
using Zorg.Pdf.Fonts;

IList<AITool> tools = [.. PdfTools.Create(new PdfToolOptions
{
    Render = new RenderOptions
    {
        Fonts = FontCollection.Empty.Add(new FontFamily("Inter") { Regular = FontSource.FromFile("Inter-Regular.ttf") }),
        Conformance = PdfConformance.PdfUA1,
    },
    SaveAsync = async (pdf, ct) => await storage.SaveAsync($"{pdf.Title}.pdf", pdf.Bytes, ct),
})];

ChatResponse response = await chatClient.GetResponseAsync(messages, new ChatOptions { Tools = tools }, cancellationToken);
```

## Documentation

- [Example documents](https://github.com/alftore/Zorg.Pdf/tree/main/docs/examples), with their data
- [Diagnostic codes](https://github.com/alftore/Zorg.Pdf/blob/main/docs/diagnostics.md)
- [Changelog](https://github.com/alftore/Zorg.Pdf/blob/main/CHANGELOG.md)
