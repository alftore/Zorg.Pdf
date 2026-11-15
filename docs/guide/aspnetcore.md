# Serving PDFs from ASP.NET Core

`Zorg.Pdf.AspNetCore` has one result, `PdfResults.Pdf`. It renders a document straight into the response as its pages finish, so a large report never sits whole in memory, and it gets the parts that are easy to get wrong right.

```csharp
app.MapGet("/invoices/{id}.pdf", async (int id, InvoiceService invoices, CancellationToken cancellationToken) =>
    PdfResults.Pdf(await invoices.DocumentAsync(id, cancellationToken), options with { Data = await invoices.DataAsync(id, cancellationToken) }, $"Faktura {id}.pdf"));
```

`options` is your `RenderOptions` (fonts, conformance, a signature), made once at startup. Each request adds its own data with `with`.

## What it does for you

- **Headers:** `Content-Type: application/pdf` and `X-Content-Type-Options: nosniff`. `Content-Disposition` is `attachment`, or `inline` with `inline: true` to show the PDF in the browser. File names with any characters work: "Faktura blåbær 42.pdf" is sent as an ASCII fallback plus RFC 6266 `filename*`.
- **Streaming:** pages reach the client as they're written. Rows from an `IAsyncEnumerable` are read as the table is laid out, so the first page can be on its way while the database is still answering.
- **A clean error for a bad document:** every check that can fail a render (validation, fonts, missing characters, images, a signature's options) runs before the first byte. A failure there becomes a `422 Unprocessable Entity` ProblemDetails, with no PDF bytes and no file name. Its `diagnostics` list each problem's code, severity, message, JSON pointer and page:

```json
{
  "status": 422,
  "title": "The document can't be rendered.",
  "diagnostics": [
    { "code": "ZPDF-F001", "severity": "error", "message": "The render has no fonts, ...", "path": "" }
  ]
}
```

  If you've called `AddProblemDetails`, your customisations apply to it.
- **A client that leaves:** the render is cancelled through `HttpContext.RequestAborted`, so no work goes on for nobody.
- **A failure partway:** once the first bytes are sent, the status can't change any more. If something fails after that, such as a data source throwing on page 40, the connection is aborted and the error logged. The client sees a broken download, not a truncated file that looks whole.

Anything else that fails before the first byte is thrown as usual, so your exception handler answers it.

## Signed or timestamped

A signature in `options` works the same way. The file streams until its last part, which waits for the signer and the timestamp authority (see [digital signatures](signatures.md)).

## What isn't there yet

Report jobs, where a `POST` answers `202 Accepted` and the client fetches the file when it's ready, with progress, are planned. Until then, a long report can be rendered in your own background job (Hangfire, Quartz, a queue) with `RenderAsync` into a file or blob, and served from there.

The package follows ASP.NET Core's support for native AOT, but it isn't in the repository's AOT smoke test yet.
