using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Validation;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// Everything a render can check before writing the first byte: which font families the text needs, that every
/// character has a glyph and can be drawn, and which parts this version doesn't draw yet. Errors stop the render with
/// a <see cref="PdfDocumentException"/>, so a server never sends half a file because of a problem it could have seen.
/// </summary>
internal static class Preflight
{
    /// <summary>The families the document's text blocks need, each with the path of the first block that needs it.</summary>
    public static Dictionary<string, string> NeededFamilies(PdfDocument document, StyleResolver styles, string? defaultFamily)
    {
        var needed = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < document.Content.Length; i++)
        {
            if (document.Content[i] is Heading or Paragraph)
            {
                string? family = styles.For(document.Content[i]).FontFamily ?? styles.DefaultFontFamily ?? defaultFamily;
                if (family is not null)
                {
                    needed.TryAdd(family, document.Content[i].Style is not null ? $"/content/{i}/style" : $"/content/{i}");
                }
            }
        }

        return needed;
    }

    public static void Check(PdfDocument document, StyleResolver styles, FontSet fonts, DiagnosticSink sink)
    {
        if (document.Header is not null)
        {
            sink.Warning(DiagnosticCodes.NotDrawnYet, "Headers aren't drawn yet in this version; the header is left out.", "/header");
        }

        if (document.Footer is not null)
        {
            sink.Warning(DiagnosticCodes.NotDrawnYet, "Footers aren't drawn yet in this version; the footer is left out.", "/footer");
        }

        for (int i = 0; i < document.Content.Length; i++)
        {
            string path = $"/content/{i}";
            switch (document.Content[i])
            {
                case Heading heading:
                    Text(heading.Inlines, Font(heading), $"{path}/inlines");
                    break;
                case Paragraph paragraph:
                    Text(paragraph.Inlines, Font(paragraph), $"{path}/inlines");
                    break;
                case Table:
                    sink.Warning(DiagnosticCodes.NotDrawnYet, "Tables aren't drawn yet in this version; the table is left out.", path);
                    break;
                case Image:
                    sink.Warning(DiagnosticCodes.NotDrawnYet, "Images aren't drawn yet in this version; the image is left out.", path);
                    break;
            }
        }

        EmbeddedFont? Font(Block block)
        {
            string? family = styles.For(block).FontFamily ?? styles.DefaultFontFamily ?? fonts.DefaultFamily;
            return family is not null && fonts.TryGet(family, out EmbeddedFont font) ? font : null;
        }

        void Text(System.Collections.Immutable.ImmutableArray<Inline> inlines, EmbeddedFont? font, string path)
        {
            for (int j = 0; j < inlines.Length; j++)
            {
                switch (inlines[j])
                {
                    case TextRun run:
                        if (run.Style is not null)
                        {
                            sink.Info(DiagnosticCodes.NotDrawnYet, "Styles on text runs aren't applied yet in this version; the run uses its paragraph's style.", $"{path}/{j}/style");
                        }

                        Characters(run.Text, font, $"{path}/{j}");
                        break;
                    case Field:
                        sink.Warning(DiagnosticCodes.NotDrawnYet, "Page number fields aren't drawn yet in this version; the field is left out.", $"{path}/{j}");
                        break;
                }
            }
        }

        void Characters(string text, EmbeddedFont? font, string path)
        {
            var reported = new HashSet<int>();
            int index = 0;
            foreach (Rune rune in text.EnumerateRunes())
            {
                int c = rune.Value;
                bool layoutControl = c is '\n' or '\r' or '\t' or 0x2028 or 0x2029 or 0x00AD or 0x200B;
                if (!layoutControl && (c < 0x20 || c is >= 0x7F and < 0xA0) && reported.Add(c))
                {
                    sink.Error(DiagnosticCodes.ControlCharacter, string.Create(CultureInfo.InvariantCulture, $"The control character U+{c:X4} at index {index} can't be drawn. Remove it; use a line feed (\\n) to break a line."), path);
                }
                else if (!layoutControl && font is not null && font.Font.GlyphFor(c) == 0 && reported.Add(c))
                {
                    sink.Error(DiagnosticCodes.MissingGlyph, string.Create(CultureInfo.InvariantCulture, $"The font '{font.Font.PostScriptName}' has no glyph for U+{c:X4} ('{rune}') at index {index}. Use a font that covers this text."), path);
                }

                index += rune.Utf16SequenceLength;
            }
        }
    }
}
