using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Zorg.Pdf.Extensions.AI;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Imaging;
using Zorg.Pdf.Markdown;

#pragma warning disable IDE0059 // Unnecessary assignment of a value

namespace Zorg.Pdf.Tests.Guide;

/// <summary>agents.md's tools: a file of its own because System.Collections.Generic.List would clash with Zorg.Pdf.List.</summary>
internal static partial class GuideSnippets
{
    /// <summary>Where the reader's app keeps files.</summary>
    public interface IStorage
    {
        /// <summary>Saves a file and returns a link to it.</summary>
        public ValueTask<string> SaveAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    }

    // agents.md
    public static async Task AgentsAsync(FontCollection fonts, IStorage storage, IChatClient chatClient, IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        IList<AITool> tools = [.. PdfTools.Create(new PdfToolOptions
        {
            Render = new RenderOptions { Fonts = fonts, Conformance = PdfConformance.PdfUA1 },
            SaveAsync = async (pdf, ct) => await storage.SaveAsync($"{pdf.Title}.pdf", pdf.Bytes, ct),
            ParseMarkdown = markdown => MarkdownDocument.Parse(markdown).ToParseResult(),
            PreviewAsync = (pdf, page, ct) => PdfPreview.RenderPagePngAsync(pdf, page, cancellationToken: ct),
        })];
        var response = await chatClient.GetResponseAsync(messages, new ChatOptions { Tools = tools }, cancellationToken);
    }
}
