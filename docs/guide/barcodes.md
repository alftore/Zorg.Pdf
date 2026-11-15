# Barcodes and payments

Barcodes are drawn as vectors by Zorg.Pdf's own encoders, with no dependency. The tests decode every kind with zbar to check them.

| Kind | JSON `kind` | Data |
|---|---|---|
| `BarcodeKind.Qr` | `qr` | Any text, in UTF-8; versions 1 to 40, error correction L, M (default), Q or H |
| `BarcodeKind.Code128` | `code128` | Printable ASCII; the code set is picked for the shortest code |
| `BarcodeKind.Gs1_128` | `gs1_128` | GS1 element strings such as `(00)370123456789012342`, for parcels and pallets |
| `BarcodeKind.Ean13` | `ean13` | 12 digits (the check digit is added) or 13 (it's checked) |

```csharp
var parcel = new Barcode(BarcodeKind.Gs1_128, "(00)370123456789012342") { Height = Length.Cm(2), Alt = "Parcel label code" };
var product = new Barcode(BarcodeKind.Ean13, "590123412345") { Alt = "Product number 5901234123457" };
var link = new Barcode(BarcodeKind.Qr, "https://example.com/report") { Size = Length.Cm(2.5), Alt = "QR code linking to the online report" };
```

```json
{ "type": "barcode", "kind": "qr", "data": "https://example.com/report", "size": "2.5cm", "alt": "QR code linking to the online report" }
```

Each barcode needs alt text unless it's decorative, as images do. Linear codes print their data under the bars unless `ShowText` is false. `Bind` takes the data from the template model.

## Payment QR codes (EPC / GiroCode)

Banking apps across the SEPA area scan the European Payments Council's QR format to fill in a transfer. `EpcPayment` builds and checks the payload: the name, the IBAN (with its check digits verified), the amount in euros and the reference.

```csharp
EpcPayment epc = EpcPayment.Create("ACME GmbH", "DE02120300000000202051", 1190.00m, "Invoice 2026-0042");
var qr = new Barcode(BarcodeKind.Qr, epc.ToString()) { Size = Length.Cm(3), Alt = "Payment QR code" };
```

## Swiss QR-bills

A `SwissQrBill` block draws the payment part and receipt that Swiss invoices carry: a 210 × 105 mm strip across the bottom of an A4 page, with the Swiss QR code, as SIX's guidelines (v2.4) and style guide lay it out. Every field is checked before anything is written (ZPDF-J035, the path names the field):

```csharp
var bill = new SwissQrBill("CH44 3199 9123 0008 8901 2", new SwissQrAddress("Muster Kaffeerösterei GmbH", "8001", "Zürich") { Street = "Bahnhofstrasse", HouseNumber = "12" })
{
    Amount = 793.00m,
    Reference = SwissQrReferences.QrReference("21000000000313947143000901"),
    Debtor = new SwissQrAddress("Café Seeblick AG", "8800", "Thalwil") { Street = "Seestrasse", HouseNumber = "45" },
    Message = "Rechnung 2026-1042",
};
```

- **Account and reference:** a QR-IBAN (its bank number is 30000 to 31999) needs a 27-digit QR reference and CHF. An ordinary IBAN takes an `RF` creditor reference or none, in CHF or EUR. `SwissQrReferences` makes both kinds with their check digits.
- **Blank fields:** leave out `Amount` or `Debtor` and the bill shows marked boxes for the payer to fill in.
- **Headings:** in the document's language if it's German, French, Italian or Romansh, otherwise English; or set `Language`.
- **Page:** the bill goes at the bottom of the page it's on, or of the next page if there isn't room. Content after it starts a new page, and that page's footer is left out. The page must be A4 portrait.
- **Fonts:** the guidelines allow only Arial, Frutiger, Helvetica or Liberation Sans. Another font gets an Info note (ZPDF-L010).
- **Templates:** `"bind": "bill"` takes the fields that change per bill (amount, reference, debtor, message and the rest) from an object in the model, and the bill is checked once they're in.
- **The code itself:** `ToPayload()` returns what's in the code, if you need it elsewhere.

In JSON:

```json
{
  "$schema": "https://alftore.github.io/Zorg.Pdf/schema/document-0.1.schema.json",
  "title": "Rechnung 2026-1042",
  "language": "de-CH",
  "content": [
    {
      "type": "swissQrBill",
      "account": "CH44 3199 9123 0008 8901 2",
      "creditor": { "name": "Muster Kaffeerösterei GmbH", "street": "Bahnhofstrasse", "houseNumber": "12", "postalCode": "8001", "town": "Zürich" },
      "amount": 793.00,
      "reference": "210000000003139471430009017"
    }
  ]
}
```

[qr-bill.json](../examples/qr-bill.json) is a whole invoice with one.

> **Not verified in practice.** No Swiss QR-bill from Zorg.Pdf has been scanned with a Swiss banking app, paid, or checked with SIX's online validator; the maintainer isn't in Switzerland and can't do it. Here's what has been checked. The payload matches the guidelines' own example. The check digits match their worked examples. Rendered bills decode with zxing-cpp to exactly their payload, and they pass veraPDF. The layout follows the guidelines' measurements, but nobody has measured a printed bill. The Swiss cross follows the Swiss flag's proportions, because SIX's logo file wasn't used. zbar, which the Linux gate uses for other codes, can't read codes with the cross over them. **Before you send real bills, scan one with your bank's app and run it through SIX's validator.** If you can report how it went, please open an issue.

## Norwegian KID numbers

A KID is the customer reference on a Norwegian giro, a number with a MOD10 or MOD11 check digit. It's printed as text, not as a barcode:

```csharp
string kid = Kid.Create("2026004217", KidMethod.Mod10);   // "20260042177"
bool ok = Kid.IsValid(kid, KidMethod.Mod10);
```
