using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Spike;
using Zorg.Pdf.Tests.Fonts;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Spike;

public sealed class FontDocumentTests
{
    private static readonly string[] Lines = ["Invoice 2026-0042", "Blåbærsyltetøy, 12 × 4,50 € = 54,00 €", "Ελληνικά · Кириллица"];

    [Fact]
    public async Task EmbedsASubsetType0Font()
    {
        byte[] file = await WriteAsync(Lines);

        PdfFile.CheckStructure(file);
        string text = PdfFile.Text(file);
        Assert.Contains("/Subtype /Type0", text, StringComparison.Ordinal);
        Assert.Contains("/Encoding /Identity-H", text, StringComparison.Ordinal);
        Assert.Contains("/Subtype /CIDFontType2", text, StringComparison.Ordinal);
        Assert.Contains("/CIDToGIDMap /Identity", text, StringComparison.Ordinal);
        Assert.Contains("/FontFile2", text, StringComparison.Ordinal);
        Assert.Matches(@"/BaseFont /[A-Z]{6}\+\S+", text);

        // Much smaller than the whole font: only the glyphs used are embedded.
        Assert.True(file.Length < TestFonts.Require().Length / 10, $"{file.Length} bytes is too large for a subset.");
    }

    [Fact]
    public async Task ToUnicodeMapsEveryGlyphBackToItsText()
    {
        byte[] file = await WriteAsync(["€ ø Ω"]);
        string cmap = Inflate(file, FindStream(file, "begincmap"));

        Assert.Contains("<20AC>", cmap, StringComparison.Ordinal); // €
        Assert.Contains("<00F8>", cmap, StringComparison.Ordinal); // ø
        Assert.Contains("<03A9>", cmap, StringComparison.Ordinal); // Ω
        Assert.Contains("<0020>", cmap, StringComparison.Ordinal);
        Assert.Contains("4 beginbfchar", cmap, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsDeterministicAndTheSubsetTagFollowsTheGlyphs()
    {
        byte[] first = await WriteAsync(Lines);
        byte[] second = await WriteAsync(Lines);
        byte[] other = await WriteAsync(["Something else"]);

        Assert.Equal(first, second);
        Assert.NotEqual(Tag(first), Tag(other));
    }

    private static async Task<byte[]> WriteAsync(string[] lines)
    {
        byte[] font = TestFonts.Require();
        using var output = new MemoryStream();
        await FontDocument.WriteAsync(output, font, lines, cancellationToken: TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static string Tag(byte[] file)
    {
        string text = PdfFile.Text(file);
        int at = text.IndexOf("/BaseFont /", StringComparison.Ordinal) + "/BaseFont /".Length;
        return text.Substring(at, 6);
    }

    /// <summary>The position of the compressed stream whose inflated text contains <paramref name="marker"/>.</summary>
    private static int FindStream(byte[] file, string marker)
    {
        string text = PdfFile.Text(file);
        for (int at = text.IndexOf("stream\n", StringComparison.Ordinal); at >= 0; at = text.IndexOf("stream\n", at + 1, StringComparison.Ordinal))
        {
            if (at > 2 && text[at - 1] != 'd') // Not "endstream".
            {
                int start = at + "stream\n".Length;
                try
                {
                    if (Inflate(file, start).Contains(marker, StringComparison.Ordinal))
                    {
                        return start;
                    }
                }
                catch (InvalidDataException)
                {
                    // Not a Flate stream.
                }
            }
        }

        Assert.Fail($"No stream contains '{marker}'.");
        return -1;
    }

    private static string Inflate(byte[] file, int start)
    {
        using var zlib = new ZLibStream(new MemoryStream(file, start, file.Length - start), CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        zlib.CopyTo(inflated);
        return Encoding.Latin1.GetString(inflated.ToArray());
    }
}
