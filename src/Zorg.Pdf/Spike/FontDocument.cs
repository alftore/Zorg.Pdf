using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Spike;

/// <summary>
/// Spike slice S3: lines of text on one A4 page in an embedded, subset TrueType font. Any script the font covers
/// works, though there's no shaping yet: Arabic shows unjoined letters in logical order, for example.
/// </summary>
internal static class FontDocument
{
    private const double PageWidth = 595.2756;
    private const double PageHeight = 841.8898;
    private const double Margin = 72;

    public static async ValueTask WriteAsync(Stream output, ReadOnlyMemory<byte> fontFile, IReadOnlyList<string> lines, double size = 14, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var font = new EmbeddedFont(TrueTypeFont.Parse(fontFile));

        // Encode first: the font subset depends on every glyph the page uses.
        double leading = font.LineHeight * size;
        var content = new ContentStreamBuilder().BeginText().SetFont("F1"u8, size).MoveText(Margin, PageHeight - Margin - (font.Font.LayoutAscender * size / font.Font.UnitsPerEm));
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                content.MoveText(0, -leading);
            }

            content.ShowGlyphs(font.Encode(lines[i]));
        }

        content.EndText();

        using var writer = new PdfObjectWriter(output, PdfVersion.Pdf17);
        PdfReference catalog = writer.Allocate();
        PdfReference pages = writer.Allocate();
        PdfReference page = writer.Allocate();
        PdfReference contents = writer.Allocate();
        PdfReference type0 = writer.Allocate();

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
        writer.Reference(type0);
        writer.EndDictionary();
        writer.EndDictionary();
        writer.Name("Contents"u8);
        writer.Reference(contents);
        writer.EndDictionary();
        writer.EndObject();

        writer.BeginStreamObject(contents);
        writer.EndStreamObject(content.WrittenSpan, compress: true);
        await writer.FlushIfNeededAsync(cancellationToken).ConfigureAwait(false);

        font.Write(writer, type0);
        await writer.FinishAsync(catalog, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
