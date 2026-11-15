using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Zorg.Pdf.AspNetCore;

namespace Zorg.Pdf.Tests.Guide;

/// <summary>aspnetcore.md, with a stand-in for the reader's invoice service.</summary>
internal static partial class GuideSnippets
{
    /// <summary>Stands for the reader's own service.</summary>
    public sealed class InvoiceService
    {
        /// <summary>The invoice's document.</summary>
        public Task<PdfDocument> DocumentAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();

        /// <summary>The invoice's rows.</summary>
        public Task<PdfData> DataAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // aspnetcore.md
    public static void AspNetCore(WebApplication app, RenderOptions options)
    {
        app.MapGet("/invoices/{id}.pdf", async (int id, InvoiceService invoices, CancellationToken cancellationToken) =>
            PdfResults.Pdf(await invoices.DocumentAsync(id, cancellationToken), options with { Data = await invoices.DataAsync(id, cancellationToken) }, $"Faktura {id}.pdf"));
    }
}
