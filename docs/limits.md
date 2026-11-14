# Known limits

Zorg.Pdf 0.1 is in development. What it doesn't do yet, and the limits it puts on input.

## Input limits

Every limit is checked before anything is written, except page and row counts, which are only known while rendering.

| Limit | Default | Where |
|---|---|---|
| Document JSON and data JSON | 16 MB, nesting 64 deep | `PdfDocument.ParseJson`, `PdfData.ParseJson` (ZPDF-J006) |
| Pages | 10,000 | `RenderOptions.Limits.MaxPages` (`PdfLimitException`) |
| Rows in one table | 1,000,000 | `RenderOptions.Limits.MaxTableRows` (`PdfLimitException`) |
| Image file | 50 MB | `RenderOptions.Limits.MaxImageBytes` (ZPDF-I002) |
| Image pixels | 50 million | `RenderOptions.Limits.MaxImagePixels` (ZPDF-I002) |

Fonts come from the caller, never from a document; a damaged font is ZPDF-F005.

## Not supported yet

- **Images: JPEG and PNG only** in the core package. `ImagingProcessor` from `Zorg.Pdf.Imaging` adds WebP, GIF (first frame), BMP and TIFF, EXIF orientation and sRGB conversion; without it the rest of this point applies. JPEG must be 8-bit baseline or progressive (no 12-bit, lossless or arithmetic-coded). EXIF orientation, colour profiles and PNG gamma are ignored, so a photo the camera stored sideways comes out sideways. Without a width or height an image is drawn at 96 dpi; its own resolution isn't read. A PNG with transparency is decoded to 8 bits per channel.
- **Text boxes hold paragraphs only**, aligned to their top, and the flow doesn't wrap around them: put a spacer after an address box. A box is drawn on the page the content has reached, once; for every page, use a header.
- **Templates bind values, repeat and show conditionally; nothing more.** No expressions, comparisons or helpers (compute in the model), no `{{ }}` in text, no binding of styles or page settings. Row templates (`"rows": { "repeat": ... }`) build the whole table in memory; for long tables from a database or another stream, use a data slot. Diagnostics from inside a repeat point into the expanded document. Number and date formats in languages other than English follow the operating system's culture data, which can differ between Windows and Linux.
- **Watermarks are one line**, the same on every page; there's no per-page or first-page-only watermark.
- **Page number fields are drawn in headers and footers only.** In the content they're left out with ZPDF-R001. A header or footer is the same on every page (no first-page or odd/even variants), and sits 12 pt from the content.
- **Table rows split across pages only when taller than a page**; a row that fits moves to the next page whole. Rows joined by a `rowSpan` never split, so a group taller than a page runs past the bottom margin (ZPDF-L001). One header row, from the column headers, and no nested tables.
- **Bold and italic need their faces for good type.** A family without them gets a synthetic face (thickened or slanted), reported as ZPDF-F003 (Info).
- **Fallback fonts work per character**, with pair kerning only within one font; shaping (ligatures, Arabic joining, Indic) isn't done.
- **No widow and orphan control** yet: a paragraph can leave one line at the foot or top of a page. Headings and blocks with `KeepWithNext` stay with the next block's first line.
- **Lengths come back from JSON in points.** `"2cm"` is read as 56.6929 points, and `ToJson` writes 56.6929.
- **PDF/A-2 only** (2b, 2u, 2a), with an sRGB output intent: no PDF/A-3 (attachments) or PDF/A-4 yet, and CMYK images must be converted to RGB (`ImagingProcessor` does).
- **Page previews read only Zorg.Pdf's own PDFs** (`PdfPreview` refuses others), draw DeviceCMYK images with a plain conversion rather than a colour profile, and depend on Zorg.Imaging's experimental canvas.
- **Fonts:** TrueType outlines only (`.ttf`, and the TrueType faces of collections, `.ttc`); no CFF (`.otf` with PostScript outlines), no symbol fonts.
- **Text:** pair kerning only. No ligatures, no Arabic joining or Indic shaping, no right-to-left text, no hyphenation dictionary (soft hyphens work).
