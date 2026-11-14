# Known limits

Zorg.Pdf is in early 0.1 development.

- **Rendering draws headings, paragraphs, spacers and page breaks.** Tables, images, headers, footers and page number fields are left out with warning ZPDF-R001; they come in later 0.1 slices.
- **One face per font family.** All text uses the regular face, so headings aren't bold yet, and styles on text runs (bold, italic, colour, size) aren't applied (diagnostic ZPDF-R001 at Info).
- **No widow and orphan control** yet: a paragraph can leave one line at the foot or top of a page. Headings and blocks with `KeepWithNext` stay with the next block's first line.
- **Lengths come back from JSON in points.** `"2cm"` is read as 56.6929 points, and `ToJson` writes 56.6929.
- **Fonts:** TrueType outlines only (`.ttf`); no CFF (`.otf` with PostScript outlines), no font collections (`.ttc`), no symbol fonts.
- **Text:** pair kerning only. No ligatures, no Arabic joining or Indic shaping, no right-to-left text, no hyphenation dictionary (soft hyphens work).
