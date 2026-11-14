using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Tests.Fonts;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

public sealed class RenderTests
{
    private static readonly PdfDocument Letter = new("Offer for consulting")
    {
        Language = "en-GB",
        Content =
        [
            new Heading("Offer for consulting"),
            new Paragraph("Thank you for a good conversation. We offer to help with your reporting, from templates and data to finished, accessible PDF files.") { Align = TextAlign.Justify },
            new Heading("Price") { Level = 2 },
            new Paragraph(["Advice: 1 450,00 kr per hour", LineBreak.Value, "Development: 1 250,00 kr per hour"]),
            new Spacer(Length.Cm(1)),
            new Paragraph("Blåbærsyltetøy AB, Kungsgatan 12, 111 43 Stockholm") { Align = TextAlign.Right },
        ],
    };

    [Fact]
    public async Task RendersATaggedDocument()
    {
        (byte[] file, RenderResult result) = await RenderAsync(Letter);

        PdfFile.CheckStructure(file);
        Assert.Equal(1, result.PageCount);
        Assert.Equal(file.Length, result.BytesWritten);
        Assert.False(result.HasWarnings); // Only Info: the headings use a synthetic bold.
        string text = PdfInspect.Text(file);
        Assert.Single(PdfInspect.Elements(file, "H1"));
        Assert.Single(PdfInspect.Elements(file, "H2"));
        Assert.Equal(3, PdfInspect.Elements(file, "P").Count);
        Assert.Contains("/MarkInfo <</Marked true>> /Lang (en-GB)", text, StringComparison.Ordinal);
        Assert.Contains("/Info ", text, StringComparison.Ordinal);
        Assert.Contains("<</Title (Offer for consulting) /Producer (Zorg.Pdf ", text, StringComparison.Ordinal);
        Assert.Contains("<pdf:Producer>Zorg.Pdf ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pdfuaid", text, StringComparison.Ordinal); // Only claimed when asked.
        Assert.DoesNotContain("CreationDate", text, StringComparison.Ordinal); // Deterministic by default.
    }

