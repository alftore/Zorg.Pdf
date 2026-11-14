# Zorg.Pdf.Markdown

Markdown for [Zorg.Pdf](https://www.nuget.org/packages/Zorg.Pdf): CommonMark and GitHub-flavoured Markdown become a tagged, accessible PDF. Notes, minutes, reports and READMEs, without writing a layout.

**Pre-release: the API will change before 1.0.**

```csharp
using Zorg.Pdf;
using Zorg.Pdf.Markdown;

MarkdownResult result = MarkdownDocument.Parse(await File.ReadAllTextAsync("minutes.md", cancellationToken));
PdfData data = PdfData.Empty;
foreach (string image in result.Images) // paths as written, such as "img/chart.png"
{
    data = data.WithImage(image, await File.ReadAllBytesAsync(Path.Combine("docs", image), cancellationToken));
}

await using FileStream output = File.Create("minutes.pdf");
await result.Document.RenderAsync(output, new RenderOptions { Fonts = fonts, Data = data }, cancellationToken);
```

- Headings, bold and italic, line breaks, bulleted and numbered lists (with their start number, nesting and task boxes), pipe tables with alignment, code blocks, quotes, rules, images on a line of their own and links.
- YAML front matter sets `title` and `lang`; without it, the first level 1 heading is the title.
- Images are data slots named by their path: nothing is read from disk or the network.
- Raw HTML is left out, with a warning. Everything simplified is reported with a `markdown:line:column` path.
- Parsing is by [Markdig](https://github.com/xoofx/markdig) (BSD-2-Clause).

## Documentation

- [Diagnostics](https://github.com/alftore/Zorg.Pdf/blob/main/docs/diagnostics.md) (ZPDF-M001 to M003)
- [Known limits](https://github.com/alftore/Zorg.Pdf/blob/main/docs/limits.md)
- [Changelog](https://github.com/alftore/Zorg.Pdf/blob/main/CHANGELOG.md)
