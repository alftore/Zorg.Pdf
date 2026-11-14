using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Tests.Fonts;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Small helpers to look inside rendered files. Structure checks only; qpdf and veraPDF do the real checking in the gate.</summary>
internal static partial class PdfInspect
{
    public static RenderOptions Options(PdfConformance conformance = PdfConformance.None) => new()
    {
        Fonts = FontCollection.Empty.Add(new FontFamily("Body") { Regular = FontSource.FromBytes(TestFonts.Require()) }),
        Conformance = conformance,
    };

    public static string Text(byte[] file) => Encoding.Latin1.GetString(file);

    /// <summary>The inflated content stream of each page, in page order: the page's own, without its header and footer.</summary>
    public static string[] PageContents(byte[] file)
    {
        var contents = new List<string>();
        foreach (Match page in PageContentsRef().Matches(Text(file)))
        {
            contents.Add(Inflate(file, page.Groups[1].Success ? page.Groups[1].Value : page.Groups[2].Value));
        }

        return [.. contents];
    }

    /// <summary>Each page's second content stream (its header and footer) as object number and content, or null without one.</summary>
    public static List<(int Number, string Content)?> PageOverlays(byte[] file)
    {
        var overlays = new List<(int, string)?>();
        foreach (Match page in PageContentsRef().Matches(Text(file)))
        {
            overlays.Add(page.Groups[3].Success ? (int.Parse(page.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture), Inflate(file, page.Groups[3].Value)) : null);
        }

        return overlays;
    }

    /// <summary>A Flate stream object, inflated.</summary>
    private static string Inflate(byte[] file, string number)
    {
        string text = Text(file);
        int obj = text.IndexOf($"\n{number} 0 obj\n", StringComparison.Ordinal) + 1;
        int start = text.IndexOf("stream\n", obj, StringComparison.Ordinal) + "stream\n".Length;
        using var zlib = new ZLibStream(new MemoryStream(file, start, file.Length - start), CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        zlib.CopyTo(inflated);
        return Encoding.Latin1.GetString(inflated.ToArray());
    }

    /// <summary>For each page, the structure element object numbers its MCIDs belong to (the parent tree).</summary>
    public static List<int[]> ParentTree(byte[] file)
    {
        Match nums = NumsArray().Match(Text(file));
        var pages = new List<int[]>();
        foreach (Match entry in NumsEntry().Matches(nums.Groups[1].Value))
        {
            var owners = new List<int>();
            foreach (Match reference in Reference().Matches(entry.Groups[1].Value))
            {
                owners.Add(int.Parse(reference.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }

            pages.Add([.. owners]);
        }

        return pages;
    }

    /// <summary>Object numbers of structure elements with a role, in object order.</summary>
    public static List<int> Elements(byte[] file, string role)
    {
        var found = new List<int>();
        foreach (Match m in Regex.Matches(Text(file), $@"\n(\d+) 0 obj\n<</Type /StructElem /S /{role} "))
        {
            found.Add(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        return found;
    }

    [GeneratedRegex(@"/Type /Page /Parent .*?/Contents (?:(\d+) 0 R|\[(\d+) 0 R (\d+) 0 R\])")]
    private static partial Regex PageContentsRef();

    [GeneratedRegex(@"/Nums \[(.*?)\]>>", RegexOptions.Singleline)]
    private static partial Regex NumsArray();

    [GeneratedRegex(@"\d+ \[([^\]]*)\]")]
    private static partial Regex NumsEntry();

    [GeneratedRegex(@"(\d+) 0 R")]
    private static partial Regex Reference();
}
