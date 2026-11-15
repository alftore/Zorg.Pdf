using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Barcodes;
using Zorg.Pdf.Tests.Writing;

namespace Zorg.Pdf.Tests.Rendering;

/// <summary>Barcodes and payment codes. Whether scanners read them is checked in the Linux gate with zbar.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1814:Prefer jagged arrays over multidimensional", Justification = "QR grids are square.")]
public sealed class BarcodeTests
{
    [Theory]
    [InlineData(1, 0, 19)]
    [InlineData(1, 1, 16)]
    [InlineData(1, 2, 13)]
    [InlineData(1, 3, 9)]
    [InlineData(10, 1, 216)]
    [InlineData(40, 0, 2956)]
    [InlineData(40, 3, 1276)]
    public void QrCapacitiesMatchTheStandardsTables(int version, int level, int dataCodewords) =>
        Assert.Equal(dataCodewords, QrEncoder.DataCodewords(version, level));

    [Fact]
    public void QrCodesGrowWithTheirDataAndKeepTheirFixedPatterns()
    {
        bool[,] small = QrEncoder.Encode("https://example.com/", QrErrorCorrection.M);
        bool[,] large = QrEncoder.Encode(new string('x', 300), QrErrorCorrection.M);

        Assert.Equal(25, small.GetLength(0)); // Version 2: 20 bytes don't fit version 1 at M.
        Assert.True(large.GetLength(0) > 45);
        foreach (bool[,] grid in new[] { small, large })
        {
            int n = grid.GetLength(0);
            foreach ((int x, int y) in new[] { (0, 0), (n - 7, 0), (0, n - 7) })
            {
                // Finder: dark border, light ring, dark 3x3 centre.
                Assert.True(grid[y, x] && grid[y + 6, x + 6] && !grid[y + 1, x + 1] && grid[y + 3, x + 3]);
            }

            Assert.True(Enumerable.Range(8, n - 16).All(i => grid[6, i] == (i % 2 == 0))); // Timing pattern.
            Assert.True(grid[n - 8, 8]);                                                   // The dark module.
        }

        Assert.Equal(177, QrEncoder.Encode(new string('7', 7089), QrErrorCorrection.L).GetLength(0)); // The most digits: version 40.
        Assert.Throws<ArgumentException>(() => QrEncoder.Encode(new string('7', 7090), QrErrorCorrection.L));
    }

    [Fact]
    public void Code128PatternsAreElevenModulesAndDistinct()
    {
        Assert.All(Code128.Patterns.Take(106), p => Assert.Equal(11, p.Sum(c => c - '0')));
        Assert.Equal(13, Code128.Patterns[106].Sum(c => c - '0'));
        Assert.Equal(107, Code128.Patterns.Distinct().Count());
    }

    [Fact]
    public void Code128PicksCompactSetsAndChecksItsInput()
    {
        LinearCode digits = Code128.Encode("12345678901234");
        LinearCode text = Code128.Encode("Zorg-Pdf 0042");

        // Start C, 7 pairs, check: 9 symbols of 11 modules, and the 13-module stop.
        Assert.Equal((9 * 11) + 13, digits.Modules);
        // Start B, "Zorg-Pdf " (9), Code C, "00" "42", check: 14 symbols, and the stop.
        Assert.Equal((14 * 11) + 13, text.Modules);
        Assert.Throws<ArgumentException>(() => Code128.Encode("blåbær"));
    }

    [Fact]
    public void Gs1ElementsAreCheckedAndSeparated()
    {
        LinearCode fixedOnly = Code128.EncodeGs1("(01)09501101530003(17)261231");
        LinearCode withLot = Code128.EncodeGs1("(10)ABC(01)09501101530003");

        Assert.True(withLot.Modules > fixedOnly.Modules);
        Assert.Contains("check digit", Assert.Throws<ArgumentException>(() => Code128.EncodeGs1("(01)09501101530004")).Message, StringComparison.Ordinal);
        Assert.Contains("takes 14", Assert.Throws<ArgumentException>(() => Code128.EncodeGs1("(01)123")).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Code128.EncodeGs1("01 09501101530003"));
    }

