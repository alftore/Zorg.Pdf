using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Tests.Fonts;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Small helpers to look inside rendered files. Structure checks only; qpdf and veraPDF do the real checking in the gate.</summary>
internal static partial class PdfInspect
{
    public static RenderOptions Options(PdfConformance conformance = PdfConformance.None) => new()
    {
        Fonts = FontCollection.Empty.Add(new FontFamily("Body") { Regular = FontSource.FromBytes(TestFonts.Require()) }),
        Conformance = conformance,
    };

    /// <summary>Every object as text, streams decoded and object streams unpacked (see <see cref="PdfReader.Flatten"/>).</summary>
    public static string Text(byte[] file) => PdfReader.Flatten(file);

    /// <summary>The inflated content stream of each page, in page order: the page's own, without its header and footer.</summary>
    public static string[] PageContents(byte[] file)
    {
        var objects = PdfReader.Read(file);
        var contents = new List<string>();
        foreach (Match page in PageContentsRef().Matches(Text(file)))
        {
            contents.Add(objects[Number(page.Groups[1].Success ? page.Groups[1] : page.Groups[2])].DecodedText);
        }

        return [.. contents];
    }

    /// <summary>Each page's second content stream (its header and footer) as object number and content, or null without one.</summary>
    public static List<(int Number, string Content)?> PageOverlays(byte[] file)
    {
        var objects = PdfReader.Read(file);
        var overlays = new List<(int, string)?>();
        foreach (Match page in PageContentsRef().Matches(Text(file)))
        {
            overlays.Add(page.Groups[3].Success ? (Number(page.Groups[3]), objects[Number(page.Groups[3])].DecodedText) : null);
        }

        return overlays;
    }

    /// <summary>Each page's content streams in drawing order, as object numbers and decoded content.</summary>
    public static List<(int Number, string Content)[]> PageStreams(byte[] file)
    {
        var objects = PdfReader.Read(file);
        var pages = new List<(int, string)[]>();
        foreach (Match page in AllContents().Matches(Text(file)))
        {
            pages.Add([.. Reference().Matches(page.Groups[1].Value).Select(r => int.Parse(r.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).Select(n => (n, objects[n].DecodedText))]);
        }

        return pages;
    }

    private static int Number(Group group) => int.Parse(group.Value, System.Globalization.CultureInfo.InvariantCulture);

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

    [GeneratedRegex(@"/Type /Page /Parent .*?/Contents (\[[^\]]*\]|\d+ 0 R)")]
    private static partial Regex AllContents();

    [GeneratedRegex(@"/Nums \[(.*?)\]>>", RegexOptions.Singleline)]
    private static partial Regex NumsArray();

    [GeneratedRegex(@"\d+ \[([^\]]*)\]")]
    private static partial Regex NumsEntry();

    [GeneratedRegex(@"(\d+) 0 R")]
    private static partial Regex Reference();
}
