# Diagnostics

Every problem Zorg.Pdf reports has a stable code, a severity, a message that says how to fix it, and a JSON pointer to where it is in the document, such as `/content/2/rows/1`. The pointer means the same for a document built in C#. A code never changes meaning; a retired code stays in this table, marked retired.

`PdfDocument.TryParseJson` reports the J00x codes. `PdfDocument.Validate` reports the rest of the J codes and the A codes. T codes come from layout, while rendering.

Areas: **J** JSON and model shape, **D** data slots, **F** fonts, **T** text, **L** layout and pagination, **I** images, **A** accessibility, **R** rendering and limits.

| Code | Severity | Meaning | Fix |
|---|---|---|---|
| ZPDF-J001 | Error | `$schema` names a different schema version than this release reads. | Use the schema in `PdfJsonSchema.Current`, or leave `$schema` out. |
| ZPDF-J002 | Error | A property isn't in the schema, often a misspelling. Properties are camelCase. | Check the name against the schema. |
| ZPDF-J003 | Error | The text isn't valid JSON, or a value is of the wrong kind (a string where a number belongs, a bad length or colour). | Fix the value at the pointer. |
| ZPDF-J004 | Error | A required property is missing, such as `title`, a block's `inlines` or a column's `header`. | Add it. |
| ZPDF-J005 | Error | A block has no `type`, or an unknown one. | Use `heading`, `paragraph`, `table`, `image`, `spacer` or `pageBreak`. |
| ZPDF-J006 | Error | The JSON is larger than 16 MiB. | Move large data, such as table rows and images, to data slots. |
| ZPDF-J010 | Error | A heading level isn't 1 to 6. | Use 1 for the main heading, 2 to 6 below it. |
| ZPDF-J011 | Error | A table has no columns. | Add at least one column. |
| ZPDF-J012 | Error | A row has a different number of cells from the table's columns. | Give every row one cell per column; use `""` for an empty cell. |
| ZPDF-J013 | Error | A length is negative, zero where a size is needed, not finite, or a million points or more. | Use a positive length, such as `"12pt"` or `"2cm"`. |
| ZPDF-J014 | Error | The page margins leave less than half an inch for content. | Make the margins smaller or the page larger. |
| ZPDF-J015 | Warning | A block names a style that doesn't exist, so its default style is used. | Add the style under `styles`, or use a built-in name. |
| ZPDF-J016 | Error | A column's share is 0 or less, or a fixed width isn't positive. | Use `"*"`, `"2*"` or a length such as `"3cm"`. |
| ZPDF-J017 | Error | A data slot has no name. | Give `$data` a name. |
| ZPDF-J018 | Error | A header or footer holds a block other than a paragraph, image or spacer. | Move tables and headings into the content. |
| ZPDF-J019 | Error | A line height isn't between 0 and 10. | Use a multiple of the font size, such as `1.2`. |
| ZPDF-A001 | Error | The document has no title. Viewers show it in place of the file name; PDF/UA requires one. | Set `title`. |
| ZPDF-A002 | Error | The language isn't a BCP 47 tag. Screen readers pick their voice from it. | Use a tag such as `en`, `nb-NO` or `de-CH`. |
| ZPDF-A003 | Error | An image has no alt text and isn't marked decorative. | Add `alt` saying what the image shows, or set `decorative: true`. |
| ZPDF-T001 | Warning | A word was wider than its column and was broken between characters. | Widen the column, make the text smaller, or add break points (spaces, soft hyphens). |
