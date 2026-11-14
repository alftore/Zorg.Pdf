using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Zorg.Pdf;
using Zorg.Pdf.Layout;
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

// Spike S1 and S2: one page of Helvetica through the object writer, deterministic.
{
    using var first = new MemoryStream();
    using var second = new MemoryStream();
    HelloDocument.WriteAsync(first, "Hello from Native AOT").AsTask().GetAwaiter().GetResult();
    HelloDocument.WriteAsync(second, "Hello from Native AOT").AsTask().GetAwaiter().GetResult();
    string text = Encoding.Latin1.GetString(first.ToArray());
    Check(
        "hello document",
        text.StartsWith("%PDF-1.7\n", StringComparison.Ordinal) && text.EndsWith("%%EOF\n", StringComparison.Ordinal)
            && text.Contains("/FlateDecode", StringComparison.Ordinal));
    Check("hello document is deterministic", first.ToArray().AsSpan().SequenceEqual(second.ToArray()));
    if (saveTo is not null)
    {
        File.WriteAllBytes(Path.Combine(saveTo, "hello.pdf"), first.ToArray());
    }
}

// Spike S3: an embedded, subset TrueType font, with any installed font (no font file is committed).
{
    string? fontPath = Environment.GetEnvironmentVariable("ZORG_PDF_TEST_FONT");
    if (string.IsNullOrEmpty(fontPath))
    {
        fontPath = Array.Find(
            [@"C:\Windows\Fonts\arial.ttf", "/usr/share/fonts/dejavu/DejaVuSans.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/System/Library/Fonts/Supplemental/Arial.ttf"],
            File.Exists);
    }

    if (fontPath is null)
    {
        Console.WriteLine("skip embedded font: no TrueType font found");
    }
    else
    {
        string[] lines = ["Zorg.Pdf spike S3: embedded, subset TrueType", "Blåbærsyltetøy, 12 × 4,50 € = 54,00 €", "Ελληνικά · Кириллица · Čeština · Polski: żółć", $"Font: {Path.GetFileName(fontPath)}"];
        byte[] font = File.ReadAllBytes(fontPath);
        using var pdf = new MemoryStream();
        FontDocument.WriteAsync(pdf, font, lines).AsTask().GetAwaiter().GetResult();
        string text = Encoding.Latin1.GetString(pdf.ToArray());
        Check("embedded font", text.Contains("/CIDFontType2", StringComparison.Ordinal) && text.Contains("/FontFile2", StringComparison.Ordinal) && pdf.Length < font.Length / 5);
        if (saveTo is not null)
        {
            File.WriteAllBytes(Path.Combine(saveTo, "font.pdf"), pdf.ToArray());
        }

        // Spike S4: paragraphs wrapped with kerning, every alignment, soft hyphens and a word too wide for the column.
        const string Body = "Zorg.Pdf lays out paragraphs in managed code: lines are filled greedily, pair kerning comes from the font's GPOS or kern table (AVATAR, To, Wa), and spaces at the end of a line hang outside the margin. Blåbær­syltetøy breaks at its soft hyphen when it has to.";
        SpikeParagraph[] paragraphs =
        [
            new("Paragraph layout (spike S4)", TextAlign.Left, 20, 12),
            new("Left: " + Body),
            new("Justified: " + Body + " " + Body, TextAlign.Justify),
            new("Centred text, for headings and covers.\nA line feed always breaks.", TextAlign.Center),
            new("Right-aligned, like amounts: 1 234,50 kr", TextAlign.Right),
            new("A word too wide for the column: PneumonoultramicroscopicsilicovolcanoconiosisSupercalifragilisticexpialidocious, broken and reported once.", TextAlign.Left, 16),
        ];
        using var paragraphPdf = new MemoryStream();
        IReadOnlyList<TextDiagnostic> diagnostics = ParagraphDocument.WriteAsync(paragraphPdf, font, paragraphs).AsTask().GetAwaiter().GetResult();
        foreach (TextDiagnostic diagnostic in diagnostics)
        {
            Console.WriteLine($"     {diagnostic.Code}: {diagnostic.Message}");
        }

        Check("paragraphs", paragraphPdf.Length > 1000 && diagnostics.Count == 1 && diagnostics[0].Code == ParagraphLayout.WordTooWide);
        if (saveTo is not null)
        {
            File.WriteAllBytes(Path.Combine(saveTo, "paragraphs.pdf"), paragraphPdf.ToArray());
        }

        // Spike S5 and S6: a tagged invoice over several pages, rows arriving asynchronously.
        using var invoice = new MemoryStream();
        FlowResult flow = FlowDocument.RenderAsync(invoice, font, SpikeInvoice.Options, SpikeInvoice.Blocks(90)).AsTask().GetAwaiter().GetResult();
        string invoiceText = Encoding.Latin1.GetString(invoice.ToArray());
        Check("tagged invoice", flow.Pages >= 2 && invoiceText.Contains("/StructTreeRoot", StringComparison.Ordinal) && invoiceText.Contains("/S /TH", StringComparison.Ordinal));
        if (saveTo is not null)
        {
            File.WriteAllBytes(Path.Combine(saveTo, "invoice.pdf"), invoice.ToArray());
        }
    }
}

return failures == 0 ? 0 : 1;
