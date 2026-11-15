using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Reading;
using Zorg.Pdf.Validation;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// The pages of an existing PDF drawn behind the document's pages. Read and checked before anything is written, so a
/// file that can't be used stops the render with a diagnostic; each page used is copied once, as a form XObject.
/// </summary>
internal sealed class BackgroundSet
{
    private readonly CosFile _file;
    private readonly PageBackground _background;
    private readonly Dictionary<int, (CosPage Page, byte[] Content)> _pages;
    private readonly Dictionary<int, (byte[] Name, PdfReference Form, PdfReference Stream)> _written = [];
    private CosImporter? _importer;

    private BackgroundSet(CosFile file, PageBackground background, Dictionary<int, (CosPage, byte[])> pages) =>
        (_file, _background, _pages) = (file, background, pages);

    /// <summary>Reads the background's file and pages, or null when there's none or it can't be used (with an error).</summary>
    public static async ValueTask<BackgroundSet?> PrepareAsync(PdfDocument document, RenderOptions options, DiagnosticSink sink, bool synchronous, CancellationToken cancellationToken)
    {
        if (document.Background is not { Source: not null } background || (background.FirstPage <= 0 && background.OtherPages <= 0))
        {
            return null;
        }

        ReadOnlyMemory<byte>? bytes = await ImageSet.BytesAsync(background.Source, options, sink, synchronous, "/background", cancellationToken).ConfigureAwait(false);
        if (bytes is not { } file)
        {
            return null;
        }

        const string Path = "/background/source";
        var read = new List<PdfDiagnostic>();
        try
        {
            var cos = new CosFile(file, PdfReadLimits.Default, read);
            List<CosPage> pages = CosPages.Read(cos, PdfReadLimits.Default, read);
            var used = new Dictionary<int, (CosPage, byte[])>();
            bool conformance = options.Conformance == PdfConformance.PdfUA1 || options.Archive != PdfArchive.None;
            PageGeometry geometry = PageGeometry.From(document.Page);
            foreach ((int number, string property) in new[] { (background.FirstPage, "firstPage"), (background.OtherPages, "otherPages") })
            {
                if (number <= 0 || used.ContainsKey(number))
                {
                    continue;
                }

                if (number > pages.Count)
                {
                    sink.Error(DiagnosticCodes.BackgroundPageMissing, $"The background asks for page {number}, but its PDF has {pages.Count} page{(pages.Count == 1 ? "" : "s")}.", $"/background/{property}");
                    continue;
                }

                CosPage page = pages[number - 1];
                byte[] content = CosContent.Read(cos, page, out bool cmyk);
                (bool notEmbedded, bool cmykResources) = Inspect(cos, page.Resources);
                if (notEmbedded)
                {
                    string message = $"Page {number} of the background uses a font that isn't embedded, so it may look different elsewhere. Export the letterhead with its fonts embedded.";
                    if (conformance)
                    {
                        sink.Error(DiagnosticCodes.BackgroundFontNotEmbedded, message + " PDF/UA and PDF/A require it.", Path);
                    }
                    else
                    {
                        sink.Warning(DiagnosticCodes.BackgroundFontNotEmbedded, message, Path);
                    }
                }

                if ((cmyk || cmykResources) && options.Archive != PdfArchive.None)
                {
                    sink.Error(DiagnosticCodes.BackgroundColour, $"Page {number} of the background uses CMYK colour, which PDF/A with an sRGB output intent doesn't allow. Export the letterhead in RGB.", Path);
                }

                (double width, double height) = Displayed(page);
                if (Math.Abs(width - geometry.Width) > 1 || Math.Abs(height - geometry.Height) > 1)
                {
                    sink.Info(DiagnosticCodes.BackgroundScaled, $"Page {number} of the background is {width:0.#} × {height:0.#} pt and the document's pages {geometry.Width:0.#} × {geometry.Height:0.#} pt: it's scaled to fit and centred.", $"/background/{property}");
                }

                used[number] = (page, content);
            }

            sink.AddRange(read.Select(d => d with { Path = Path }));
            return used.Count == 0 ? null : new BackgroundSet(cos, background, used);
        }
        catch (PdfReadException ex)
        {
            sink.Error(DiagnosticCodes.BackgroundUnreadable, $"The background's PDF can't be used. {ex.Message}", Path);
            return null;
        }
    }

