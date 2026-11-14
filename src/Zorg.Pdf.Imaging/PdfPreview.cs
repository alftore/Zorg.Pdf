using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging;
using Bitmap = Zorg.Imaging.Image;
using Zorg.Imaging.Drawing;
using Zorg.Pdf.Imaging.Preview;

namespace Zorg.Pdf.Imaging;

/// <summary>
/// Pictures of pages of a PDF that Zorg.Pdf wrote, for checking a layout by looking: an agent after rendering, a
/// person in <c>zorg-pdf watch</c>, a thumbnail in an app. It reads only Zorg.Pdf's own output (anything else throws
/// <see cref="PdfPreviewException"/>), draws with Zorg.Imaging's canvas, and gives the same pixels on every machine.
/// </summary>
/// <example>
/// <code>
/// using var output = new MemoryStream();
/// await document.RenderAsync(output, options, cancellationToken);
/// byte[] png = await PdfPreview.RenderPagePngAsync(output.ToArray(), page: 0, cancellationToken: cancellationToken);
/// </code>
/// </example>
public static class PdfPreview
{
    private const int MaxPageTreeDepth = 32;

    /// <summary>The number of pages.</summary>
    /// <param name="pdf">A PDF that Zorg.Pdf wrote.</param>
    /// <param name="limits">Limits for reading it; null for the defaults.</param>
    /// <returns>The page count.</returns>
    /// <exception cref="PdfPreviewException">The file isn't one Zorg.Pdf wrote, is damaged, or is over a limit.</exception>
    public static int PageCount(ReadOnlyMemory<byte> pdf, PdfPreviewLimits? limits = null) =>
        Pages(new PreviewFile(pdf, limits ?? PdfPreviewLimits.Default)).Count;

    /// <summary>Draws one page as an image (RGB, 8 bits, sRGB). The caller disposes it.</summary>
    /// <param name="pdf">A PDF that Zorg.Pdf wrote.</param>
    /// <param name="page">The page, from 0.</param>
    /// <param name="options">Resolution, background and limits; null for 96 dpi on white.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The page's picture.</returns>
    /// <exception cref="PdfPreviewException">The file isn't one Zorg.Pdf wrote, is damaged, or is over a limit.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page doesn't exist, or the resolution is outside 18 to 600 dpi.</exception>
    public static ValueTask<Bitmap> RenderPageAsync(ReadOnlyMemory<byte> pdf, int page, PreviewOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
#pragma warning disable CA2000 // Ownership passes to the caller, as the documentation says.
        return ValueTask.FromResult(RenderPage(pdf, page, options ?? PreviewOptions.Default));
#pragma warning restore CA2000
    }

    /// <summary>Draws one page as PNG bytes.</summary>
    /// <param name="pdf">A PDF that Zorg.Pdf wrote.</param>
    /// <param name="page">The page, from 0.</param>
    /// <param name="options">Resolution, background and limits; null for 96 dpi on white.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The PNG file.</returns>
    /// <exception cref="PdfPreviewException">The file isn't one Zorg.Pdf wrote, is damaged, or is over a limit.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page doesn't exist, or the resolution is outside 18 to 600 dpi.</exception>
    public static async ValueTask<byte[]> RenderPagePngAsync(ReadOnlyMemory<byte> pdf, int page, PreviewOptions? options = null, CancellationToken cancellationToken = default)
    {
        using Bitmap image = await RenderPageAsync(pdf, page, options, cancellationToken).ConfigureAwait(false);
        using var png = new MemoryStream();
        await image.SaveAsPngAsync(png, cancellationToken: cancellationToken).ConfigureAwait(false);
        return png.ToArray();
    }

