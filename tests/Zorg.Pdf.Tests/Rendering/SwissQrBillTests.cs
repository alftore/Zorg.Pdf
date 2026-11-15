using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>The Swiss QR-bill against SIX's guidelines v2.4: references, the payload of Annex A's example 2, the rules, the layout.</summary>
public sealed class SwissQrBillTests
{
    private static readonly SwissQrAddress Creditor = new("Max Muster & Söhne (sample company)", "8000", "Seldwyla") { Street = "Musterstrasse", HouseNumber = "123" };
    private static readonly SwissQrAddress Debtor = new("Simon Muster", "8000", "Seldwyla") { Street = "Musterstrasse", HouseNumber = "1" };

    /// <summary>Annex A, example 2 (without its alternative procedure).</summary>
    private static SwissQrBill Example() => new("CH44 3199 9123 0008 8901 2", Creditor)
    {
        Amount = 1949.75m,
        Debtor = Debtor,
        Reference = "21 00000 00003 13947 14300 09017",
        Message = "Order from 15.10.2020",
        BillInformation = "//S1/10/1234/11/201021/30/102673386/32/7.7/40/0:30",
    };

    [Fact]
    public void ReferencesHaveTheGuidelinesCheckDigits()
    {
        Assert.Equal("210000000003139471430009017", SwissQrReferences.QrReference("21 00000 00003 13947 14300 0901")); // Annex B's worked example.
        Assert.Equal("000008207791225857421286694", SwissQrReferences.QrReference("820779122585742128669")); // Annex A, example 1.
        Assert.True(SwissQrReferences.IsValidQrReference("000008207791225857421286694"));
        Assert.False(SwissQrReferences.IsValidQrReference("000008207791225857421286695"));
        Assert.False(SwissQrReferences.IsValidQrReference("000000000000000000000000000"));

        Assert.Equal("RF18539007547034", SwissQrReferences.CreditorReference("5390 0754 7034"));
        Assert.True(SwissQrReferences.IsValidCreditorReference("rf18 5390 0754 7034"));
        Assert.False(SwissQrReferences.IsValidCreditorReference("RF19539007547034"));
    }

