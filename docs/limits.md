# Known limits

Zorg.Pdf is in early 0.1 development.

- **The document model can't be rendered yet.** `PdfDocument` can be built, validated and serialised; rendering comes in the next slice.
- **Lengths come back from JSON in points.** `"2cm"` is read as 56.6929 points, and `ToJson` writes 56.6929.
- **Fonts:** TrueType outlines only (`.ttf`); no CFF (`.otf` with PostScript outlines), no font collections (`.ttc`), no symbol fonts.
- **Text:** pair kerning only. No ligatures, no Arabic joining or Indic shaping, no right-to-left text, no hyphenation dictionary (soft hyphens work).
