using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Layout;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Spike;

/// <summary>One paragraph for the spike document.</summary>
internal sealed record SpikeParagraph(string Text, TextAlign Align = TextAlign.Left, double Size = 11, double SpaceAfter = 8);

/// <summary>
/// Spike slice S4: paragraphs wrapped to the page width, with kerning and alignment, on one A4 page. Pagination is
/// S5; text that doesn't fit one page throws.
/// </summary>
internal static class ParagraphDocument
{
    private const double PageWidth = 595.2756;
    private const double PageHeight = 841.8898;
    private const double Margin = 72;

    /// <summary>Writes the page and returns what layout reported (words broken inside, for example).</summary>
    public static async ValueTask<IReadOnlyList<TextDiagnostic>> WriteAsync(Stream output, ReadOnlyMemory<byte> fontFile, IReadOnlyList<SpikeParagraph> paragraphs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        var font = new EmbeddedFont(TrueTypeFont.Parse(fontFile));
        double width = PageWidth - (2 * Margin);
        var diagnostics = new List<TextDiagnostic>();

        var content = new ContentStreamBuilder().BeginText();
        double x = Margin;
        double y = PageHeight - Margin;
        double lastX = 0;
        double lastY = 0;
        double currentSize = 0;
        foreach (SpikeParagraph paragraph in paragraphs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ParagraphLayout layout = ParagraphLayout.Create(paragraph.Text, font, paragraph.Size, width, paragraph.Align);
            diagnostics.AddRange(layout.Diagnostics);
            if (paragraph.Size != currentSize)
            {
                content.SetFont("F1"u8, paragraph.Size);
                currentSize = paragraph.Size;
            }

            double leading = font.LineHeight * paragraph.Size;
            double ascent = font.Font.LayoutAscender * paragraph.Size / font.Font.UnitsPerEm;
            double baseline = y - ascent;
            foreach (LayoutLine line in layout.Lines)
            {
                if (baseline < Margin)
                {
                    throw new PdfException("The text doesn't fit on one page. Pagination comes in spike slice S5.");
                }

                // Td moves relative to the start of the previous line.
                double lineX = x + line.Offset;
                content.MoveText(lineX - lastX, baseline - lastY);
                (lastX, lastY) = (lineX, baseline);
                content.ShowGlyphs(line.Glyphs, line.ExtraAfter);
                baseline -= leading;
            }

            y = baseline + ascent - paragraph.SpaceAfter;
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
        font.Write(writer, type0);
        await writer.FinishAsync(catalog, cancellationToken: cancellationToken).ConfigureAwait(false);
        return diagnostics;
    }
}