    /// <summary>The background's resource name and drawing stream for a page (from 0), writing them on first use; or null.</summary>
    public (byte[] Name, PdfReference Form, PdfReference Stream)? For(int pageIndex, PdfObjectWriter writer, PageGeometry geometry)
    {
        int number = pageIndex == 0 ? _background.FirstPage : _background.OtherPages;
        if (number <= 0 || !_pages.TryGetValue(number, out (CosPage Page, byte[] Content) source))
        {
            return null;
        }

        if (_written.TryGetValue(number, out (byte[] Name, PdfReference Form, PdfReference Stream) done))
        {
            return done;
        }

        _importer ??= new CosImporter(_file, writer, PdfReadLimits.Default.MaxObjects);
        byte[] name = System.Text.Encoding.ASCII.GetBytes($"Bg{number}");
        PdfReference form = writer.Allocate(), stream = writer.Allocate();

        double[] box = source.Page.CropBox;
        writer.BeginStreamObject(form);
        writer.Name("Type"u8);
        writer.Name("XObject"u8);
        writer.Name("Subtype"u8);
        writer.Name("Form"u8);
        writer.Name("BBox"u8);
        writer.BeginArray();
        foreach (double v in box)
        {
            writer.Real(v);
        }

        writer.EndArray();
        writer.Name("Resources"u8);
        if (source.Page.Resources is { } resources && _file.Resolve(resources) is CosDictionary)
        {
            _importer.Write(resources);
        }
        else
        {
            writer.BeginDictionary();
            writer.EndDictionary();
        }

        if (source.Page.Dictionary["Group"] is { } group)
        {
            writer.Name("Group"u8);
            _importer.Write(group);
        }

        writer.EndStreamObject(source.Content, compress: true);
        _importer.Flush();

        // Drawn as a layout artifact, turned as the page is shown, scaled to fit and centred.
        (double a, double b, double c, double d, double e, double f) = Matrix(source.Page, geometry);
        var content = new ContentStreamBuilder();
        content.SaveState().BeginArtifact("Layout"u8, default).Transform(a, b, c, d, e, f).DrawXObject(name).EndMarkedContent().RestoreState();
        writer.BeginStreamObject(stream);
        writer.EndStreamObject(content.WrittenSpan, compress: true);
        done = (name, form, stream);
        _written[number] = done;
        return done;
    }

    /// <summary>The page's size as viewers show it: its crop box, turned by its rotation.</summary>
    private static (double Width, double Height) Displayed(CosPage page)
    {
        double w = page.CropBox[2] - page.CropBox[0], h = page.CropBox[3] - page.CropBox[1];
        return page.Rotate is 90 or 270 ? (h, w) : (w, h);
    }

    /// <summary>From the source page's space to the target page: rotation as viewers apply it, then scale and centre.</summary>
    internal static (double A, double B, double C, double D, double E, double F) Matrix(CosPage page, PageGeometry geometry)
    {
        double x0 = page.CropBox[0], y0 = page.CropBox[1], x1 = page.CropBox[2], y1 = page.CropBox[3];
        (double a, double b, double c, double d, double e, double f) = page.Rotate switch
        {
            90 => (0.0, -1.0, 1.0, 0.0, -y0, x1),
            180 => (-1.0, 0.0, 0.0, -1.0, x1, y1),
            270 => (0.0, 1.0, -1.0, 0.0, y1, -x0),
            _ => (1.0, 0.0, 0.0, 1.0, -x0, -y0),
        };
        (double width, double height) = Displayed(page);
        double scale = Math.Abs(width - geometry.Width) <= 1 && Math.Abs(height - geometry.Height) <= 1 ? 1 : Math.Min(geometry.Width / width, geometry.Height / height);
        double ox = (geometry.Width - (width * scale)) / 2, oy = (geometry.Height - (height * scale)) / 2;
        return (a * scale, b * scale, c * scale, d * scale, (e * scale) + ox, (f * scale) + oy);
    }

    /// <summary>Whether the resources use a font that isn't embedded, or CMYK colour. Walks the graph once, bounded.</summary>
    private static (bool NotEmbedded, bool Cmyk) Inspect(CosFile file, CosValue? resources)
    {
        bool notEmbedded = false, cmyk = false;
        var seen = new HashSet<int>();
        var stack = new Stack<(CosValue Value, int Depth)>();
        stack.Push((resources ?? CosNull.Value, 0));
        int visited = 0;
        while (stack.Count > 0 && visited++ < 200_000)
        {
            (CosValue value, int depth) = stack.Pop();
            if (value is CosReference r)
            {
                if (!seen.Add(r.Number))
                {
                    continue;
                }

                value = file.Resolve(r);
            }

            if (depth > CosLexer.MaxDepth)
            {
                continue;
            }

            CosDictionary? dictionary = value switch
            {
                CosDictionary d => d,
                CosStream s => s.Dictionary,
                _ => null,
            };
            if (dictionary is not null)
            {
                if (file.Resolve(dictionary["Type"]) is CosName { Value: "FontDescriptor" } && dictionary["FontFile"] is null && dictionary["FontFile2"] is null && dictionary["FontFile3"] is null)
                {
                    notEmbedded = true;
                }

                if (file.Resolve(dictionary["Type"]) is CosName { Value: "Font" } && file.Resolve(dictionary["Subtype"]) is CosName { Value: "Type1" or "TrueType" or "MMType1" } && dictionary["FontDescriptor"] is null)
                {
                    notEmbedded = true; // A standard 14 font: never embedded.
                }

                if (file.Resolve(dictionary["N"]).AsInteger == 4 && dictionary["Alternate"] is not null)
                {
                    cmyk = true; // An ICC profile with four components.
                }

                foreach ((string key, CosValue item) in dictionary.Entries)
                {
                    if (key is not ("Parent" or "P" or "StructParent" or "StructParents" or "Metadata"))
                    {
                        stack.Push((item, depth + 1));
                    }
                }
            }
            else if (value is CosArray array)
            {
                foreach (CosValue item in array.Items)
                {
                    stack.Push((item, depth + 1));
                }
            }
            else if (value is CosName { Value: "DeviceCMYK" })
            {
                cmyk = true;
            }
        }

        return (notEmbedded, cmyk);
    }
}
