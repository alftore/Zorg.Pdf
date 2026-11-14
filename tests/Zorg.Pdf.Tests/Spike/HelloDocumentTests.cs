using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Spike;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Spike;

public sealed class HelloDocumentTests
{
    [Fact]
    public async Task WritesOnePageWithAPageTreeAndFont()
    {
        byte[] file = await WriteAsync("Hello, PDF (spike)");

        Assert.Equal(6, PdfFile.CheckStructure(file).Count); // Free entry plus five objects.
        string text = PdfFile.Text(file);
        Assert.Contains("<</Type /Catalog /Pages 2 0 R>>", text, StringComparison.Ordinal);
        Assert.Contains("/MediaBox [0 0 595.2756 841.8898]", text, StringComparison.Ordinal);
        Assert.Contains("/BaseFont /Helvetica /Encoding /WinAnsiEncoding", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsDeterministic() =>
        Assert.Equal(await WriteAsync("Blåbærsyltetøy"), await WriteAsync("Blåbærsyltetøy"));

    [Fact]
    public async Task RefusesTextOutsideWinAnsiWithTheIndex()
    {
        PdfException error = await Assert.ThrowsAsync<PdfException>(() => WriteAsync("abل"));
        Assert.Contains("U+0644 at index 2", error.Message, StringComparison.Ordinal);
    }

    private static async Task<byte[]> WriteAsync(string text)
    {
        using var output = new MemoryStream();
        await HelloDocument.WriteAsync(output, text, TestContext.Current.CancellationToken);
        return output.ToArray();
    }
}
