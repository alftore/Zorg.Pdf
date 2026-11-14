using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Fonts;

namespace Zorg.Pdf.Tests.Fonts;

/// <summary>Parsed fonts are shared, but a changed file or different bytes are never mistaken for the old ones.</summary>
public sealed class FontCacheTests
{
    [Fact]
    public async Task AnUnchangedFileIsParsedOnceAndAChangedOneAgain()
    {
        byte[] regular = TestFonts.Require();
        byte[] bold = TestFonts.Require("bold");
        string path = Path.Combine(Path.GetTempPath(), $"zorg-pdf-{Guid.NewGuid():N}.ttf");
        try
        {
            await File.WriteAllBytesAsync(path, regular, TestContext.Current.CancellationToken);
            FontSource source = FontSource.FromFile(path);
            TrueTypeFont first = await source.LoadAsync(false, TestContext.Current.CancellationToken);
            Assert.Same(first, await source.LoadAsync(true, TestContext.Current.CancellationToken));

            await File.WriteAllBytesAsync(path, bold, TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
            TrueTypeFont second = await source.LoadAsync(false, TestContext.Current.CancellationToken);
            Assert.NotSame(first, second);
            Assert.Same(FontCache.GetOrParse(bold), second); // Same content, same parsed font.
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ACopyOfTheBytesFindsTheSameFont()
    {
        byte[] font = TestFonts.Require();
        Assert.Same(FontCache.GetOrParse(font), FontCache.GetOrParse(font.AsSpan().ToArray()));
    }
}