    [Fact]
    public async Task PdfUa1WritesTheIdentifier()
    {
        (byte[] file, _) = await RenderAsync(Letter, PdfInspect.Options(PdfConformance.PdfUA1));
        Assert.Contains("<pdfuaid:part>1</pdfuaid:part>", PdfInspect.Text(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncAndAsyncGiveTheSameBytes()
    {
        (byte[] asynchronous, _) = await RenderAsync(Letter);
        (byte[] synchronous, RenderResult result) = RenderSync(Letter);

        Assert.Equal(asynchronous, synchronous);
        Assert.Equal(1, result.PageCount);
    }

    private static (byte[] File, RenderResult Result) RenderSync(PdfDocument document)
    {
        using var output = new MemoryStream();
        RenderResult result = document.Render(output, PdfInspect.Options());
        return (output.ToArray(), result);
    }

    [Fact]
    public void SyncRenderNeverCallsAsyncWrites()
    {
        var output = new SyncOnlyStream();
        Letter.Render(output, PdfInspect.Options());
        Assert.True(output.Length > 1000);
    }

    [Fact]
    public async Task ParagraphsFlowOverPagesWithMarkedContentPerPage()
    {
        PdfDocument document = Letter with { Content = [.. Enumerable.Range(1, 80).Select(i => (Block)new Paragraph($"Paragraph {i}. Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua."))] };

        (byte[] file, RenderResult result) = await RenderAsync(document);

        Assert.True(result.PageCount >= 3, $"{result.PageCount} pages");
        List<int[]> parentTree = PdfInspect.ParentTree(file);
        Assert.Equal(result.PageCount, parentTree.Count);
        string[] pages = PdfInspect.PageContents(file);
        for (int page = 0; page < pages.Length; page++)
        {
            Assert.Equal(parentTree[page].Length, System.Text.RegularExpressions.Regex.Count(pages[page], @"/P <</MCID \d+>> BDC"));
        }
    }

    [Fact]
    public async Task AParagraphOverAPageBreakIsOneElementWithContentOnBothPages()
    {
        string sentences = string.Concat(Enumerable.Repeat("Lorem ipsum dolor sit amet, consectetur adipiscing elit. ", 300));
        (byte[] file, RenderResult result) = await RenderAsync(Letter with { Content = [new Paragraph(sentences)] });

        Assert.True(result.PageCount >= 2);
        int paragraph = Assert.Single(PdfInspect.Elements(file, "P"));
        Assert.All(PdfInspect.ParentTree(file), page => Assert.Equal([paragraph], page));
        Assert.Contains("/Type /MCR /Pg", PdfInspect.Text(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KeepWithNextHoldsABlockWithTheNextOnesFirstLine(bool keep)
    {
        // Push a one-line block down the page step by step. With keep-with-next it must never be the last thing on a
        // page while the next block starts on the following one; without it, that must happen at some point.
        bool everSplit = false;
        for (int filler = 30; filler < 70; filler++)
        {
            PdfDocument document = Letter with
            {
                Content =
                [
                    .. Enumerable.Range(0, filler).Select(i => (Block)new Paragraph($"Line {i}") { SpaceAfter = Length.Pt(0) }),
                    new Paragraph("Totals") { KeepWithNext = keep, SpaceAfter = Length.Pt(0) },
                    new Paragraph("12 450,00 NOK"),
                ],
            };

            (byte[] file, _) = await RenderAsync(document);
            List<int> paragraphs = PdfInspect.Elements(file, "P");
            int kept = paragraphs[^2];
            int next = paragraphs[^1];
            List<int[]> pages = PdfInspect.ParentTree(file);
            int keptPage = pages.FindIndex(p => p.Contains(kept));
            int nextPage = pages.FindIndex(p => p.Contains(next));
            if (keep)
            {
                Assert.True(keptPage == nextPage, $"With {filler} lines, the kept block is on page {keptPage + 1} and the next on page {nextPage + 1}.");
            }

            everSplit |= keptPage != nextPage;
        }

        Assert.Equal(!keep, everSplit);
    }

    [Fact]
    public async Task HeadingsKeepWithTheirParagraph()
    {
        for (int filler = 40; filler < 60; filler++)
        {
            PdfDocument document = Letter with
            {
                Content = [.. Enumerable.Range(0, filler).Select(i => (Block)new Paragraph($"Line {i}") { SpaceAfter = Length.Pt(0) }), new Heading("Section") { Level = 2 }, new Paragraph("Body")],
            };

            (byte[] file, _) = await RenderAsync(document);
            List<int[]> pages = PdfInspect.ParentTree(file);
            int headingPage = pages.FindIndex(p => p.Contains(PdfInspect.Elements(file, "H2")[0]));
            int bodyPage = pages.FindIndex(p => p.Contains(PdfInspect.Elements(file, "P")[^1]));
            Assert.Equal(headingPage, bodyPage);
        }
    }

    [Fact]
    public async Task PageBreaksAndSpacersAtTheTopOfAPageDoNothing()
    {
        PdfDocument document = Letter with { Content = [new PageBreak(), new Spacer(Length.Cm(5)), new Paragraph("First"), new PageBreak(), new PageBreak(), new Paragraph("Second")] };
        (byte[] file, RenderResult result) = await RenderAsync(document);

        Assert.Equal(2, result.PageCount);
        Assert.Contains("1 0 0 1 56.6929 ", PdfInspect.PageContents(file)[0], StringComparison.Ordinal); // "First" at the top margin, not pushed down.
    }

    [Fact]
    public async Task AnEmptyDocumentHasOneBlankPage()
    {
        (byte[] file, RenderResult result) = await RenderAsync(new PdfDocument("Empty"));
        Assert.Equal(1, result.PageCount);
        PdfFile.CheckStructure(file);
    }

    [Fact]
    public async Task LandscapeSwapsTheMediaBox()
    {
        (byte[] file, _) = await RenderAsync(Letter with { Page = PageSettings.A4 with { Orientation = PageOrientation.Landscape } });
        Assert.Contains("/MediaBox [0 0 841.8898 595.2756]", PdfInspect.Text(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StyleColourAndSizeAreApplied()
    {
        PdfDocument document = Letter with
        {
            Styles = new StyleSheet { Styles = System.Collections.Immutable.ImmutableDictionary<string, TextStyle>.Empty.Add("note", new TextStyle { Size = Length.Pt(7), Color = Color.Parse("#1F6FEB") }) },
            Content = [new Paragraph("Note") { Style = "note" }],
        };

        string page = PdfInspect.PageContents((await RenderAsync(document)).File)[0];
        Assert.Contains("/F1 7 Tf", page, StringComparison.Ordinal);
        Assert.Contains("0.1216 0.4353 0.9216 rg", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACreationDateGoesInInfoAndXmp()
    {
        RenderOptions options = PdfInspect.Options();
        options = new RenderOptions { Fonts = options.Fonts, CreationDate = new DateTimeOffset(2026, 11, 14, 13, 5, 9, TimeSpan.FromHours(1)) };
        string text = PdfInspect.Text((await RenderAsync(Letter, options)).File);

        Assert.Contains("/CreationDate (D:20261114130509+01'00')", text, StringComparison.Ordinal);
        Assert.Contains("<xmp:CreateDate>2026-11-14T13:05:09+01:00</xmp:CreateDate>", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsProgressPerPage()
    {
        var reports = new List<RenderProgress>();
        RenderOptions options = PdfInspect.Options();
        options = new RenderOptions { Fonts = options.Fonts, Progress = new SyncProgress(reports.Add) };
        PdfDocument document = Letter with { Content = [new Paragraph("One"), new PageBreak(), new Paragraph("Two"), new PageBreak(), new Paragraph("Three")] };

        await RenderAsync(document, options);

        Assert.Equal([1, 2, 3], reports.Select(r => r.PagesWritten));
    }

    [Fact]
    public async Task StopsAtTheMaxPagesLimit()
    {
        RenderOptions options = PdfInspect.Options();
        options = new RenderOptions { Fonts = options.Fonts, Limits = new PdfLimits { MaxPages = 2 } };
        PdfDocument document = Letter with { Content = [new Paragraph("1"), new PageBreak(), new Paragraph("2"), new PageBreak(), new Paragraph("3")] };

        PdfLimitException error = await Assert.ThrowsAsync<PdfLimitException>(() => RenderAsync(document, options));
        Assert.Contains("MaxPages", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HonoursCancellation()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Letter.RenderAsync(new MemoryStream(), PdfInspect.Options(), cancel.Token).AsTask());
    }

    [Fact]
    public async Task FontsFromFilesAreRead()
    {
        string path = Path.Combine(Path.GetTempPath(), $"zorg-pdf-{Guid.NewGuid():N}.ttf");
        await File.WriteAllBytesAsync(path, TestFonts.Require(), TestContext.Current.CancellationToken);
        try
        {
            var options = new RenderOptions { Fonts = FontCollection.Empty.Add(new FontFamily("Body") { Regular = FontSource.FromFile(path) }) };
            (byte[] fromFile, _) = await RenderAsync(Letter, options);
            (byte[] fromBytes, _) = await RenderAsync(Letter);
            Assert.Equal(fromBytes, fromFile);
        }
        finally
        {
            File.Delete(path);
        }
    }

    internal static async Task<(byte[] File, RenderResult Result)> RenderAsync(PdfDocument document, RenderOptions? options = null)
    {
        using var output = new MemoryStream();
        RenderResult result = await document.RenderAsync(output, options ?? PdfInspect.Options(), TestContext.Current.CancellationToken);
        return (output.ToArray(), result);
    }

    /// <summary>IProgress that reports on the calling thread, unlike Progress&lt;T&gt;.</summary>
    private sealed class SyncProgress(Action<RenderProgress> report) : IProgress<RenderProgress>
    {
        public void Report(RenderProgress value) => report(value);
    }

    private sealed class SyncOnlyStream : MemoryStream
    {
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new InvalidOperationException("Async write in a sync render.");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Async write in a sync render.");

        public override Task FlushAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Async flush in a sync render.");
    }
}