    [Fact]
    public void Ean13AddsOrChecksItsCheckDigit()
    {
        Assert.Equal("5901234123457", Ean13.Encode("590123412345").Text);
        Assert.Equal(95, Ean13.Encode("5901234123457").Modules);
        Assert.Contains("should be 7", Assert.Throws<ArgumentException>(() => Ean13.Encode("5901234123458")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EpcPaymentsFollowTheGuidelines()
    {
        EpcPayment epc = EpcPayment.Create("ACME GmbH", "DE02 1203 0000 0000 2020 51", 1190m, "Invoice 2026-0042");
        EpcPayment referenced = EpcPayment.Create("ACME GmbH", "DE02120300000000202051", null, "RF18539007547034", "BYLADEM1001");

        Assert.Equal("BCD\n002\n1\nSCT\n\nACME GmbH\nDE02120300000000202051\nEUR1190.00\n\n\nInvoice 2026-0042", epc.ToString());
        Assert.Equal("BCD\n002\n1\nSCT\nBYLADEM1001\nACME GmbH\nDE02120300000000202051\n\n\nRF18539007547034", referenced.ToString());
        Assert.Contains("IBAN", Assert.Throws<ArgumentException>(() => EpcPayment.Create("A", "DE03120300000000202051", 1m)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => EpcPayment.Create("A", "DE02120300000000202051", 0.001m));
    }

    [Theory]
    [InlineData("1234567890", KidMethod.Mod10, "12345678903")]
    [InlineData("12345678", KidMethod.Mod11, "123456785")]
    [InlineData("0", KidMethod.Mod10, "00")]
    public void KidNumbersGetTheirCheckDigit(string digits, KidMethod method, string kid)
    {
        Assert.Equal(kid, Kid.Create(digits, method));
        Assert.True(Kid.IsValid(kid, method));
        Assert.False(Kid.IsValid(kid[..^1] + (kid[^1] == '9' ? '0' : (char)(kid[^1] + 1)), method));
    }

    [Fact]
    public void Mod11CanGiveADash()
    {
        // Weights 2 to 7 from the right: a remainder of 1 has no digit, and banks use '-'.
        string? dash = Enumerable.Range(1, 200).Select(n => Kid.Create(n.ToString(System.Globalization.CultureInfo.InvariantCulture), KidMethod.Mod11)).FirstOrDefault(k => k.EndsWith('-'));
        Assert.NotNull(dash);
        Assert.True(Kid.IsValid(dash!, KidMethod.Mod11));
    }

    [Fact]
    public async Task BarcodesAreDrawnAsTaggedFiguresAndBoundFromTheModel()
    {
        var document = new PdfDocument("Codes")
        {
            Content =
            [
                new Barcode(BarcodeKind.Qr, "https://example.com/") { Alt = "Link" },
                new Barcode(BarcodeKind.Ean13, "590123412345") { Alt = "Product number" },
                new Barcode(BarcodeKind.Qr, "") { Data = null, Bind = "payment", Alt = "Payment" },
            ],
        };
        RenderOptions inspect = PdfInspect.Options(PdfConformance.PdfUA1);

        (byte[] file, RenderResult result) = await RenderTests.RenderAsync(document, new RenderOptions { Fonts = inspect.Fonts, Conformance = PdfConformance.PdfUA1, Data = PdfData.Empty.WithModel(JsonDocument.Parse("""{"payment":"BCD\n002\n1\nSCT"}""").RootElement) });

        Assert.Empty(result.Diagnostics);
        PdfFile.CheckStructure(file);
        Assert.Equal(3, PdfInspect.Elements(file, "Figure").Count);
        string page = PdfInspect.PageContents(file)[0];
        Assert.True(page.Split(" re").Length > 100); // Runs of modules and bars as rectangles.
    }

    [Fact]
    public void BarcodesAreChecked()
    {
        var document = new PdfDocument("x")
        {
            Content =
            [
                new Barcode(BarcodeKind.Qr, "x"),
                new Barcode(BarcodeKind.Ean13, "123") { Alt = "a" },
                new Barcode(BarcodeKind.Code128, "x") { Bind = "y", Alt = "a" },
                new Barcode(BarcodeKind.Qr, "x") { Alt = "a", Size = Length.Zero },
            ],
        };

        Assert.Equal(["ZPDF-A003 /content/0/alt", "ZPDF-J032 /content/1/data", "ZPDF-J032 /content/2", "ZPDF-J013 /content/3/size"], document.Validate().Diagnostics.Select(d => $"{d.Code} {d.Path}"));
        PdfDocument parsed = PdfDocument.ParseJson("""{"title":"x","content":[{"type":"barcode","kind":"gs1_128","data":"(00)370123456789012342","alt":"SSCC","errorCorrection":"h","showText":false}]}""");
        Barcode barcode = Assert.IsType<Barcode>(parsed.Content[0]);
        Assert.Equal((BarcodeKind.Gs1_128, QrErrorCorrection.H, false), (barcode.Kind, barcode.ErrorCorrection, barcode.ShowText));
        Assert.Equal(parsed.ToJson(), PdfDocument.ParseJson(parsed.ToJson()).ToJson());
    }
}
