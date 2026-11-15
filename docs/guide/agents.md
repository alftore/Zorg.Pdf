# Agents

Zorg.Pdf is built so an AI agent can produce good PDFs without a human fixing them:
- the whole document is one JSON model with a published schema;
- every problem comes back as a stable code with a JSON pointer to the part to fix;
- PNG previews let a model that sees images check its own layout.

## Tools for Microsoft.Extensions.AI

`Zorg.Pdf.Extensions.AI` gives ready-made `AIFunction` tools for any `IChatClient`:

| Tool | Does |
|---|---|
| `get_capabilities` | What this renderer can do, in a few lines |
| `get_document_schema` | The JSON Schema |
| `validate_document` | Checks a document and returns diagnostics with paths |
| `render_document` | Renders JSON to PDF and hands it to your `SaveAsync`, which returns a link or id |
| `render_markdown` | Renders Markdown, when `ParseMarkdown` is set |
| `render_preview` | Draws a page as PNG, when `PreviewAsync` is set |

```csharp
IList<AITool> tools = [.. PdfTools.Create(new PdfToolOptions
{
    Render = new RenderOptions { Fonts = fonts, Conformance = PdfConformance.PdfUA1 },
    SaveAsync = async (pdf, ct) => await storage.SaveAsync($"{pdf.Title}.pdf", pdf.Bytes, ct),
    ParseMarkdown = markdown => MarkdownDocument.Parse(markdown).ToParseResult(),
    PreviewAsync = (pdf, page, ct) => PdfPreview.RenderPagePngAsync(pdf, page, cancellationToken: ct),
})];
var response = await chatClient.GetResponseAsync(messages, new ChatOptions { Tools = tools }, cancellationToken);
```

Fonts, limits, data and storage stay on your side: the model chooses the content, not which files are read or where output goes. Documents can't name files, and every size is bounded by `PdfLimits`.

## Without the tools

Put the schema in a prompt with `PdfJsonSchema.Current`, parse the model's answer with `PdfDocument.TryParseJson`, and send the diagnostics back until it's clean:

```csharp
string schema = PdfJsonSchema.Current;
DocumentParseResult parsed = PdfDocument.TryParseJson(answer);
if (!parsed.IsSuccess)
{
    return parsed.Diagnostics;
}
```

A diagnostic reads like `error ZPDF-J002 at /content/1/colour: Unknown property 'colour'`, which models fix in one turn. Markdown needs even less: models write it fluently, and [the Markdown package](markdown.md) turns it into a tagged document.
