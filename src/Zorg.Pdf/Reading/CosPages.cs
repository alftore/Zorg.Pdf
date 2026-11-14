using System;
using System.Collections.Generic;
using System.Text;
using Zorg.Pdf.Validation;

namespace Zorg.Pdf.Reading;

/// <summary>A page of an existing file, with the attributes it inherits from the page tree filled in.</summary>
/// <param name="Dictionary">The page dictionary.</param>
/// <param name="ObjectNumber">Its object number, or -1 when it's a direct object.</param>
/// <param name="MediaBox">The paper: x0, y0, x1, y1 in points, normalised so x0 &lt;= x1 and y0 &lt;= y1.</param>
/// <param name="CropBox">The visible part, within the media box.</param>
/// <param name="Rotate">0, 90, 180 or 270.</param>
/// <param name="Resources">The resources, inherited when the page has none.</param>
internal sealed record CosPage(CosDictionary Dictionary, int ObjectNumber, double[] MediaBox, double[] CropBox, int Rotate, CosValue? Resources);

/// <summary>Walks the page tree and decodes text strings.</summary>
internal static class CosPages
{
    private static readonly double[] Letter = [0, 0, 612, 792];

    /// <summary>The pages in order. Loops, missing kids and nodes that aren't pages are skipped, with a diagnostic.</summary>
    public static List<CosPage> Read(CosFile file, PdfReadLimits limits, List<PdfDiagnostic> diagnostics)
    {
        var pages = new List<CosPage>();
        CosValue rootValue = file.Catalog["Pages"] ?? CosNull.Value;
        if (file.Resolve(rootValue) is not CosDictionary)
        {
            throw PdfReadException.Malformed("The catalog has no page tree.");
        }

        var visited = new HashSet<int>();
        bool skipped = false;
        var stack = new Stack<(CosValue Node, int Depth, Inherited Inherited)>();
        stack.Push((rootValue, 0, new Inherited(null, null, null, null)));
        while (stack.Count > 0)
        {
            (CosValue nodeValue, int depth, Inherited inherited) = stack.Pop();
            if (nodeValue is CosReference reference && !visited.Add(reference.Number))
            {
                skipped = true; // A loop, or a page listed twice.
                continue;
            }

            if (file.Resolve(nodeValue) is not CosDictionary node || depth > CosLexer.MaxDepth)
            {
                skipped = true;
                continue;
            }

            var here = new Inherited(
                node["MediaBox"] ?? inherited.MediaBox,
                node["CropBox"] ?? inherited.CropBox,
                node["Rotate"] ?? inherited.Rotate,
                node["Resources"] ?? inherited.Resources);
            CosValue type = file.Resolve(node["Type"]);
            CosValue kids = file.Resolve(node["Kids"]);
            if (kids is CosArray array && type is not CosName { Value: "Page" })
            {
                for (int i = array.Items.Count - 1; i >= 0; i--)
                {
                    stack.Push((array.Items[i], depth + 1, here));
                }

                continue;
            }

            if (type is CosName { Value: "Pages" })
            {
                skipped = true; // A branch without kids.
                continue;
            }

            if (pages.Count >= limits.MaxPages)
            {
                throw new PdfReadException($"The file has more than {limits.MaxPages:N0} pages (PdfReadLimits.MaxPages).");
            }

            double[] media = Box(file, here.MediaBox) ?? Letter;
            double[] crop = Box(file, here.CropBox) is { } c ? Intersect(c, media) : media;
            long rotate = file.Resolve(here.Rotate).AsInteger ?? 0;
            pages.Add(new CosPage(node, nodeValue is CosReference r ? r.Number : -1, media, crop, (int)((((rotate % 360) + 360) % 360) / 90 * 90), here.Resources));
        }

        if (skipped)
        {
            diagnostics.Add(new PdfDiagnostic(DiagnosticCodes.ReadPageTree, DiagnosticSeverity.Warning, "The page tree has loops, missing pages or nodes that aren't pages: they were skipped.", "file"));
        }

        return pages;
    }

    /// <summary>A rectangle as x0, y0, x1, y1 with x0 &lt;= x1 and y0 &lt;= y1, or null.</summary>
    public static double[]? Box(CosFile file, CosValue? value)
    {
        if (file.Resolve(value) is not CosArray { Items.Count: 4 } array)
        {
            return null;
        }

        double[] v = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (file.Resolve(array.Items[i]).AsNumber is not { } n || !double.IsFinite(n))
            {
                return null;
            }

            v[i] = n;
        }

        double[] box = [Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3])];
        return box[2] - box[0] > 0 && box[3] - box[1] > 0 ? box : null;
    }

    private static double[] Intersect(double[] a, double[] b)
    {
        double[] box = [Math.Max(a[0], b[0]), Math.Max(a[1], b[1]), Math.Min(a[2], b[2]), Math.Min(a[3], b[3])];
        return box[2] > box[0] && box[3] > box[1] ? box : b;
    }

    /// <summary>A text string: UTF-16BE or UTF-8 with a byte-order mark, else PDFDocEncoding.</summary>
    public static string Text(CosValue value)
    {
        if (value is not CosString s)
        {
            return value is CosName n ? n.Value : "";
        }

        byte[] b = s.Value;
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(b, 2, (b.Length - 2) & ~1);
        }

        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        }

        var text = new StringBuilder(b.Length);
        foreach (byte c in b)
        {
            text.Append(c is >= 0x80 and <= 0x9F ? DocEncoding[c - 0x80] : c is >= 0x18 and <= 0x1F ? DocEncodingLow[c - 0x18] : (char)c);
        }

        return text.ToString();
    }

    // PDFDocEncoding where it differs from Latin-1 (ISO 32000-2, annex D).
    private static readonly char[] DocEncodingLow = ['˘', 'ˇ', 'ˆ', '˙', '˝', '˛', '˚', '˜'];

    private static readonly char[] DocEncoding =
    [
        '•', '†', '‡', '…', '—', '–', 'ƒ', '⁄', '‹', '›', '−', '‰', '„', '“', '”', '‘',
        '’', '‚', '™', 'ﬁ', 'ﬂ', 'Ł', 'Œ', 'Š', 'Ÿ', 'Ž', 'ı', 'ł', 'œ', 'š', 'ž', '�',
    ];

    private sealed record Inherited(CosValue? MediaBox, CosValue? CropBox, CosValue? Rotate, CosValue? Resources);
}
