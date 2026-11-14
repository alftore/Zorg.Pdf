using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Zorg.Pdf.Layout;
using Zorg.Pdf.Validation;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// Everything a render can check before writing the first byte: which font faces the text needs, that every
/// character has a glyph in its run's font chain and can be drawn, and which parts this version doesn't draw yet.
/// Errors stop the render with a <see cref="PdfDocumentException"/>, so a server never sends half a file because of a
/// problem it could have seen.
/// </summary>
internal static class Preflight
{
    /// <summary>The style of a run: its block's style with the run's own on top.</summary>
    public static TextStyle RunStyle(TextStyle block, Inline inline) => inline is TextRun { Style: { } own } ? block.Merge(own) : block;

    /// <summary>The face a style asks for, or null when no family is named anywhere.</summary>
    public static FaceKey? Face(TextStyle style, StyleResolver styles, string? defaultFamily) =>
        (style.FontFamily ?? styles.DefaultFontFamily ?? defaultFamily) is { } family
            ? new FaceKey(family, style.Weight ?? FontWeight.Normal, style.FontStyle ?? FontStyle.Normal)
            : null;

    /// <summary>
    /// Every face the text needs, each with the path of the first place that needs it: the block's own face (an empty
    /// paragraph still has a line height) and each run's.
    /// </summary>
    public static Dictionary<FaceKey, string> NeededFaces(PdfDocument document, StyleResolver styles, string? defaultFamily, DiagnosticSink sink)
    {
        var needed = new Dictionary<FaceKey, string>();
        bool reportedNoFamily = false;
        for (int i = 0; i < document.Content.Length; i++)
        {
            if (Inlines(document.Content[i]) is not { } inlines)
            {
                continue;
            }

            TextStyle block = styles.For(document.Content[i]);
            string blockPath = document.Content[i].Style is not null ? $"/content/{i}/style" : $"/content/{i}";
            Need(block, blockPath);
            for (int j = 0; j < inlines.Length; j++)
            {
                if (inlines[j] is TextRun { Style: not null })
                {
                    Need(RunStyle(block, inlines[j]), $"/content/{i}/inlines/{j}/style");
                }
            }
        }

        return needed;

        void Need(TextStyle style, string path)
        {
            if (Face(style, styles, defaultFamily) is { } face)
            {
                needed.TryAdd(face, path);
            }
            else if (!reportedNoFamily)
            {
                reportedNoFamily = true;
                sink.Error(DiagnosticCodes.NoFonts, "No font family is named for this text, and the render has no fonts to default to. Add one: new RenderOptions { Fonts = FontCollection.Empty.Add(new FontFamily(\"Inter\") { Regular = FontSource.FromFile(\"Inter-Regular.ttf\") }) }.", path);
            }
        }
    }

    public static ImmutableArray<Inline>? Inlines(Block block) => block switch
    {
        Heading heading => heading.Inlines,
        Paragraph paragraph => paragraph.Inlines,
        _ => null,
    };

    public static void Check(PdfDocument document, StyleResolver styles, FontSet fonts, string? defaultFamily, PdfConformance conformance, DiagnosticSink sink)
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
                case Heading or Paragraph:
                    TextStyle block = styles.For(document.Content[i]);
                    ImmutableArray<Inline> inlines = Inlines(document.Content[i])!.Value;
                    for (int j = 0; j < inlines.Length; j++)
                    {
                        switch (inlines[j])
                        {
                            case TextRun run:
                                FontChain? chain = Face(RunStyle(block, run), styles, defaultFamily) is { } face && fonts.TryGet(face, out FontChain found) ? found : null;
                                Characters(run.Text, chain, $"{path}/inlines/{j}");
                                break;
                            case Field:
                                sink.Warning(DiagnosticCodes.NotDrawnYet, "Page number fields aren't drawn yet in this version; the field is left out.", $"{path}/inlines/{j}");
                                break;
                        }
                    }

                    break;
                case Table:
                    sink.Warning(DiagnosticCodes.NotDrawnYet, "Tables aren't drawn yet in this version; the table is left out.", path);
                    break;
                case Image:
                    sink.Warning(DiagnosticCodes.NotDrawnYet, "Images aren't drawn yet in this version; the image is left out.", path);
                    break;
            }
        }

        void Characters(string text, FontChain? chain, string path)
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
                else if (!layoutControl && chain is not null && !chain.Covers(c) && reported.Add(c))
                {
                    // Drawn as .notdef: visible and searchable as U+FFFD, but meaningless to a screen reader.
                    string message = string.Create(CultureInfo.InvariantCulture, $"No font for this text has a glyph for U+{c:X4} ('{rune}') at index {index} (tried '{chain.Primary.Font.PostScriptName}' and {chain.Fallbacks.Count} fallback{(chain.Fallbacks.Count == 1 ? "" : "s")}), so it's drawn as an empty box. Add a fallback family that covers it.");
                    if (conformance == PdfConformance.PdfUA1)
                    {
                        sink.Error(DiagnosticCodes.MissingGlyph, message + " PDF/UA requires every character to be drawn.", path);
                    }
                    else
                    {
                        sink.Warning(DiagnosticCodes.MissingGlyph, message, path);
                    }
                }

                index += rune.Utf16SequenceLength;
            }
        }
    }
}
