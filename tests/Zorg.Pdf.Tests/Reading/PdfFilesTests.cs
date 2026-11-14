using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Reading;
using Zorg.Pdf.Tests.Rendering;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Reading;

/// <summary>Merging, extracting and rewriting existing PDFs: pages copied whole, links kept between copied pages.</summary>
public sealed class PdfFilesTests
{
    [Fact]
    public async Task FilesMergeInOrderWithTheChosenPagesAndNewMetadata()
    {
        byte[] a = await Render("A", 2, PageSize.A4);
        byte[] b = await Render("B", 3, PageSize.Letter);

        (byte[] merged, PdfFileResult result) = await Merge([new PdfPageSource(a), new PdfPageSource(b) { Pages = "3,1" }], new PdfFileOptions { Title = "Pack", Author = "Kari" });
        (byte[] again, _) = await Merge([new PdfPageSource(a), new PdfPageSource(b) { Pages = "3,1" }], new PdfFileOptions { Title = "Pack", Author = "Kari" });

        Assert.Equal(4, result.PageCount);
        Assert.Equal(merged, again); // Deterministic.
        PdfFile.CheckStructure(merged);
        PdfFileInfo info = PdfFileInfo.Read(merged);
        Assert.Equal(("Pack", "Kari", "nb", false), (info.Title, info.Author, info.Language, info.IsTagged));
        Assert.Equal([595, 595, 612, 612], info.Pages.Select(p => Math.Round(p.Width)));
        Assert.Equal(["A1", "A2", "B3", "B1"], Texts(merged));
        Assert.Contains(result.Diagnostics, d => d.Code == "ZPDF-E001" && d.Path == "file 1"); // Tags aren't kept.
    }

    [Fact]
    public async Task LinksBetweenCopiedPagesAreKeptAndOthersDropped()
    {
        var document = new PdfDocument("Linked")
        {
            Content =
            [
                new Paragraph(["See ", new Link(["the appendix"]) { Target = "#appendix" }, " or ", new Link(["the web"]) { Target = "https://example.com" }]),
                new PageBreak(), new Paragraph("Middle"), new PageBreak(),
                new Heading("Appendix") { Id = "appendix" },
            ],
        };
        (byte[] file, _) = await RenderTests.RenderAsync(document, PdfInspect.Options());

        (byte[] both, PdfFileResult kept) = await Merge([new PdfPageSource(file) { Pages = "1,3" }]);
        (byte[] alone, PdfFileResult dropped) = await Merge([new PdfPageSource(file) { Pages = "1" }]);

        Assert.DoesNotContain(kept.Diagnostics, d => d.Code == "ZPDF-E004");
        var cos = new CosFile(both, PdfReadLimits.Default, []);
        var pages = CosPages.Read(cos, PdfReadLimits.Default, []);
        var annotations = ((CosArray)cos.Resolve(pages[0].Dictionary["Annots"])).Items.Select(i => (CosDictionary)cos.Resolve(i)).ToList();
        Assert.Equal(2, annotations.Count);
        var destination = (CosArray)cos.Resolve(annotations.Single(a => a["Dest"] is not null)["Dest"]);
        Assert.Equal(pages[1].ObjectNumber, ((CosReference)destination.Items[0]).Number); // Points at the copied appendix page.

        Assert.Contains(dropped.Diagnostics, d => d.Code == "ZPDF-E004");
        var lone = new CosFile(alone, PdfReadLimits.Default, []);
        Assert.Single(((CosArray)lone.Resolve(CosPages.Read(lone, PdfReadLimits.Default, [])[0].Dictionary["Annots"])).Items); // Only the web link.
    }

    [Fact]
    public async Task InheritedAttributesAreMadeExplicitAndMetadataRewritten()
    {
        string text = "%PDF-1.4\n1 0 obj << /Type /Catalog /Pages 2 0 R /Lang (de) >> endobj\n"
            + "2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 500] /Rotate 90 /Resources << >> >> endobj\n"
            + "3 0 obj << /Type /Page /Parent 2 0 R >> endobj\n4 0 obj << /Title (Alt) /Author (Old) >> endobj\ntrailer << /Root 1 0 R /Info 4 0 R >>\n%%EOF";
        byte[] file = Encoding.Latin1.GetBytes(text); // No cross-reference table: read by scanning.

        using var output = new MemoryStream();
        PdfFileResult result = await PdfFiles.ExtractAsync(file, null, output, new PdfFileOptions { Title = "Neu", ModificationDate = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero) }, TestContext.Current.CancellationToken);

