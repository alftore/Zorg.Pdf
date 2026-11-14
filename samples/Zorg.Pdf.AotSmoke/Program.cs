using System;
using System.IO;
using System.Text;
using Zorg.Pdf;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Spike;

// Published with Native AOT and executed in CI. Trimming keeps only what's used, so every
// feature has to be exercised here: a clean publish alone proves little. Add each new one.
//
// With a folder as the first argument, the PDFs are also saved there, for the oracle tools (qpdf, veraPDF).
int failures = 0;
string? saveTo = args.Length > 0 ? args[0] : null;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

Check("exception keeps its message", new PdfException("smoke").Message == "smoke");

// 0.1 slice 1: the document model through the source-generated JSON layer. Trimming would break reflection here.
{
    var document = new PdfDocument("Invoice 2026-0042")
    {
        Page = PageSettings.A4 with { Orientation = PageOrientation.Landscape, Margin = Thickness.All(Length.Cm(1.5)) },
        Footer = new PageBand([new Paragraph(["Page ", Field.PageNumber, " of ", Field.PageCount]) { Align = TextAlign.Right }]),
        Content =
        [
            new Heading("Invoice 2026-0042"),
            new Paragraph(["Due in ", new TextRun("30 days") { Style = TextStyle.Bold }, LineBreak.Value, "Thank you."]),
            new Table([new Column("Item") { Width = ColumnWidth.Star(3) }, new Column("Amount") { Width = ColumnWidth.Fixed(Length.Cm(3)), Align = TextAlign.Right }])
            {
                Rows = TableRows.Inline([["Consulting", "1 250,00"], [new Cell("Total") { Align = TextAlign.Right }, "1 250,00"]]),
            },
            new Image(ImageSource.FromSlot("logo")) { Alt = "Logo" },
            new Spacer(Length.Mm(5)),
            new PageBreak(),
        ],
    };
    string json = document.ToJson();
    PdfDocument back = PdfDocument.ParseJson(json);
    Check("model json round trip", back.ToJson() == json && back.Validate().IsValid && back.Page.Orientation == PageOrientation.Landscape);

    DocumentParseResult bad = PdfDocument.TryParseJson("""{"title":"t","content":[{"type":"heading","inlines":["x"],"colour":"red"}]}""");
    Check("model json diagnostics", !bad.IsSuccess && bad.Diagnostics[0].Code == "ZPDF-J002" && bad.Diagnostics[0].Path == "/content/0/colour");

    PdfDocument defaults = PdfDocument.ParseJson("""{"title":"t","content":[{"type":"heading","inlines":["x"]},{"type":"table","columns":[{"header":"A"}]}]}""");
    Check("model json defaults", defaults.Content[0] is Heading { Level: 1 } && defaults.Content[1] is Table { RepeatHeader: true, Rows: InlineTableRows } && defaults.Language == "en");
    Check("schema resource", PdfJsonSchema.Current.Contains(PdfJsonSchema.Id, StringComparison.Ordinal));
    if (saveTo is not null)
    {
        // The Linux gate validates this against the schema with an independent validator.
        File.WriteAllText(Path.Combine(saveTo, "model.json"), document.ToJson(indented: true));
    }
}

// Fonts come from the machine (no font file is committed): Arial on Windows, DejaVu Sans on Linux.
string? fontPath = Environment.GetEnvironmentVariable("ZORG_PDF_TEST_FONT");
if (string.IsNullOrEmpty(fontPath))
{
    fontPath = Array.Find(
        [@"C:\Windows\Fonts\arial.ttf", "/usr/share/fonts/dejavu/DejaVuSans.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/System/Library/Fonts/Supplemental/Arial.ttf"],
        File.Exists);
}

if (fontPath is null)
{
    // CI sets ZORG_REQUIRE_FONTS=1: a run that skips rendering proves nothing about it.
    Console.WriteLine("skip rendering: no TrueType font found (set ZORG_PDF_TEST_FONT)");
    Check("a font to render with, as ZORG_REQUIRE_FONTS asks", Environment.GetEnvironmentVariable("ZORG_REQUIRE_FONTS") != "1");
    return failures == 0 ? 0 : 1;
}

