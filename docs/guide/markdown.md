# Markdown

`Zorg.Pdf.Markdown` turns CommonMark and GitHub-flavoured Markdown into a document: headings, paragraphs with bold, italic and code, lists, task lists, tables, quotes, code blocks, images and links. Everything is tagged as usual. It uses Markdig to parse.

```csharp
MarkdownResult result = MarkdownDocument.Parse("# Minutes\n\nPresent: **Kari**, Ola.\n\n- Budget\n- Hiring", new MarkdownOptions { Title = "Minutes 14 November", Language = "nb" });
await result.Document.RenderAsync(output, new RenderOptions { Fonts = fonts }, cancellationToken);
```

- **Diagnostics** say what was simplified or dropped, with a `markdown:line:column` path. For example, raw HTML is dropped, and a link a PDF can't use keeps its text without the link.
- **Images** become data slots named by their path (`![Logo](logo.png)` is slot `logo.png`), listed in `result.Images`, so nothing is read from disk unless you supply it: `PdfData.Empty.WithImage("logo.png", bytes)`.
- **The result is a normal document,** so you can add a header, a footer, a background or a watermark with `with` before rendering.

On the command line, a `.md` file renders directly, with images read from beside the file:

```bash
zorg-pdf render minutes.md --title "Minutes 14 November" --lang nb
```

Models write Markdown well, so this is also the easiest way for an AI agent to produce a PDF. See [agents](agents.md).