    [Fact]
    public void ThePayloadIsAnnexAsExampleTwo()
    {
        string expected = string.Join('\n', [
            "SPC", "0200", "1", "CH4431999123000889012",
            "S", "Max Muster & Söhne (sample company)", "Musterstrasse", "123", "8000", "Seldwyla", "CH",
            "", "", "", "", "", "", "",
            "1949.75", "CHF",
            "S", "Simon Muster", "Musterstrasse", "1", "8000", "Seldwyla", "CH",
            "QRR", "210000000003139471430009017", "Order from 15.10.2020", "EPD",
            "//S1/10/1234/11/201021/30/102673386/32/7.7/40/0:30"]);
        Assert.Equal(expected, Example().ToPayload());

        // Without billing information the payload ends with the trailer, no separator after it; blank amount and debtor stay as empty lines.
        string plain = (Example() with { Amount = null, Debtor = null, BillInformation = null, Message = null }).ToPayload();
        Assert.EndsWith("\nQRR\n210000000003139471430009017\n\nEPD", plain, StringComparison.Ordinal);
        Assert.Equal(31, plain.Split('\n').Length);
        Assert.Contains("\n\nCHF\n\n\n\n\n\n\n\nQRR", plain, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("qrr-with-iban", "/content/0/reference")]
    [InlineData("rf-with-qr-iban", "/content/0/reference")]
    [InlineData("eur-with-qr-iban", "/content/0/currency")]
    [InlineData("bad-iban", "/content/0/account")]
    [InlineData("german-iban", "/content/0/account")]
    [InlineData("amount", "/content/0/amount")]
    [InlineData("character", "/content/0/message")]
    [InlineData("long", "/content/0/message")]
    [InlineData("country", "/content/0/creditor/country")]
    [InlineData("town", "/content/0/debtor/town")]
    public void BillsThatBreakTheGuidelinesAreErrors(string fault, string path)
    {
        SwissQrBill bill = fault switch
        {
            "qrr-with-iban" => Example() with { Account = "CH93 0076 2011 6238 5295 7" },
            "rf-with-qr-iban" => Example() with { Reference = "RF18539007547034" },
            "eur-with-qr-iban" => Example() with { Currency = "EUR" },
            "bad-iban" => Example() with { Account = "CH44 3199 9123 0008 8901 3" },
            "german-iban" => Example() with { Account = "DE02 1203 0000 0000 2020 51", Reference = null },
            "amount" => Example() with { Amount = 0.001m },
            "character" => Example() with { Message = "Rechnung → 42" },
            "long" => Example() with { Message = new string('x', 100) },
            "country" => Example() with { Creditor = Creditor with { Country = "ch" } },
            _ => Example() with { Debtor = Debtor with { Town = "" } },
        };
        var document = new PdfDocument("Invoice") { Content = [bill] };

        PdfDiagnostic diagnostic = Assert.Single(document.Validate().Diagnostics);
        Assert.Equal(("ZPDF-J035", path), (diagnostic.Code, diagnostic.Path));
        Assert.Throws<ArgumentException>(() => bill.ToPayload());
    }

    [Fact]
    public void AnOrdinaryIbanTakesACreditorReferenceOrNone()
    {
        SwissQrBill scor = Example() with { Account = "CH93 0076 2011 6238 5295 7", Reference = "RF18 5390 0754 7034", Currency = "EUR" };
        Assert.Contains("\nSCOR\nRF18539007547034\n", scor.ToPayload(), StringComparison.Ordinal);
        Assert.Contains("\nNON\n\n", (scor with { Reference = null }).ToPayload(), StringComparison.Ordinal);
    }

    [Fact]
    public void ABillRoundTripsThroughJsonAndNeedsAnA4PortraitPage()
    {
        PdfDocument document = PdfDocument.ParseJson("""
            {"title":"Invoice","content":[{"type":"swissQrBill","account":"CH4431999123000889012","amount":1949.75,"currency":"CHF",
             "creditor":{"name":"Max Muster & Söhne","street":"Musterstrasse","houseNumber":"123","postalCode":"8000","town":"Seldwyla"},
             "reference":"210000000003139471430009017","language":"de"}]}
            """);
        SwissQrBill bill = Assert.IsType<SwissQrBill>(document.Content[0]);
        Assert.Equal((1949.75m, "CH", "de"), (bill.Amount!.Value, bill.Creditor.Country, bill.Language));
        Assert.True(document.Validate().IsValid);
        Assert.Equal(document.ToJson(), PdfDocument.ParseJson(document.ToJson()).ToJson());

        PdfDocument letter = document with { Page = document.Page with { Size = PageSize.Letter } };
        Assert.Equal("ZPDF-J035", Assert.Single(letter.Validate().Diagnostics).Code);
    }

    [Fact]
    public async Task ABoundBillTakesItsFieldsFromTheModelAndIsCheckedThen()
    {
        const string Template = """
            {"title":"Invoice","content":[{"type":"swissQrBill","account":"CH4431999123000889012",
             "creditor":{"name":"Max Muster & Söhne","postalCode":"8000","town":"Seldwyla"},"bind":"bill"}]}
            """;
        PdfDocument document = PdfDocument.ParseJson(Template);
        Assert.True(document.Validate().IsValid); // No reference yet: checked once the model fills it in.

        RenderOptions good = PdfInspect.Options() with { Data = PdfData.ParseJson("""{"bill":{"amount":793.00,"reference":"210000000003139471430009017","debtor":{"name":"Café Seeblick AG","postalCode":"8800","town":"Thalwil"}}}""") };
        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, good);
        Assert.Single(PdfInspect.Elements(file, "Figure"));
        Assert.Equal(1, result.PageCount);

        RenderOptions bad = PdfInspect.Options() with { Data = PdfData.ParseJson("""{"bill":{"amount":793.00,"reference":"RF18539007547034"}}""") };
        PdfDocumentException error = await Assert.ThrowsAsync<PdfDocumentException>(async () => await RenderTests.RenderAsync(document, bad));
        Assert.Contains(error.Diagnostics, d => d is { Code: "ZPDF-J035", Path: "/content/0/reference" });
    }