byte[] font = File.ReadAllBytes(fontPath);

// 0.1 slice 2: the public render API, sync and async, tagged for PDF/UA-1.
{
    const string Body = "Zorg.Pdf lays out paragraphs in managed code: lines are filled greedily, pair kerning comes from the font's GPOS or kern table (AVATAR, To, Wa), and spaces at the end of a line hang outside the margin. Blåbær­syltetøy breaks at its soft hyphen when it has to.";
    var document = new PdfDocument("Zorg.Pdf render smoke test")
    {
        Language = "en-GB",
        Content =
        [
            new Heading("Zorg.Pdf render smoke test"),
            new Paragraph(Body),
            new Heading("Alignment") { Level = 2 },
            new Paragraph("Justified: " + Body + " " + Body) { Align = TextAlign.Justify },
            new Paragraph(["Centred text, for headings and covers.", LineBreak.Value, "A line break inside the paragraph."]) { Align = TextAlign.Center },
            new Paragraph("Right-aligned, like amounts: 1 234,50 kr") { Align = TextAlign.Right },
            new Heading("Languages") { Level = 2 },
            new Paragraph("Blåbærsyltetøy, 12 × 4,50 € = 54,00 €. Ελληνικά · Кириллица · Čeština · Polski: żółć."),
            new PageBreak(),
            new Heading("A second page") { Level = 2 },
            new Paragraph("A word too wide for the page: " + new string('W', 80)),
        ],
    };
    var options = new RenderOptions
    {
        Fonts = FontCollection.Empty.Add(new FontFamily("Body") { Regular = FontSource.FromBytes(font) }),
        Conformance = PdfConformance.PdfUA1,
    };

    using var asynchronous = new MemoryStream();
    RenderResult result = document.RenderAsync(asynchronous, options).AsTask().GetAwaiter().GetResult();
    using var synchronous = new MemoryStream();
    document.Render(synchronous, options);
    string text = Encoding.Latin1.GetString(asynchronous.ToArray());
    Check("render", result.PageCount == 2 && text.Contains("/S /H1", StringComparison.Ordinal) && text.Contains("<pdfuaid:part>1</pdfuaid:part>", StringComparison.Ordinal));
    Check("render diagnostics", result.Diagnostics is [{ Code: "ZPDF-T001", Path: "/content/10/inlines/0" }]);
    Check("render sync equals async", asynchronous.ToArray().AsSpan().SequenceEqual(synchronous.ToArray()));

    try
    {
        new PdfDocument("x") { Content = [new Paragraph("bell\u0007")] }.Render(new MemoryStream(), options);
        Check("render refuses before writing", false);
    }
    catch (PdfDocumentException ex)
    {
        Check("render refuses before writing", ex.Diagnostics is [{ Code: "ZPDF-T002" }]);
    }

    if (saveTo is not null)
    {
        File.WriteAllBytes(Path.Combine(saveTo, "document.pdf"), asynchronous.ToArray());
    }
}

// Spike S5 and S6 (tables move to the public engine in slice 4): a tagged invoice, rows arriving asynchronously.
{
    using var invoice = new MemoryStream();
    FlowResult flow = FlowDocument.RenderAsync(invoice, font, SpikeInvoice.Options, SpikeInvoice.Blocks(90)).AsTask().GetAwaiter().GetResult();
    string invoiceText = Encoding.Latin1.GetString(invoice.ToArray());
    Check("tagged invoice", flow.Pages >= 2 && invoiceText.Contains("/StructTreeRoot", StringComparison.Ordinal) && invoiceText.Contains("/S /TH", StringComparison.Ordinal));
    if (saveTo is not null)
    {
        File.WriteAllBytes(Path.Combine(saveTo, "invoice.pdf"), invoice.ToArray());
    }
}

return failures == 0 ? 0 : 1;
