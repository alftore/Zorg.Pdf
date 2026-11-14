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
- **Sections** change the page numbering, header and footer, not yet the page size or orientation.
- **Tables of contents** list top-level headings only, one per document, with arabic page numbers; the number column is as wide as the largest page count allowed (`Limits.MaxPages`).
- **Links** work in headings, paragraphs and list items; not yet in table cells, text boxes, headers or footers. They're coloured, not underlined.
- **Ids and bookmarks:** ids work on top-level blocks only; a table, image or list that moves to the next page whole is found a page early by its destination. Headings in lists, tables and boxes don't become bookmarks.
- **PDF/A-2 only** (2b, 2u, 2a), with an sRGB output intent: no PDF/A-3 (attachments) or PDF/A-4 yet, and CMYK images must be converted to RGB (`ImagingProcessor` does).
- **Page previews read only Zorg.Pdf's own PDFs** (`PdfPreview` refuses others), draw DeviceCMYK images with a plain conversion rather than a colour profile.
- **Fonts:** TrueType outlines only (`.ttf`, and the TrueType faces of collections, `.ttc`); no CFF (`.otf` with PostScript outlines), no symbol fonts.
- **Markdown** (`Zorg.Pdf.Markdown`): raw HTML is left out; strikethrough is drawn plain; an image inside a line of text keeps only its description; links in table cells keep only their text; headings, tables and code inside list items are flattened or left out; footnotes, definition lists and maths aren't read. Each is reported.
- **Text:** pair kerning only. No ligatures, no Arabic joining or Indic shaping, no right-to-left text, hyphenation by patterns for English, German, French, Norwegian and Danish only (`Zorg.Pdf.Hyphenation`), greedy line breaking (no paragraph-wide optimisation).
- **Reading existing PDFs** (`PdfFileInfo`): encrypted files say so, and their page sizes are read when their page tree isn't in an encrypted object stream; nothing else of them is read yet. Image codecs (DCT, JPX, CCITT, JBIG2) aren't decoded, as nothing needs them yet. Files over 500 MB, with over a million objects or 100,000 pages, are refused by default (`PdfReadLimits`).
- **Backgrounds** (`PageBackground`): one page of the file for the first page and one for the rest; sections can't change it yet. Links, form fields and other annotations on the letterhead page aren't copied, nor its tags (it's an artifact). An encrypted letterhead can't be used. Previews draw a third-party letterhead only when it uses what Zorg.Pdf itself writes (TrueType text, paths, images); otherwise the preview names what it can't draw.
- **Merging and extracting** (`PdfFiles`): the new file isn't tagged, has no bookmarks or page labels, and form fields in it can't be filled in; encrypted files can't be used yet. It's written as PDF 1.7 whatever the sources' versions.
- **Attachments**: at the document level only (no file attachment annotations on a page). Merging and extracting don't carry a source file's attachments over.
- **Factur-X and ZUGFeRD** (`Zorg.Pdf.Invoicing`): the CII XML is supplied, not made from the document, and isn't checked against EN 16931's business rules or kept in step with the PDF's content; that's the invoicing system's job. UBL isn't embedded. Order-X and other document types aren't handled.
