#pragma warning disable IDE0059 // Unnecessary assignment of a value

namespace Zorg.Pdf.Tests.Guide;

/// <summary>barcodes.md's Swiss QR-bill.</summary>
internal static partial class GuideSnippets
{
    // barcodes.md
    public static SwissQrBill SwissQrBillSnippet()
    {
        var bill = new SwissQrBill("CH44 3199 9123 0008 8901 2", new SwissQrAddress("Muster Kaffeerösterei GmbH", "8001", "Zürich") { Street = "Bahnhofstrasse", HouseNumber = "12" })
        {
            Amount = 793.00m,
            Reference = SwissQrReferences.QrReference("21000000000313947143000901"),
            Debtor = new SwissQrAddress("Café Seeblick AG", "8800", "Thalwil") { Street = "Seestrasse", HouseNumber = "45" },
            Message = "Rechnung 2026-1042",
        };
        return bill;
    }
}
