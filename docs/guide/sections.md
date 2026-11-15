# Headers, footers and sections

## Headers and footers

`Header` and `Footer` are page bands: blocks drawn on every page, outside the content area, as pagination artifacts that screen readers skip. Fields fill in the page number and the page count. The count is known only at the end, so it's drawn last without holding pages back.

```csharp
var report = document with
{
    Header = new PageBand([new Paragraph("Fjordlys AS · Annual report 2026") { Align = TextAlign.Right, Style = "header" }]),
    Footer = new PageBand([new Paragraph(["Page ", Field.PageNumber, " of ", Field.PageCount]) { Align = TextAlign.Right, Style = "footer" }]),
};
```

```json
"footer": { "content": [{ "type": "paragraph", "style": "footer", "align": "right", "inlines": ["Page ", { "field": "pageNumber" }, " of ", { "field": "pageCount" }] }] }
```

## Sections

A `SectionBreak` starts a new page and, from there on, can change the page numbering and the header and footer. Typical uses are a cover without a footer, roman numbers for front matter, and an appendix numbered A-1, A-2.

```csharp
var thesis = new PdfDocument("Thesis")
{
    Numbering = PageNumberStyle.LowerRoman,
    Content =
    [
        new Heading("A study of light"),
        new SectionBreak { Footer = new PageBand([new Paragraph([Field.PageNumber]) { Align = TextAlign.Center }]) },
        new TableOfContents { Title = "Contents" },
        new SectionBreak { Numbering = PageNumberStyle.Number, StartAt = 1 },
        new Heading("Introduction"),
        new SectionBreak { Numbering = PageNumberStyle.Number, StartAt = 1, Prefix = "A-", Header = PageBand.None },
        new Heading("Appendix"),
    ],
};
```

- A section without `Header` or `Footer` keeps the one before it. `PageBand.None` (JSON `{ "content": [] }`) removes it.
- The numbering shows in page number fields, in the table of contents and as the viewer's page labels, so "page iv" in the viewer is page iv on paper.
- Numbering styles are `number`, `lowerRoman`, `upperRoman`, `lowerLetter` and `upperLetter`.

[handbook.json](../examples/handbook.json) and [annual-report.json](../examples/annual-report.json) both start with a page without a footer.
