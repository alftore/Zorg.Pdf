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

## Norwegian KID numbers

A KID is the customer reference on a Norwegian giro, a number with a MOD10 or MOD11 check digit. It's printed as text, not as a barcode:

```csharp
string kid = Kid.Create("2026004217", KidMethod.Mod10);   // "20260042177"
bool ok = Kid.IsValid(kid, KidMethod.Mod10);
```
