# Accessibility and archiving

## Tagged by default

Every PDF Zorg.Pdf writes is tagged. Headings, paragraphs, lists, tables (with header cells), figures with their alt text, links and the table of contents carry their structure. Page furniture (headers, footers, watermarks, backgrounds, fills) is marked as artifacts that screen readers skip. Reading order is the order of the content, and the document's language is declared.

## PDF/UA-1

`PdfConformance.PdfUA1` claims PDF/UA-1, the ISO standard for accessible PDF, and makes the checks behind it strict. Problems that would break the claim stop the render before any output instead of passing as warnings: an image, chart or barcode without alt text, a character no font has, a skipped heading level.

```csharp
var options = new RenderOptions { Fonts = fonts, Conformance = PdfConformance.PdfUA1, Archive = PdfArchive.PdfA2a };
```

Every example in [docs/examples](../examples) is checked with veraPDF, the reference validator, in the Linux gate. What the library can't check is whether your alt text is any good, your colours have enough contrast, or your headings make sense. That part is yours.

## PDF/A for archiving

`Archive` claims PDF/A, the ISO standard for long-term storage. Zorg.Pdf embeds the sRGB colour profile as the output intent, writes the identifiers in the XMP metadata, and refuses what PDF/A forbids, such as CMYK images without a profile.

| `PdfArchive` | Means |
|---|---|
| `PdfA2b` | Looks the same in 50 years |
| `PdfA2u` | ... and its text can be extracted as Unicode |
| `PdfA2a` | ... and it's tagged, the accessible level. Pairs with PDF/UA-1 |
| `PdfA3b`, `PdfA3u`, `PdfA3a` | The same, allowing attachments of any type, such as e-invoice XML |

On the command line: `--pdfua` and `--pdfa 2a` (or 2u, 2b, 3a, 3u, 3b).

## Attachments

Files can travel inside the PDF, such as the XML of an e-invoice or the spreadsheet behind a report. Under PDF/A-3 each one is tied to the document with a relationship: the source it was made from, data it presents, an alternative form, or a supplement.

```csharp
var invoice = document with { Attachments = [new Attachment("factur-x.xml", ImageSource.FromSlot("xml")) { MimeType = "text/xml", Relationship = AttachmentRelationship.Data }] };
var archived = new RenderOptions { Fonts = fonts, Archive = PdfArchive.PdfA3b, Data = PdfData.Empty.WithFile("xml", xmlBytes) };
```

```json
"attachments": [{ "name": "invoice.xml", "source": { "$data": "xml" }, "mimeType": "application/xml", "relationship": "data", "description": "The invoice as XML" }]
```

## Your own metadata

`MetadataSchemas` adds properties of your own to the XMP metadata, declared as PDF/A asks, so archive systems can read a case number or a customer id without parsing the pages:

```csharp
var archive = new XmpSchema("ACME archive", "https://acme.example/ns/archive/1.0/", "acme")
{
    Properties = [new XmpProperty("CaseNumber", "2026-0042") { Description = "The case the document belongs to" }],
};
var withCase = new RenderOptions { Fonts = fonts, Archive = PdfArchive.PdfA3b, MetadataSchemas = [archive] };
```
