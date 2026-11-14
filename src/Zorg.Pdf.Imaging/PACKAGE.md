# Zorg.Pdf.Imaging

Image handling for [Zorg.Pdf](https://www.nuget.org/packages/Zorg.Pdf), built on [Zorg.Imaging](https://www.nuget.org/packages/Zorg.Imaging). Managed code only, no native binaries; Native AOT and trimming work.

**Pre-release: the API will change before 1.0.**

Zorg.Pdf reads JPEG and PNG by itself. With `ImagingProcessor` it also:

- reads **WebP, GIF, BMP and TIFF**;
- turns camera photos **the right way up** from their EXIF orientation;
- converts **CMYK and ICC-profiled** images to sRGB;
- **shrinks** images larger than they're printed at, to 300 dpi by default, and draws them at their original size.

JPEG and PNG that need none of that are embedded untouched.

```csharp
using Zorg.Pdf;
using Zorg.Pdf.Imaging;

var options = new RenderOptions
{
    Fonts = fonts,
    ImageProcessor = new ImagingProcessor { Resolution = 200, JpegQuality = 85 },
};
await document.RenderAsync(output, options, cancellationToken);
```

## Page previews

`PdfPreview` draws pages of a PDF that Zorg.Pdf wrote as PNG, so a person or an agent can check a layout by looking.
It reads only Zorg.Pdf's own output and refuses anything else, and gives the same pixels on every machine. It draws
with Zorg.Imaging's canvas, which is experimental, so this API may change too.

```csharp
byte[] png = await PdfPreview.RenderPagePngAsync(pdf, page: 0, new PreviewOptions { Dpi = 96 }, cancellationToken);
```

For agents, pass it to the tools: `new PdfToolOptions { Render = ..., PreviewAsync = (pdf, page, ct) => PdfPreview.RenderPagePngAsync(pdf, page, cancellationToken: ct) }`.

## Documentation

- [Known limits](https://github.com/alftore/Zorg.Pdf/blob/main/docs/limits.md)
- [Changelog](https://github.com/alftore/Zorg.Pdf/blob/main/CHANGELOG.md)
