using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Tests.Rendering;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Fonts;

/// <summary>TrueType collections (.ttc): each face can be chosen, embedded and found by family.</summary>
public sealed class FontCollectionFileTests
{
    [Fact]
    public async Task AFaceOfACollectionRendersLikeTheSameFontOnItsOwn()
    {
        byte[] font = TestFonts.Require();
        byte[] collection = Collection(font, faces: 2);
        var document = new PdfDocument("x") { Content = [new Paragraph("Blåbærsyltetøy, 42 ½.")] };

        byte[] single = await Render(document, FontSource.FromBytes(font));
        byte[] fromCollection = await Render(document, FontSource.FromBytes(collection, face: 1));

        Assert.Equal(2, TrueTypeFont.FaceCount(collection));
        PdfFile.CheckStructure(fromCollection);
        Assert.Equal(PdfInspect.PageContents(single)[0], PdfInspect.PageContents(fromCollection)[0]); // Same glyphs, same places.
        PdfDocumentException missing = await Assert.ThrowsAsync<PdfDocumentException>(() => Render(document, FontSource.FromBytes(collection, face: 2)));
        Assert.Contains("face 2 doesn't exist", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FacesOfACollectionFileAreFoundByFamily()
    {
        string folder = Directory.CreateTempSubdirectory("zorg-pdf-ttc-").FullName;
        try
        {
            string path = Path.Combine(folder, "pair.ttc");
            File.WriteAllBytes(path, Collection(TestFonts.Require(), faces: 2));

            FontCollection fonts = FontCollection.FromFolder(folder);

            FontFamily family = Assert.Single(fonts.Families.Values);
            Assert.Equal(FontSource.FromFile(path, 0), family.Regular); // The first of equal faces.
            Assert.Equal([0, 1], SystemFontResolver.DescribeFaces(path).Select(f => f.Face));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task<byte[]> Render(PdfDocument document, FontSource source)
    {
        (byte[] file, _) = await RenderTests.RenderAsync(document, new RenderOptions { Fonts = FontCollection.Empty.Add(new FontFamily("Body") { Regular = source }) });
        return file;
    }

    /// <summary>A collection whose faces all point at one copy of the font, its table offsets moved past the header.</summary>
    private static byte[] Collection(byte[] font, int faces)
    {
        int header = 12 + (faces * 4);
        byte[] ttc = new byte[header + font.Length];
        "ttcf"u8.CopyTo(ttc);
        BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(8), (uint)faces);
        for (int i = 0; i < faces; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(ttc.AsSpan(12 + (i * 4)), (uint)header);
        }

        font.CopyTo(ttc, header);
        int tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int t = 0; t < tables; t++)
        {
            Span<byte> offset = ttc.AsSpan(header + 12 + (t * 16) + 8, 4);
            BinaryPrimitives.WriteUInt32BigEndian(offset, BinaryPrimitives.ReadUInt32BigEndian(offset) + (uint)header);
        }

        return ttc;
    }
}