        Assert.Contains(result.Diagnostics, d => d.Code == "ZPDF-P001");
        PdfFileInfo info = PdfFileInfo.Read(output.ToArray());
        Assert.Empty(info.Diagnostics);
        Assert.Equal(("Neu", "Old", "de"), (info.Title, info.Author, info.Language));
        Assert.Equal(new PdfPageInfo(1, 500, 300, 90), Assert.Single(info.Pages));
        Assert.Contains("/ModDate (D:20261115120000+00'00')", PdfInspect.Text(output.ToArray()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2-", new[] { 2, 3, 4 })]
    [InlineData("-2", new[] { 1, 2 })]
    [InlineData(" 4 , 1-2 ", new[] { 4, 1, 2 })]
    [InlineData(null, new[] { 1, 2, 3, 4 })]
    public void PageRangesAreReadInTheOrderGiven(string? text, int[] expected) =>
        Assert.Equal(expected, PageRanges.Parse(text, 4, "file 1").Select(p => p + 1));

    [Theory]
    [InlineData("0")]
    [InlineData("5")]
    [InlineData("3-2")]
    [InlineData("one")]
    public void BadRangesAreRefusedWithTheReason(string text) =>
        Assert.Throws<ArgumentException>(() => PageRanges.Parse(text, 4, "file 1"));

    [Fact]
    public async Task EncryptedAndEmptySelectionsAreRefused()
    {
        string text = "%PDF-1.4\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n3 0 obj << /Type /Page /MediaBox [0 0 10 10] >> endobj\ntrailer << /Root 1 0 R /Encrypt << /Filter /Standard >> >>\n%%EOF";

        await Assert.ThrowsAsync<PdfReadException>(() => Merge([new PdfPageSource(Encoding.Latin1.GetBytes(text))]));
        await Assert.ThrowsAsync<ArgumentException>(() => Merge([]));
    }

    private static async Task<(byte[] File, PdfFileResult Result)> Merge(PdfPageSource[] sources, PdfFileOptions? options = null)
    {
        using var output = new MemoryStream();
        PdfFileResult result = await PdfFiles.MergeAsync(sources, output, options, TestContext.Current.CancellationToken);
        return (output.ToArray(), result);
    }

    private static async Task<byte[]> Render(string name, int pages, PageSize size)
    {
        var document = new PdfDocument(name)
        {
            Language = "nb",
            Page = new PageSettings { Size = size },
            Content = [.. Enumerable.Range(1, pages).SelectMany(i => i < pages ? new Block[] { new Paragraph($"{name}{i}"), new PageBreak() } : [new Paragraph($"{name}{i}")])],
        };
        (byte[] file, _) = await RenderTests.RenderAsync(document, PdfInspect.Options(PdfConformance.PdfUA1));
        return file;
    }

    /// <summary>Each page's text, read through its content stream and the test font.</summary>
    private static string[] Texts(byte[] file) =>
        [.. PdfInspect.PageStreams(file).Select(p => string.Concat(p.SelectMany(s => SectionTestsText(s.Content))))];

    private static System.Collections.Generic.IEnumerable<string> SectionTestsText(string content)
    {
        Zorg.Pdf.Fonts.TrueTypeFont font = Zorg.Pdf.Fonts.TrueTypeFont.Parse(Zorg.Pdf.Tests.Fonts.TestFonts.Require());
        var characters = new System.Collections.Generic.Dictionary<string, char>();
        foreach (char c in Enumerable.Range(32, 95).Select(i => (char)i))
        {
            characters.TryAdd(font.GlyphFor(c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture), c);
        }

        return System.Text.RegularExpressions.Regex.Matches(content, @"(?:<([0-9A-F]+)> Tj|\[(.*?)\] TJ)").Select(m =>
            string.Concat(System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Success ? $"<{m.Groups[1].Value}>" : m.Groups[2].Value, @"<([0-9A-F]+)>")
                .SelectMany(h => h.Groups[1].Value.Chunk(4).Select(g => characters.TryGetValue(new string(g), out char c) ? c : '?'))));
    }
}