    private static Bitmap RenderPage(ReadOnlyMemory<byte> pdf, int page, PreviewOptions options)
    {
        if (options.Dpi is < 18 or > 600 || double.IsNaN(options.Dpi))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Dpi, "The resolution is from 18 to 600 dpi.");
        }

        var file = new PreviewFile(pdf, options.Limits);
        List<(PdfDictionary Page, PdfDictionary Resources, PdfArray Box)> pages = Pages(file);
        if (page < 0 || page >= pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, $"The document has {pages.Count} pages, numbered from 0.");
        }

        (PdfDictionary dictionary, PdfDictionary resources, PdfArray box) = pages[page];
        float left = (float)file.Resolve(box.Items[0]).Number, bottom = (float)file.Resolve(box.Items[1]).Number;
        float width = (float)file.Resolve(box.Items[2]).Number - left, height = (float)file.Resolve(box.Items[3]).Number - bottom;
        float scale = (float)(options.Dpi / 72);
        int pixelsWide = (int)Math.Ceiling(width * scale), pixelsHigh = (int)Math.Ceiling(height * scale);
        if (width <= 0 || height <= 0 || (long)pixelsWide * pixelsHigh > 100_000_000)
        {
            throw PdfPreviewException.Malformed("The page's MediaBox is empty or too large to draw.");
        }

        Bitmap? image = new(pixelsWide, pixelsHigh, PixelFormat.Rgb24);
        try
        {
            using var canvas = new Canvas(image);
            canvas.Clear(options.Background);
            canvas.Transform = Matrix3x2.CreateTranslation(-left, -bottom) * Matrix3x2.CreateScale(scale, -scale) * Matrix3x2.CreateTranslation(0, pixelsHigh);
            var rasterizer = new PageRasterizer(file, resources, canvas, options.Limits);
            try
            {
                foreach (PdfStream content in Contents(file, dictionary))
                {
                    rasterizer.Draw(file.Decode(content));
                }
            }
            finally
            {
                rasterizer.Dispose();
            }

            Bitmap drawn = image;
            image = null; // The caller owns it now.
            return drawn;
        }
        finally
        {
            image?.Dispose();
        }
    }

    private static IEnumerable<PdfStream> Contents(PreviewFile file, PdfDictionary page)
    {
        PdfValue? contents = page["Contents"];
        if (file.Stream(contents) is { } single)
        {
            yield return single;
        }
        else if (file.Resolve(contents) is PdfArray parts)
        {
            foreach (PdfValue part in parts.Items)
            {
                yield return file.Stream(part) ?? throw PdfPreviewException.Malformed("A page's contents aren't streams.");
            }
        }
    }

    /// <summary>The pages in order, with their inherited resources and MediaBox.</summary>
    private static List<(PdfDictionary Page, PdfDictionary Resources, PdfArray Box)> Pages(PreviewFile file)
    {
        PdfDictionary root = file.Dictionary(file.Trailer["Root"]) ?? throw PdfPreviewException.Malformed("There's no document catalog.");
        PdfDictionary tree = file.Dictionary(root["Pages"]) ?? throw PdfPreviewException.Malformed("There's no page tree.");
        var pages = new List<(PdfDictionary, PdfDictionary, PdfArray)>();
        Walk(tree, null, null, 0);
        return pages;

        void Walk(PdfDictionary node, PdfDictionary? resources, PdfArray? box, int depth)
        {
            if (depth > MaxPageTreeDepth || pages.Count > 100_000)
            {
                throw PdfPreviewException.Malformed("The page tree is too deep or too large.");
            }

            resources = file.Dictionary(node["Resources"]) ?? resources;
            box = file.Resolve(node["MediaBox"]) as PdfArray ?? box;
            if (file.Resolve(node["Type"]) is PdfName { Value: "Page" })
            {
                pages.Add((node, resources ?? new PdfDictionary([]), box is { Items.Count: 4 } ? box : throw PdfPreviewException.Malformed("A page has no MediaBox.")));
                return;
            }

            if (file.Resolve(node["Kids"]) is PdfArray kids)
            {
                foreach (PdfValue kid in kids.Items)
                {
                    Walk(file.Dictionary(kid) ?? throw PdfPreviewException.Malformed("A page tree entry isn't a dictionary."), resources, box, depth + 1);
                }
            }
        }
    }
}
