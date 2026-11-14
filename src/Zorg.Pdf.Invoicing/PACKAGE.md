# Zorg.Pdf.Invoicing

Factur-X and ZUGFeRD e-invoices for [Zorg.Pdf](https://www.nuget.org/packages/Zorg.Pdf): the invoice people read as a tagged PDF, with the Cross Industry Invoice (CII) XML your invoicing system writes embedded in it for software to read.

**Pre-release: the API will change before 1.0.**

```csharp
using Zorg.Pdf;
using Zorg.Pdf.Invoicing;

FacturXInvoice facturX = FacturX.Read(await File.ReadAllBytesAsync("invoice.xml", cancellationToken));
var options = facturX.ApplyTo(new RenderOptions { Fonts = fonts, Conformance = PdfConformance.PdfUA1, Archive = PdfArchive.PdfA3a });
await facturX.ApplyTo(invoice).RenderAsync(output, options, cancellationToken);
```

- Reads the profile from the XML: MINIMUM, BASIC WL, BASIC, EN 16931, EXTENDED or XRECHNUNG.
- Embeds the XML unchanged as `factur-x.xml` (`xrechnung.xml` for XRECHNUNG), with the relationship receivers expect.
- Writes the `fx:` metadata (document type, file name, version, conformance level) and its PDF/A extension schema.
- Claims PDF/A-3 at the level asked for, 3b at least.
- Reads the XML with DTDs and external resources off, and refuses anything that isn't CII.

It doesn't make the XML or check it against EN 16931's business rules: that's the invoicing system's job, and its validator's. The PDF's content is your document, so keep it saying what the XML says.

## Documentation

- [Diagnostics](https://github.com/alftore/Zorg.Pdf/blob/main/docs/diagnostics.md) (ZPDF-X001 to X003)
- [Changelog](https://github.com/alftore/Zorg.Pdf/blob/main/CHANGELOG.md)
