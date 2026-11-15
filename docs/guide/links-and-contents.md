# Links, bookmarks and contents

## Links

A `Link` wraps inlines and points at a web address, an email address, or a block in the document by its `Id`. Links are tagged as Link elements with their text, so screen readers can list them. `Description` adds the alternate text PDF/UA asks for when the visible text alone isn't clear.

```csharp
var appendix = new Heading("Appendix B") { Id = "appendix-b" };
var see = new Paragraph(["See ", new Link(["Appendix B"]) { Target = "#appendix-b" }, " or ", new Link(["our terms"]) { Target = "https://example.com/terms" }, "."]);
var mail = new Paragraph(["Questions: ", new Link(["hr@example.com"]) { Target = "mailto:hr@example.com", Description = "Email HR" }]);
```

```json
{ "type": "paragraph", "inlines": ["See ", { "link": "#appendix-b", "inlines": ["Appendix B"] }, " or ", { "link": "https://example.com/terms", "inlines": ["our terms"] }, "."] }
```

Targets must be `https://`, `http://`, `mailto:` or `#` with an id some block has. Anything else, including a link to a missing id, is error ZPDF-J027 rather than a dead link. International host names are written as punycode. Links can't go in table cells, text boxes, or headers and footers yet.

## Bookmarks

Headings of levels 1 to `OutlineLevels` (3 by default) become the viewer's bookmarks, nested by level. Set it to `0` for none, or lower for long documents:

```csharp
var report = document with { OutlineLevels = 2 };
```

Blocks with an `Id` also become named destinations, so `report.pdf#appendix-b` opens the PDF at that block in most viewers.

## Tables of contents

A `TableOfContents` lists the headings after it, indented by level, with dot leaders and page numbers. Each entry links to its heading. Page numbers are filled in when the document is finished, without a second layout pass, so streaming still works. The entries use the styles `toc1` to `toc6`, and the numbers follow the [sections](sections.md)' numbering.

```csharp
var annual = new PdfDocument("Annual report 2026")
{
    Content = [new Heading("Annual report 2026"), new TableOfContents { Title = "Contents", MaxLevel = 2 }, new PageBreak(), new Heading("Introduction")],
};
```

```json
{ "type": "tableOfContents", "title": "Contents", "maxLevel": 2, "leaders": true }
```

Only headings at the top level of the content are listed, not ones inside tables or boxes. A heading before the table of contents, such as a title page's, isn't listed.
