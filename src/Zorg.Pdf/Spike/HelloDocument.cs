using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Spike;

/// <summary>
/// Spike slice S2: one A4 page of text in Helvetica, one of the 14 standard fonts, through the object writer. Not
/// accessible and not archivable (the font isn't embedded); it proves the page tree and content streams.
/// </summary>
internal static class HelloDocument
{
    /// <summary>A4 in points.</summary>
    private const double PageWidth = 595.2756;
    private const double PageHeight = 841.8898;

    public static async ValueTask WriteAsync(Stream output, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] encoded = EncodeWinAnsi(text);

        using var writer = new PdfObjectWriter(output, PdfVersion.Pdf17);
        PdfReference catalog = writer.Allocate();
        PdfReference pages = writer.Allocate();
        PdfReference page = writer.Allocate();
        PdfReference content = writer.Allocate();
        PdfReference font = writer.Allocate();

        writer.BeginObject(catalog);
        writer.BeginDictionary();
        writer.Name("Type"u8);
        writer.Name("Catalog"u8);
        writer.Name("Pages"u8);
        writer.Reference(pages);
        writer.EndDictionary();
        writer.EndObject();

        writer.BeginObject(pages);
        writer.BeginDictionary();
        writer.Name("Type"u8);
        writer.Name("Pages"u8);
        writer.Name("Kids"u8);
        writer.BeginArray();
        writer.Reference(page);
        writer.EndArray();
        writer.Name("Count"u8);
        writer.Integer(1);
        writer.EndDictionary();
        writer.EndObject();

        writer.BeginObject(page);
        writer.BeginDictionary();
        writer.Name("Type"u8);
        writer.Name("Page"u8);
        writer.Name("Parent"u8);
        writer.Reference(pages);
        writer.Name("MediaBox"u8);
        writer.BeginArray();
        writer.Integer(0);
        writer.Integer(0);
        writer.Real(PageWidth);
        writer.Real(PageHeight);
        writer.EndArray();
        writer.Name("Resources"u8);
        writer.BeginDictionary();
        writer.Name("Font"u8);
        writer.BeginDictionary();
        writer.Name("F1"u8);
        writer.Reference(font);
        writer.EndDictionary();
        writer.EndDictionary();
        writer.Name("Contents"u8);
        writer.Reference(content);
        writer.EndDictionary();
        writer.EndObject();

        ContentStreamBuilder stream = new ContentStreamBuilder()
            .BeginText()
            .SetFont("F1"u8, 24)
            .MoveText(72, PageHeight - 96)
            .ShowText(encoded)
            .EndText();
        writer.BeginStreamObject(content);
        writer.EndStreamObject(stream.WrittenSpan, compress: true);

        writer.BeginObject(font);
        writer.BeginDictionary();
        writer.Name("Type"u8);
        writer.Name("Font"u8);
        writer.Name("Subtype"u8);
        writer.Name("Type1"u8);
        writer.Name("BaseFont"u8);
        writer.Name("Helvetica"u8);
        writer.Name("Encoding"u8);
        writer.Name("WinAnsiEncoding"u8);
        writer.EndDictionary();
        writer.EndObject();

        await writer.FinishAsync(catalog, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Latin-1 printable characters map to the same WinAnsi codes. Anything else needs an embedded font (S3).</summary>
    private static byte[] EncodeWinAnsi(string text)
    {
        byte[] bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is < ' ' or (> '~' and < ' ') or > 'ÿ')
            {
                throw new PdfException($"Character U+{(int)c:X4} at index {i} isn't in WinAnsiEncoding. Only Latin-1 text works until fonts are embedded.");
            }

            bytes[i] = (byte)c;
        }

        return bytes;
    }
}
