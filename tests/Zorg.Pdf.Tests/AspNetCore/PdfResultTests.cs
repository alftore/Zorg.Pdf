using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;
using Zorg.Pdf.AspNetCore;
using Zorg.Pdf.Tests.Rendering;

namespace Zorg.Pdf.Tests.AspNetCore;

/// <summary><see cref="PdfResults.Pdf"/> in a real ASP.NET Core pipeline, over TestServer.</summary>
public sealed class PdfResultTests
{
    private static async Task<WebApplication> StartAsync(Action<WebApplication> map)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        WebApplication app = builder.Build();
        map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    [Fact]
    public async Task ThePdfStreamsWithItsHeadersAndTheSameBytesAsRenderAsync()
    {
        RenderOptions options = TableRenderTests.Data(120);
        await using WebApplication app = await StartAsync(a => a.MapGet("/invoice.pdf", () => PdfResults.Pdf(TableRenderTests.Invoice, options, "Faktura blåbær 42.pdf")));

        using HttpResponseMessage response = await app.GetTestClient().GetAsync(new Uri("/invoice.pdf", UriKind.Relative), TestContext.Current.CancellationToken);
        byte[] body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        string disposition = response.Content.Headers.GetValues("Content-Disposition").Single();
        Assert.StartsWith("attachment", disposition, StringComparison.Ordinal);
        Assert.Contains("filename*=UTF-8''Faktura%20bl%C3%A5b%C3%A6r%2042.pdf", disposition, StringComparison.Ordinal);
        Assert.Equal("Faktura blåbær 42.pdf", response.Content.Headers.ContentDisposition!.FileNameStar);
        (byte[] expected, _) = await RenderTests.RenderAsync(TableRenderTests.Invoice, TableRenderTests.Data(120));
        Assert.Equal(expected, body);
    }

    [Fact]
    public async Task InlineHasNoFileNameUnlessGivenOne()
    {
        await using WebApplication app = await StartAsync(a => a.MapGet("/view", () => PdfResults.Pdf(TableRenderTests.Invoice, TableRenderTests.Data(3), inline: true)));

        using HttpResponseMessage response = await app.GetTestClient().GetAsync(new Uri("/view", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal("inline", response.Content.Headers.GetValues("Content-Disposition").Single());
    }

    [Fact]
    public async Task ADocumentThatCantRenderIsA422WithItsDiagnosticsAndNoPdf()
    {
        await using WebApplication app = await StartAsync(a => a.MapGet("/broken.pdf", () => PdfResults.Pdf(new PdfDocument("No fonts") { Content = [new Paragraph("x")] }, RenderOptions.Default, "broken.pdf")));

        using HttpResponseMessage response = await app.GetTestClient().GetAsync(new Uri("/broken.pdf", UriKind.Relative), TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.False(response.Content.Headers.Contains("Content-Disposition"));
        using JsonDocument problem = JsonDocument.Parse(body);
        Assert.Equal(422, problem.RootElement.GetProperty("status").GetInt32());
        JsonElement diagnostic = problem.RootElement.GetProperty("diagnostics")[0];
        Assert.Equal("ZPDF-F001", diagnostic.GetProperty("code").GetString());
        Assert.Equal("error", diagnostic.GetProperty("severity").GetString());
        Assert.DoesNotContain("%PDF", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureAfterTheFirstByteAbortsTheResponse()
    {
        RenderOptions options = PdfInspect.Options() with { Data = PdfData.Empty.WithRows("lines", FailingRowsAsync(3000), TableRenderTests.Cells) };
        await using WebApplication app = await StartAsync(a => a.MapGet("/big.pdf", () => PdfResults.Pdf(TableRenderTests.Invoice, options)));

        // The headers arrive as a success; only the body breaks, so a client can't mistake it for a whole file.
        using HttpResponseMessage response = await app.GetTestClient().GetAsync(new Uri("/big.pdf", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Exception error = await Assert.ThrowsAnyAsync<Exception>(async () => await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("database", error.ToString(), StringComparison.Ordinal); // An abort, not the server's exception passed through.
    }

    [Fact]
    public async Task AClientThatLeavesCancelsTheRender()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RenderOptions options = PdfInspect.Options() with { Data = PdfData.Empty.WithRows("lines", EndlessRowsAsync(started, stopped, TestContext.Current.CancellationToken), TableRenderTests.Cells) };
        await using WebApplication app = await StartAsync(a => a.MapGet("/slow.pdf", () => PdfResults.Pdf(TableRenderTests.Invoice, options)));

        using var leave = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<HttpResponseMessage> request = app.GetTestClient().GetAsync(new Uri("/slow.pdf", UriKind.Relative), leave.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await leave.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheProblemListsEveryDiagnostic()
    {
        var exception = new PdfDocumentException([new PdfDiagnostic("ZPDF-J001", DiagnosticSeverity.Error, "Bad.", "/content/0"), new PdfDiagnostic("ZPDF-R001", DiagnosticSeverity.Warning, "Later.", "/content/1") { Page = 2 }]);
        Microsoft.AspNetCore.Mvc.ProblemDetails problem = PdfResult.Problem(exception);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.Status);
        var diagnostics = (JsonElement)problem.Extensions["diagnostics"]!;
        Assert.Equal(2, diagnostics.GetArrayLength());
        Assert.Equal("/content/0", diagnostics[0].GetProperty("path").GetString());
        Assert.Equal(("warning", 2), (diagnostics[1].GetProperty("severity").GetString(), diagnostics[1].GetProperty("page").GetInt32()));
    }

    private static async IAsyncEnumerable<int> FailingRowsAsync(int failAt)
    {
        for (int i = 1; ; i++)
        {
            await Task.Yield();
            if (i == failAt)
            {
                throw new InvalidOperationException("The database went away.");
            }

            yield return i;
        }
    }

    private static async IAsyncEnumerable<int> EndlessRowsAsync(TaskCompletionSource started, TaskCompletionSource<bool> stopped, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            for (int i = 1; ; i++)
            {
                if (i == 200)
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                yield return i;
            }
        }
        finally
        {
            stopped.TrySetResult(cancellationToken.IsCancellationRequested);
        }
    }
}