    [Theory]
    [InlineData("\"793,00\"", "isn't a number like 793.00")]
    [InlineData("\"1'949.75\"", "isn't a number like 793.00")]
    [InlineData("\"CHF 793.00\"", "isn't a number like 793.00")]
    [InlineData("true", "isn't a number like 793.00")]
    [InlineData("793.00, \"debtor\": \"Simon Muster\"", "must be an object")]
    public async Task ABoundValueThatCantBeUsedIsAnErrorNotABlank(string fields, string message)
    {
        PdfDocument document = PdfDocument.ParseJson("""
            {"title":"Invoice","content":[{"type":"swissQrBill","account":"CH4431999123000889012","reference":"210000000003139471430009017",
             "creditor":{"name":"Max Muster & Söhne","postalCode":"8000","town":"Seldwyla"},"bind":"bill"}]}
            """);
        RenderOptions options = PdfInspect.Options() with { Data = PdfData.ParseJson("""{"bill":{"amount":""" + fields + "}}") };

        PdfDocumentException error = await Assert.ThrowsAsync<PdfDocumentException>(async () => await RenderTests.RenderAsync(document, options));
        PdfDiagnostic diagnostic = Assert.Single(error.Diagnostics, d => d.Code == "ZPDF-J035");
        Assert.Equal("/content/0/bind", diagnostic.Path);
        Assert.Contains(message, diagnostic.Message, StringComparison.Ordinal);

        RenderOptions plain = PdfInspect.Options() with { Data = PdfData.ParseJson("""{"bill":{"amount":"1949.75"}}""") };
        (_, RenderResult result) = await RenderTests.RenderAsync(document, plain);
        Assert.Equal(1, result.PageCount);
    }

    [Fact]
    public void TheCodeMustFitVersion25AndElementsAreTrimmed()
    {
        // Every field at its longest, in letters that take two bytes in UTF-8: under 997 characters, over version 25.
        string Long(int n) => new('é', n);
        var longest = new SwissQrAddress(Long(70), Long(16), Long(35)) { Street = Long(70), HouseNumber = Long(16) };
        SwissQrBill big = Example() with { Creditor = longest, Debtor = longest, Message = Long(140), BillInformation = null };
        PdfDiagnostic tooBig = Assert.Single(new PdfDocument("x") { Content = [big] }.Validate().Diagnostics);
        Assert.Contains("version 25", tooBig.Message, StringComparison.Ordinal);

        // Blank-only elements aren't padding in the code.
        string trimmed = (Example() with { Debtor = Debtor with { Street = "   ", HouseNumber = " 1 " }, Message = "  Order  " }).ToPayload();
        Assert.Contains("\nS\nSimon Muster\n\n1\n8000\n", trimmed, StringComparison.Ordinal);
        Assert.Contains("\nOrder\nEPD", trimmed, StringComparison.Ordinal);

        // Billing information alone over the limit is reported where it is.
        PdfDiagnostic billing = Assert.Single(new PdfDocument("x") { Content = [Example() with { Message = null, BillInformation = "//S1/10/" + new string('1', 140) }] }.Validate().Diagnostics);
        Assert.Equal("/content/0/billInformation", billing.Path);
    }

    [Fact]
    public async Task LongValuesAreCutToFitAndItsDiagnosticsNameTheBlock()
    {
        var longest = new SwissQrAddress("Pia-Maria Rutschmann-Schnyder & Söhne, Kaffeerösterei & Feinkost AG", "9400", "Rorschach am Bodensee") { Street = "Grosse Marktgasse beim alten Bahnhof gleich neben der Post", HouseNumber = "28a-28c" };
        SwissQrBill bill = Example() with { Creditor = longest, Debtor = longest, Message = new string('x', 80), BillInformation = null };
        (_, RenderResult result) = await RenderTests.RenderAsync(new PdfDocument("Invoice") { Content = [new Paragraph("Text"), bill] });

        Assert.Equal(1, result.PageCount);
        Assert.All(result.Diagnostics.Where(d => d.Code != "ZPDF-F003"), d => Assert.StartsWith("/content/1", d.Path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSlipTakesThePageBottomAndWhatFollowsGoesOn()
    {
        var document = new PdfDocument("Invoice 1042")
        {
            Language = "de-CH",
            Footer = new PageBand([new Paragraph("Footer text")]),
            Content = [new Heading("Rechnung 1042"), new Paragraph("Vielen Dank für Ihren Auftrag."), Example(), new Paragraph("Allgemeine Geschäftsbedingungen")],
        };
        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, PdfInspect.Options(PdfConformance.PdfUA1));

        Assert.Equal(2, result.PageCount);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Single(PdfInspect.Elements(file, "Figure"));
        Assert.Contains("/Alt (Swiss QR code for the payment)", PdfInspect.Text(file), StringComparison.Ordinal);
        var overlays = PdfInspect.PageOverlays(file);
        Assert.Null(overlays[0]); // No footer under the slip, and no header: no band stream at all.
        Assert.NotNull(overlays[1]);
    }
}
