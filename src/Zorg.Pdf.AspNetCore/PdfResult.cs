using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Zorg.Pdf.AspNetCore;

/// <summary>Results that render a <see cref="PdfDocument"/> into the response.</summary>
public static class PdfResults
{
    /// <summary>
    /// Renders <paramref name="document"/> straight into the response body as its pages finish, without buffering the
    /// file. A document that can't be rendered becomes a 422 ProblemDetails with its diagnostics before any PDF byte
    /// is sent; a client that leaves cancels the render.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="options">Fonts, data, conformance and the rest; the same for every request is fine.</param>
    /// <param name="fileName">The name to save it under. Any characters: non-ASCII names are sent as RFC 6266 <c>filename*</c>.</param>
    /// <param name="inline">Shows the PDF in the browser instead of downloading it.</param>
    /// <example>
    /// <code>
    /// app.MapGet("/invoices/{id}.pdf", async (int id, InvoiceService invoices, CancellationToken cancellationToken) =>
    ///     PdfResults.Pdf(await invoices.DocumentAsync(id, cancellationToken), options with { Data = await invoices.DataAsync(id, cancellationToken) }, $"Faktura {id}.pdf"));
    /// </code>
    /// </example>
    public static PdfResult Pdf(PdfDocument document, RenderOptions options, string? fileName = null, bool inline = false) =>
        new(document, options, fileName, inline);
}

/// <summary>A PDF rendered into the response. Made by <see cref="PdfResults.Pdf"/>.</summary>
public sealed partial class PdfResult : IResult, IContentTypeHttpResult
{
    internal PdfResult(PdfDocument document, RenderOptions options, string? fileName, bool inline)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        FileName = fileName;
        Inline = inline;
    }

    /// <summary>The document to render.</summary>
    public PdfDocument Document { get; }

    /// <summary>The render options.</summary>
    public RenderOptions Options { get; }

    /// <summary>The file name for <c>Content-Disposition</c>, or null for none.</summary>
    public string? FileName { get; }

    /// <summary>True: shown in the browser. False: downloaded.</summary>
    public bool Inline { get; }

    /// <summary><c>application/pdf</c>.</summary>
    public string ContentType => "application/pdf";

    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        HttpResponse response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentType;
        response.Headers.XContentTypeOptions = "nosniff";
        if (FileName is not null || Inline)
        {
            var disposition = new ContentDispositionHeaderValue(Inline ? "inline" : "attachment");
            if (FileName is not null)
            {
                disposition.SetHttpFileName(FileName);
            }

            response.Headers.ContentDisposition = disposition.ToString();
        }

        try
        {
            // Nothing reaches the body until the checks before the first page have passed.
            await Document.RenderAsync(response.Body, Options, httpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            // The client left; there's no one to answer.
        }
        catch (PdfDocumentException exception) when (!response.HasStarted)
        {
            response.Clear();
            await TypedResults.Problem(Problem(exception)).ExecuteAsync(httpContext).ConfigureAwait(false);
        }
        catch (Exception exception) when (response.HasStarted)
        {
            // The status and part of the file are gone; a clean error is impossible, so the client mustn't take a
            // truncated file for a whole one.
            ILogger? logger = httpContext.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger<PdfResult>();
            if (logger is not null)
            {
                LogAbortedAfterStart(logger, exception, Document.Title);
            }

            httpContext.Abort();
        }
    }

    /// <summary>422 with each diagnostic's code, severity, message, JSON pointer and page.</summary>
    internal static ProblemDetails Problem(PdfDocumentException exception)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (PdfDiagnostic diagnostic in exception.Diagnostics)
            {
                writer.WriteStartObject();
                writer.WriteString("code", diagnostic.Code);
                writer.WriteString("severity", diagnostic.Severity switch
                {
                    DiagnosticSeverity.Error => "error",
                    DiagnosticSeverity.Warning => "warning",
                    _ => "info",
                });
                writer.WriteString("message", diagnostic.Message);
                writer.WriteString("path", diagnostic.Path);
                if (diagnostic.Page is { } page)
                {
                    writer.WriteNumber("page", page);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        using JsonDocument diagnostics = JsonDocument.Parse(buffer.ToArray());
        return new ProblemDetails
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "The document can't be rendered.",
            Detail = exception.Message,
            Extensions = { ["diagnostics"] = diagnostics.RootElement.Clone() },
        };
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Rendering \"{Title}\" failed after the response started; the connection was aborted.")]
    private static partial void LogAbortedAfterStart(ILogger logger, Exception exception, string title);
}
