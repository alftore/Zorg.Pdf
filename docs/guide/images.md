# Images and previews

## Images

The core package embeds **JPEG** as it is and **PNG** with its transparency, with no dependencies. Images come in through slots, so a document never reads files on its own:

```csharp
var logo = new Image(ImageSource.FromSlot("logo")) { Alt = "Zorg Consulting", Width = Length.Cm(4) };
var data = PdfData.Empty.WithImage("logo", await File.ReadAllBytesAsync("logo.png", cancellationToken));
```

```json
{ "type": "image", "source": { "$data": "logo" }, "alt": "Zorg Consulting", "width": "4cm" }
```

- **Size** comes from `Width` or `Height` (the other follows the aspect ratio), or from the pixels at 96 dpi. Images too wide for the column are scaled down.
- **Alt text** is required unless `Decorative` is true, and a missing one is an error under PDF/UA. Write what the image means in context, not "image of".
- **The same image** used twice, or on every page in a header, is embedded once.
- Images can load lazily: `WithImage("logo", async ct => ...)` fetches the bytes only when the image is reached, for example from blob storage.

## More formats: Zorg.Pdf.Imaging

`Zorg.Pdf.Imaging` adds WebP, GIF, BMP and TIFF, turns photos upright from their EXIF orientation, converts other colour profiles to sRGB and downsamples large photos. It uses Zorg.Imaging, which is managed code too.

```csharp
var options = new RenderOptions { Fonts = fonts, ImageProcessor = new ImagingProcessor { Resolution = 200 } };
```

`Resolution` is the most pixels per inch kept: a 24-megapixel photo printed 8 cm wide doesn't need all of them. For print work that needs the inks kept, `KeepCmyk = true` keeps CMYK photos as CMYK. That isn't allowed under PDF/A, which needs sRGB.

## Previews

`PdfPreview` draws a page of a PDF that Zorg.Pdf wrote as a PNG. It's for checking layouts by eye, in a test or by an AI agent, without installing a PDF renderer:

```csharp
using var pdf = new MemoryStream();
await document.RenderAsync(pdf, options, cancellationToken);
byte[] png = await PdfPreview.RenderPagePngAsync(pdf.ToArray(), page: 0, new PreviewOptions { Dpi = 96 }, cancellationToken);
```

The preview draws what Zorg.Pdf writes (text, paths, images, backgrounds) and the Linux gate compares it with MuPDF. It is not a general PDF renderer for other people's files. On the command line, `zorg-pdf preview file.pdf --page all` or `render --preview` writes the PNGs beside the PDF.
