# Invoices

## Invoice PDFs

An invoice is an ordinary document. The examples [invoice.json](../examples/invoice.json), [receipt.json](../examples/receipt.json), [reminder.json](../examples/reminder.json) and [statement.json](../examples/statement.json) show the usual parts:
- a [window-envelope address](letters.md);
- line items from [data](tables-and-data.md);
- [totals with formats](templates.md);
- a [payment QR code](barcodes.md) or KID;
- "page X of Y".

## Factur-X and ZUGFeRD

Factur-X (France) and ZUGFeRD 2 (Germany) are the same e-invoice format: a PDF/A-3 file that people read, with the invoice's Cross Industry Invoice (CII) XML attached for software. `FacturX` (namespace `Zorg.Pdf.Invoicing`, in the core package) turns your document into one.

```csharp
FacturXInvoice facturX = FacturX.Read(await File.ReadAllBytesAsync("invoice.xml", cancellationToken));
await facturX.ApplyTo(invoice).RenderAsync(output, facturX.ApplyTo(new RenderOptions { Fonts = fonts, Conformance = PdfConformance.PdfUA1 }), cancellationToken);
```

- **The XML comes from your invoicing system.** Zorg.Pdf doesn't build CII, because the format changes twice a year and every system already makes it. `FacturX.Read` checks that it's CII and reads its profile (Minimum, Basic WL, Basic, EN 16931, Extended or XRechnung) from the guideline id. It reads safely: no DTDs and no external resources.
- **`ApplyTo(document)`** attaches the XML as `factur-x.xml` with the relationship receivers expect.
- **`ApplyTo(options)`** sets PDF/A-3 (3b unless you asked for 3a or 3u) and adds the `fx:` metadata.
- **Keep the PDF and the XML saying the same thing.** Bind both from the same data.

On the command line:

```bash
zorg-pdf render invoice.json --data invoice-data.json --facturx invoice.xml --pdfa 3a --pdfua
```

Check the result with a validator such as Mustang before sending invoices to customers who process them automatically. Mustang's checks aren't part of this repository's gate yet.

In Norway, B2B and public invoicing uses EHF (PEPPOL BIS) XML sent through the PEPPOL network, without a PDF. Factur-X is for customers in Germany and France.
