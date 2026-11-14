# Zorg.Pdf.Hyphenation

Hyphenation for [Zorg.Pdf](https://www.nuget.org/packages/Zorg.Pdf): Liang's patterns, as TeX uses them, for **English** (US and UK), **German** (1996 spelling), **French**, **Norwegian** (bokmål and nynorsk) and **Danish**. Justified and narrow text breaks long words where the language allows instead of leaving gaps or breaking them between letters.

**Pre-release: the API will change before 1.0.**

```csharp
using Zorg.Pdf;
using Zorg.Pdf.Hyphenation;

var options = new RenderOptions { Fonts = fonts, Hyphenator = PatternHyphenator.Default };
// In the document: a style with Hyphens = Hyphens.Auto (JSON "hyphens": "auto"), and the document's or the text's language.
```

- Only a word that doesn't fit at the end of a line is hyphenated; headings aren't, unless their style says so.
- A hyphen added at a line end copies out of the PDF as a soft hyphen, so copied text keeps whole words.
- Each language's patterns are read on first use (German, the largest, in about 45 ms) and kept for the process.

## Patterns and their licences

The pattern files come from the [hyph-utf8](https://github.com/hyphenation/tex-hyphen) project and ship unchanged, with their notices, in the package's `patterns` folder:

| Language | File | Licence |
|---|---|---|
| English (US) | `hyph-en-us.tex` | Copying and distribution permitted, with or without modification, provided the notice is kept (Gerard D.C. Kuiken) |
| English (UK) | `hyph-en-gb.tex` | MIT (Dominik Wujastyk, Graham Toal) |
| German (1996) | `hyph-de-1996.tex` | MIT |
| French | `hyph-fr.tex` | MIT (Daniel Flipo, Bernard Gaulle, Arthur Reutenauer) |
| Norwegian | `hyph-no.tex`, `hyph-nb.tex`, `hyph-nn.tex` | Copying and distribution permitted, with or without modification, provided the notice is kept (Rune Kleveland, Ole Michael Selberg, Karl Ove Hufthammer) |
| Danish | `hyph-da.tex` | MIT, or LPPL 1.3 or later (Frank Jensen) |

## Documentation

- [Known limits](https://github.com/alftore/Zorg.Pdf/blob/main/docs/limits.md)
- [Changelog](https://github.com/alftore/Zorg.Pdf/blob/main/CHANGELOG.md)
