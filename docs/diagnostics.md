# Diagnostics

Every problem Zorg.Pdf reports has a stable code, a severity, a message that says how to fix it, and a JSON pointer to where it is in the document, such as `/content/2/rows/1`. The pointer means the same for a document built in C#. A code never changes meaning; a retired code stays in this table, marked retired.

`PdfDocument.TryParseJson` reports the J00x codes. `PdfDocument.Validate` reports the rest of the J codes and the A codes. Rendering checks fonts, text, data slots and images before it writes anything (F codes, T002, D001, I codes, R001, R002), so errors never leave half a file behind; T001 comes from layout while rendering. Some severities depend on the conformance claimed: under `PdfConformance.PdfUA1`, F002 is an error.

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
| ZPDF-J012 | Error | A row's cells don't fill the columns it has (counting each cell's `columnSpan`, and leaving out columns a `rowSpan` from above covers), or a span is less than 1. | Give every row one cell per free column; use `""` for an empty cell. |
| ZPDF-J013 | Error | A length is negative, zero where a size is needed, not finite, or a million points or more. | Use a positive length, such as `"12pt"` or `"2cm"`. |
| ZPDF-J014 | Error | The page margins leave less than half an inch for content. | Make the margins smaller or the page larger. |
| ZPDF-J015 | Warning | A block names a style that doesn't exist, so its default style is used. | Add the style under `styles`, or use a built-in name. |
| ZPDF-J016 | Error | A column's share is 0 or less, or a fixed width isn't positive. | Use `"*"`, `"2*"` or a length such as `"3cm"`. |
| ZPDF-J017 | Error | A data slot has no name. | Give `$data` a name. |
| ZPDF-J018 | Error | A header or footer holds a block other than a paragraph, image or spacer. | Move tables and headings into the content. |
| ZPDF-J019 | Error | A line height isn't between 0 and 10. | Use a multiple of the font size, such as `1.2`. |
| ZPDF-J020 | Error | A list has no items, an item has no content, or `start` is negative. | Give every list items and every item text; number from 0 or more. |
| ZPDF-J021 | Error | A list item holds something other than paragraphs and lists, or lists nest more than six deep. | Put headings, tables and images between lists; flatten deep nesting. |
| ZPDF-J022 | Error | A watermark has neither text nor an image, or both; or its opacity isn't between 0 and 1, or its angle isn't a number. | Set `text` or `image`, and an opacity from 0 to 1. |
| ZPDF-J024 | Error | A text box has no place (no `preset`, or not all of `x`, `y`, `width` and `height`), a size of zero or less, a start off the page, padding that leaves no room, or content other than paragraphs. | Give it a `preset` or all four, inside the page, and paragraphs. |
| ZPDF-J025 | Error | A block's `id` isn't letters, digits, `-`, `_`, `.` and `:`; it's used twice; or it's on a block that can't be linked to yet (inside a repeat, list, table, box or band). | Give each top-level block that's linked to a unique id such as `appendix-b`. |
| ZPDF-J026 | Error | `outlineLevels` isn't 0 to 6. | Use 0 for no bookmarks, or the deepest heading level to include. |
| ZPDF-J027 | Error | A link's target isn't `https://`, `http://`, `mailto:` or `#` and an id some block has; it has no text, an empty description, another link or a field inside; or it's in a table cell, text box, header or footer, where links aren't supported yet. | Fix the target, or move the link into a paragraph. |
| ZPDF-J028 | Error | A table of contents has a `maxLevel` outside 1 to 6 or an empty title, isn't at the top level, or is the second in the document. | One table of contents, at the top level, listing levels 1 to 6. |
| ZPDF-J029 | Error | A section break has a `startAt` below 1, a prefix that's long or has control characters, or isn't at the top level. | Start at 1 or more, keep the prefix short, and put section breaks at the top level. |
| ZPDF-J030 | Error | A background has no source, or a page number below 0. | Give `source` (a data slot or base64) and pages from 1, or 0 for none. |
| ZPDF-J031 | Error | An attachment has a name that isn't a plain file name, the same name as another, no source, or a media type that isn't `type/subtype`. | Give each a unique file name such as `invoice.xml`, a source and a media type such as `application/xml`. |
| ZPDF-J023 | Error | A template part is incomplete: a `bind` without a path, a `repeat` without `items` or content, or a `when` without a `test` or content. | Give every binding a path such as `customer.name`, and every repeat and when their content. |
| ZPDF-B001 | Warning | A binding found no value in the model (or the render has no model), or the value can't be shown as text, or its `format` doesn't fit it. The text is left empty or unformatted. | Add the value to the model, fix the path, or bind a value inside the object. |
| ZPDF-B002 | Warning | A `repeat` names something in the model that isn't an array, so its content is left out. | Point `items` at an array. |
| ZPDF-D001 | Error | A table or image names a data slot (`{"$data": ...}`) that the render has no rows or image for. | Supply them: `RenderOptions.Data = PdfData.Empty.WithRows("slot", rows)` or `.WithImage("slot", bytes)`. |
| ZPDF-A001 | Error | The document has no title. Viewers show it in place of the file name; PDF/UA requires one. | Set `title`. |
| ZPDF-A002 | Error | The language isn't a BCP 47 tag. Screen readers pick their voice from it. | Use a tag such as `en`, `nb-NO` or `de-CH`. |
| ZPDF-A003 | Error | An image has no alt text and isn't marked decorative. | Add `alt` saying what the image shows, or set `decorative: true`. |
| ZPDF-A004 | Warning (Error under `PdfUA1`) | A heading skips a level: the first heading isn't level 1, or a heading is more than one level below the one before it. Screen readers navigate by level, so a skipped level reads as a missing section. | Use the next level down, or restructure the headings. |
| ZPDF-A005 | Error | The document has attachments and claims PDF/A-2, which allows only PDF/A files as attachments. | Claim PDF/A-3 (`PdfArchive.PdfA3b`, `3u` or `3a`), which allows any file. |
| ZPDF-F001 | Error | The render has no fonts. There are no system fonts by default. | Add one: `new RenderOptions { Fonts = FontCollection.Empty.Add(new FontFamily("Inter") { Regular = FontSource.FromFile("Inter-Regular.ttf") }) }`. |
| ZPDF-F002 | Warning (Error under PdfUA1) | No font in the run's chain (its face and the family's fallbacks) has a glyph for a character, so it's drawn as an empty box (.notdef). | Add a fallback family that covers the text, or change the text. |
| ZPDF-F003 | Info | The family has no bold or italic face that the text asks for, so it's drawn thickened or slanted from a face it has. | Add the real face to the `FontFamily` for better type. |
| ZPDF-F004 | Error | A style names a font family the render doesn't have, or the family has no regular face. | Add the family to `RenderOptions.Fonts`, or use one that's there. |
| ZPDF-F005 | Error | A font file can't be read or used: damaged, not TrueType, or its licence forbids embedding. | Use another font file; the message says why. |
| ZPDF-T001 | Warning | A word was wider than its column and was broken between characters. | Widen the column, make the text smaller, or add break points (spaces, soft hyphens). |
| ZPDF-T002 | Error | The text has a control character that can't be drawn. | Remove it; use a line feed (`\n`) to break a line. |
| ZPDF-T003 | Info | Text is set to `hyphens: auto`, but the render has no hyphenator, or the hyphenator has no rules for the text's language: only soft hyphens break words there. | Set `RenderOptions.Hyphenator` (`PatternHyphenator.Default` from Zorg.Pdf.Hyphenation), or set the text's `language` to one it knows. |
| ZPDF-M001 | Warning | Markdown (Zorg.Pdf.Markdown) has raw HTML, which isn't rendered: it was left out. The path is `markdown:line:column`. | Write it in Markdown, or leave it out. |
| ZPDF-M002 | Info | A Markdown construct was simplified or left out: strikethrough, an image inside text, a heading or table in a list item, front matter keys other than `title` and `lang`. | Restructure it, or accept the simpler form. |
| ZPDF-M003 | Info | A Markdown link's target isn't one a PDF can use (`https://`, `http://`, `mailto:` or `#id`), or the link is in a table cell: its text was kept without the link. | Use an absolute URL or a block id, and keep links out of tables for now. |
| ZPDF-M004 | Error | The Markdown is nested too deeply to read: lists, quotes, emphasis or links inside each other past the parser's depth limit. Nothing is rendered. | Flatten the nesting. |
| ZPDF-X001 | Error | Factur-X (Zorg.Pdf.Invoicing): the XML isn't well-formed, has a DTD, or is over 20 MB. | Give the XML your invoicing system wrote, unchanged. |
| ZPDF-X002 | Error | Factur-X: the XML's root isn't `rsm:CrossIndustryInvoice` in the CII namespace (it may be UBL). | Export CII (Factur-X/ZUGFeRD 2) from the invoicing system. |
| ZPDF-X003 | Error | Factur-X: the XML's `GuidelineSpecifiedDocumentContextParameter/ID` isn't a Factur-X or ZUGFeRD profile. | Use MINIMUM, BASIC WL, BASIC, EN 16931, EXTENDED or XRECHNUNG. |
| ZPDF-P001 | Warning | Reading an existing PDF: its cross-reference data is damaged or missing, or an object isn't where it says, so objects were found by scanning the file, as viewers do. The path is `file`. | Check the result; save the file again from a viewer to repair it. |
| ZPDF-P002 | Info | Reading an existing PDF: a stream's `/Length` is wrong, so its end was found by searching for `endstream`. | Nothing, usually; the writer that made the file has a bug. |
| ZPDF-P003 | Warning | Reading an existing PDF: a compressed stream is damaged, and only what could be decompressed was used. | Check the result; the page may be incomplete. |
| ZPDF-P004 | Warning | Reading an existing PDF: its page tree has loops, missing pages or nodes that aren't pages, which were skipped. | Check the page count. |
| ZPDF-G001 | Error | The background's PDF can't be used: it isn't a PDF, it's damaged beyond reading, it's encrypted, or its page's content uses a filter that can't be read. The message says which. | Save the letterhead again as a plain PDF (Print to PDF, or "Save as" without a password). |
| ZPDF-G002 | Error | The background asks for a page the PDF doesn't have. | Use a page number from 1 to the file's page count, or 0 for none. |
| ZPDF-G003 | Info | The background's page is a different size from the document's: it's scaled to fit and centred. | Make the letterhead the same paper size, or accept the scaling. |
| ZPDF-G004 | Error under PDF/UA or PDF/A, else Warning | The background uses a font that isn't embedded, so it looks different on other computers, and PDF/UA and PDF/A forbid it. | Export the letterhead with fonts embedded, or convert its text to outlines. |
| ZPDF-G005 | Error under PDF/A | The background uses CMYK colour, which PDF/A with Zorg.Pdf's sRGB output intent doesn't allow. | Export the letterhead in RGB. |
| ZPDF-E001 | Info | Merging or extracting pages: a source file is tagged, but tags aren't kept, so the new file isn't tagged. The path names the file (`file 2`). | Make the accessible version from the source document instead, if it must be accessible. |
| ZPDF-E002 | Info | Merging or extracting pages: a source file's bookmarks aren't kept. | Nothing, or add bookmarks in the source document. |
| ZPDF-E003 | Info | Merging or extracting pages: form fields are copied as they look, and can't be filled in or submitted in the new file. | Fill the form first, or keep the original. |
| ZPDF-E004 | Info | Merging or extracting pages: links to pages that weren't copied, or that run scripts, open other files or launch programs, aren't kept. | Nothing; the link's text and look are still there. |
| ZPDF-L001 | Warning | Rows joined by a `rowSpan` are taller together than a page, so they run past the bottom margin. A single row that tall splits across pages instead; rows joined by a span don't. | Shorten the spanned rows' text, or drop the span. |
| ZPDF-L002 | Warning | A row from a data slot doesn't fill the columns it has (counting spans); it's padded with empty cells or cut. | Give every row one cell per free column. |
| ZPDF-L003 | Warning | The fixed-width columns are wider than the page, so all columns are scaled to fit. | Make the fixed columns narrower, or use shares (`"2*"`). |
| ZPDF-L005 | Info | A text box's text was made smaller to fit (`overflow: shrinkToFit`). The message gives the size. | Nothing, or shorten the text, or make the box larger. |
| ZPDF-L006 | Warning | A text box's text doesn't fit: lines were left out (`clip`, or `shrinkToFit` at its minimum size), or run past the box (`visible`). | Shorten the text, make the box larger, or lower `minSize`. |
| ZPDF-L007 | Warning | A text box reaches past the edge of the page. | Move it or make it smaller. |
| ZPDF-L004 | Error | The header and footer leave less than 36 pt of the page for the content. | Shorten the header or footer, use smaller text, or narrow the top and bottom margins. |
| ZPDF-I001 | Error | An image can't be embedded: it isn't a JPEG or PNG, it's damaged, or it's a kind PDF can't hold (12-bit, lossless or arithmetic-coded JPEG). The message says which. | Save it again as an 8-bit baseline or progressive JPEG, or as a PNG. |
| ZPDF-I002 | Error | An image is over `RenderOptions.Limits.MaxImageBytes` or `MaxImagePixels`. | Make the image smaller, or raise the limit if the input is trusted. |
| ZPDF-R001 | Warning | This version doesn't draw that part yet (page number fields outside headers and footers), so it's left out. | Wait for the release that draws it, or leave it out. |
| ZPDF-R002 | Error | A synchronous `Render` was given rows from an `IAsyncEnumerable`, an image that loads asynchronously, or an image processor that doesn't finish at once, which it can't wait for. | Use `RenderAsync`, or give the rows as an `IEnumerable` and the image as bytes. |
| ZPDF-R003 | Error | A render stopped at a `RenderOptions.Limits` limit (pages or table rows) partway through. Reported by the agent tools; a direct render throws `PdfLimitException`. | Shorten the document or its data, or raise the limit if the input is trusted. |
| ZPDF-R004 | Error | An attachment is larger than `PdfLimits.MaxAttachmentBytes` (50 MB by default). | Attach a smaller file, or raise the limit. |
| ZPDF-R005 | Error | A `RenderOptions.MetadataSchemas` entry has a prefix or property name that isn't an XML name, no namespace, a missing value, or a category other than `external` or `internal`. | Fix the schema. |
