using System;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Hyphenation;
using Zorg.Pdf.Imaging;
using Zorg.Pdf.Invoicing;
using Zorg.Pdf.Markdown;

// The snippets show values being made; the guide's reader uses them, not this file.
#pragma warning disable IDE0059 // Unnecessary assignment of a value

namespace Zorg.Pdf.Tests.Guide;

/// <summary>
/// The C# snippets in <c>docs/guide</c>, compiled. They aren't run: they name files that don't exist here.
/// <see cref="GuideTests"/> checks that every C# block in the guide appears in a GuideSnippets file, line for line.
/// Parameters stand for what the reader already has.
/// </summary>
internal static partial class GuideSnippets
{
    // getting-started.md
    public static async Task GettingStartedAsync()
    {
        FontCollection fonts = FontCollection.FromFolder("fonts").WithDefault("Inter");

        var document = new PdfDocument("Hello")
        {
            Content = [new Heading("Hello, PDF"), new Paragraph("Tagged, accessible and the same on every machine.")],
        };
        await using FileStream output = File.Create("hello.pdf");
        RenderResult result = await document.RenderAsync(output, new RenderOptions { Fonts = fonts });
        Console.WriteLine($"{result.PageCount} pages, {result.BytesWritten} bytes");

        PdfDocument fromJson = PdfDocument.ParseJson(await File.ReadAllTextAsync("hello.json"));
        foreach (PdfDiagnostic problem in fromJson.Validate().Diagnostics)
        {
            Console.WriteLine($"{problem.Code} at {problem.Path}: {problem.Message}");
        }
    }

    // design.md
    public static (StyleSheet Styles, TextBox Cover, Table Table) Design()
    {
        var styles = StyleSheet.Default with
        {
            Styles = StyleSheet.Default.Styles
                .SetItem("heading1", new TextStyle { Size = Length.Pt(22), Weight = FontWeight.Bold, Color = Color.Parse("#0F2A3D") })
                .SetItem("coverTitle", new TextStyle { Size = Length.Pt(40), Weight = FontWeight.Bold, Color = Color.White }),
        };

        var cover = new TextBox([new Paragraph("Fjordlys AS") { Style = "coverTitle" }])
        {
            X = Length.Zero,
            Y = Length.Zero,
            Width = Length.Mm(210),
            Height = Length.Mm(110),
            Padding = new Thickness(Length.Mm(34), Length.Mm(20), Length.Zero, Length.Mm(20)),
            Fill = Color.Parse("#0F2A3D"),
        };

        var table = new Table([new Column("Measure"), new Column("2026") { Align = TextAlign.Right }])
        {
            Rows = TableRows.Inline([["Lamps taken back", "11 200"], ["Parts reused", "93 %"]]),
            TableStyle = new TableStyle { HeaderFill = Color.Parse("#0F2A3D"), StripeFill = Color.Parse("#F4F6F9"), CellPadding = Length.Pt(4.5) },
        };

        return (styles, cover, table);
    }

    // document-model.md
    public static void DocumentModel(FontCollection fonts)
    {
        var document = new PdfDocument("Invoice 2026-0042")
        {
            Language = "en-GB",
            Page = PageSettings.A4 with { Margin = Thickness.All(Length.Cm(2)) with { Top = Length.Cm(3) } },
            Content =
            [
                new Heading("Invoice 2026-0042") { Id = "top" },
                new Paragraph(["Due: ", new TextRun("14 December") { Style = TextStyle.Bold }]) { Align = TextAlign.Justify },
                new List(["Check the amounts", "Pay by transfer"]) { Marker = ListMarker.Number },
                new Spacer(Length.Mm(10)),
                new Heading("Payment terms") { Level = 2, KeepWithNext = true },
                new Paragraph("Payment is due within 30 days."),
                new PageBreak(),
            ],
        };
        var draft = document with { Watermark = new Watermark("DRAFT") };

        var options = new RenderOptions { Fonts = fonts, Hyphenator = PatternHyphenator.Default };
        var styles = new StyleSheet { Styles = StyleSheet.Default.Styles.SetItem("body", new TextStyle { Size = Length.Pt(10), Hyphens = Hyphens.Auto }) };

        var mixed = new Paragraph(["The German word ", new TextRun("Fernweh") { Style = new TextStyle { Language = "de" } }, " has no English equal."]);
    }

    // tables-and-data.md
    public sealed record InvoiceLine(string Description, decimal Amount);

    public static async Task TablesAndDataAsync(FontCollection fonts, Stream output, System.Collections.Generic.IAsyncEnumerable<InvoiceLine> linesAsync, CancellationToken cancellationToken)
    {
        var lines = new Table(
        [
            new Column("Description") { Width = ColumnWidth.Star(5) },
            new Column("Hours") { Width = ColumnWidth.Fixed(Length.Cm(2)), Align = TextAlign.Right },
            new Column("Amount") { Width = ColumnWidth.Fixed(Length.Cm(3)), Align = TextAlign.Right },
        ])
        {
            Rows = TableRows.Inline(
            [
                ["Consulting", "7.5", "9 375.00"],
                ["Travel", "", "1 200.00"],
                [new Cell("Total") { ColumnSpan = 2, Align = TextAlign.Right }, new Cell([new TextRun("10 575.00") { Style = TextStyle.Bold }])],
            ]),
        };

        var invoice = new PdfDocument("Invoice")
        {
            Content = [new Table([new Column("Description"), new Column("Amount") { Align = TextAlign.Right }]) { Rows = TableRows.Bound("lines") }],
        };
        var data = PdfData.Empty.WithRows("lines", linesAsync, line => [line.Description, line.Amount.ToString("N2")]);
        await invoice.RenderAsync(output, new RenderOptions { Fonts = fonts, Data = data }, cancellationToken);

        var items = new Table([new Column("Description"), new Column("Amount") { Align = TextAlign.Right }])
        {
            Rows = TableRows.Repeat("lines", [new Cell([new Bind("description")]), new Cell([new Bind("amount") { Format = "N2" }])]),
        };
    }

    // templates.md
    public static async Task TemplatesAsync(FontCollection fonts, Stream output, CancellationToken cancellationToken)
    {
        var reminder = new PdfDocument("Reminder")
        {
            Content =
            [
                new Paragraph(["Dear ", new Bind("customer.name"), ","]),
                new Conditional("overdue", [new Paragraph("Payment is overdue.")]),
                new Conditional("overdue", [new Paragraph("Thank you for paying on time.")]) { Not = true },
                new Repeat("notes", [new Paragraph([new Bind("@number"), ". ", new Bind("text")])]),
                new Paragraph(["Total: ", new Bind("total") { Format = "N2", Style = TextStyle.Bold }]),
            ],
        };
        using JsonDocument model = JsonDocument.Parse("""{ "customer": { "name": "Blåbær AB" }, "overdue": true, "total": 1250, "notes": [{ "text": "Fee added" }] }""");
        await reminder.RenderAsync(output, new RenderOptions { Fonts = fonts, Data = PdfData.Empty.WithModel(model.RootElement) }, cancellationToken);
    }

    // sections.md
    public static void Sections(PdfDocument document)
    {
        var report = document with
        {
            Header = new PageBand([new Paragraph("Fjordlys AS · Annual report 2026") { Align = TextAlign.Right, Style = "header" }]),
            Footer = new PageBand([new Paragraph(["Page ", Field.PageNumber, " of ", Field.PageCount]) { Align = TextAlign.Right, Style = "footer" }]),
        };

        var thesis = new PdfDocument("Thesis")
        {
            Numbering = PageNumberStyle.LowerRoman,
            Content =
            [
                new Heading("A study of light"),
                new SectionBreak { Footer = new PageBand([new Paragraph([Field.PageNumber]) { Align = TextAlign.Center }]) },
                new TableOfContents { Title = "Contents" },
                new SectionBreak { Numbering = PageNumberStyle.Number, StartAt = 1 },
                new Heading("Introduction"),
                new SectionBreak { Numbering = PageNumberStyle.Number, StartAt = 1, Prefix = "A-", Header = PageBand.None },
                new Heading("Appendix"),
            ],
        };
    }

    // links-and-contents.md
    public static void LinksAndContents(PdfDocument document)
    {
        var appendix = new Heading("Appendix B") { Id = "appendix-b" };
        var see = new Paragraph(["See ", new Link(["Appendix B"]) { Target = "#appendix-b" }, " or ", new Link(["our terms"]) { Target = "https://example.com/terms" }, "."]);
        var mail = new Paragraph(["Questions: ", new Link(["hr@example.com"]) { Target = "mailto:hr@example.com", Description = "Email HR" }]);

        var report = document with { OutlineLevels = 2 };

        var annual = new PdfDocument("Annual report 2026")
        {
            Content = [new Heading("Annual report 2026"), new TableOfContents { Title = "Contents", MaxLevel = 2 }, new PageBreak(), new Heading("Introduction")],
        };
    }

    // charts.md
    public static Chart Charts()
    {
        var sales = new Chart(ChartKind.Bar, ["Q1", "Q2", "Q3", "Q4"], [new ChartSeries("2025", [12, 15, 14, 18]), new ChartSeries("2026", [14, 17, 19, 22])])
        {
            Title = "Sales, MNOK",
            ValueFormat = "N0",
            Height = Length.Cm(6),
            Alt = "Sales rose every quarter in 2026, from 14 to 22 million kroner, above 2025's.",
        };
        return sales;
    }

    // barcodes.md
    public static void Barcodes()
    {
        var parcel = new Barcode(BarcodeKind.Gs1_128, "(00)370123456789012342") { Height = Length.Cm(2), Alt = "Parcel label code" };
        var product = new Barcode(BarcodeKind.Ean13, "590123412345") { Alt = "Product number 5901234123457" };
        var link = new Barcode(BarcodeKind.Qr, "https://example.com/report") { Size = Length.Cm(2.5), Alt = "QR code linking to the online report" };

        EpcPayment epc = EpcPayment.Create("ACME GmbH", "DE02120300000000202051", 1190.00m, "Invoice 2026-0042");
        var qr = new Barcode(BarcodeKind.Qr, epc.ToString()) { Size = Length.Cm(3), Alt = "Payment QR code" };

        string kid = Kid.Create("2026004217", KidMethod.Mod10);   // "20260042177"
        bool ok = Kid.IsValid(kid, KidMethod.Mod10);
    }

    // letters.md
    public static async Task LettersAsync(PdfDocument document, FontCollection fonts, CancellationToken cancellationToken)
    {
        var address = new TextBox([new Paragraph([new Bind("customer.name")]), new Paragraph([new Bind("customer.street")]), new Paragraph([new Bind("customer.city")])])
        {
            Preset = TextBoxPreset.NorwegianWindow,
        };

        var letter = document with { Background = new PageBackground(ImageSource.FromSlot("letterhead")) { OtherPages = 2 } };
        var options = new RenderOptions { Fonts = fonts, Data = PdfData.Empty.WithPdf("letterhead", await File.ReadAllBytesAsync("letterhead.pdf", cancellationToken)) };
    }

    // images.md
    public static async Task ImagesAsync(PdfDocument document, FontCollection fonts, CancellationToken cancellationToken)
    {
        var logo = new Image(ImageSource.FromSlot("logo")) { Alt = "Zorg Consulting", Width = Length.Cm(4) };
        var data = PdfData.Empty.WithImage("logo", await File.ReadAllBytesAsync("logo.png", cancellationToken));

        var options = new RenderOptions { Fonts = fonts, ImageProcessor = new ImagingProcessor { Resolution = 200 } };

        using var pdf = new MemoryStream();
        await document.RenderAsync(pdf, options, cancellationToken);
        byte[] png = await PdfPreview.RenderPagePngAsync(pdf.ToArray(), page: 0, new PreviewOptions { Dpi = 96 }, cancellationToken);
    }

    // accessibility.md
    public static void Accessibility(PdfDocument document, FontCollection fonts, byte[] xmlBytes)
    {
        var options = new RenderOptions { Fonts = fonts, Conformance = PdfConformance.PdfUA1, Archive = PdfArchive.PdfA2a };

        var invoice = document with { Attachments = [new Attachment("factur-x.xml", ImageSource.FromSlot("xml")) { MimeType = "text/xml", Relationship = AttachmentRelationship.Data }] };
        var archived = new RenderOptions { Fonts = fonts, Archive = PdfArchive.PdfA3b, Data = PdfData.Empty.WithFile("xml", xmlBytes) };

        var archive = new XmpSchema("ACME archive", "https://acme.example/ns/archive/1.0/", "acme")
        {
            Properties = [new XmpProperty("CaseNumber", "2026-0042") { Description = "The case the document belongs to" }],
        };
        var withCase = new RenderOptions { Fonts = fonts, Archive = PdfArchive.PdfA3b, MetadataSchemas = [archive] };
    }

    // existing-pdfs.md
    public static async Task ExistingPdfsAsync(ReadOnlyMemory<byte> cover, ReadOnlyMemory<byte> contract, CancellationToken cancellationToken)
    {
        PdfFileInfo info = await PdfFileInfo.ReadAsync(File.OpenRead("contract.pdf"), cancellationToken: cancellationToken);
        Console.WriteLine($"{info.Title}: {info.PageCount} pages, the first {info.Pages[0].Width:0} × {info.Pages[0].Height:0} pt");

        await using FileStream pack = File.Create("pack.pdf");
        PdfFileResult merged = await PdfFiles.MergeAsync([new PdfPageSource(cover), new PdfPageSource(contract) { Pages = "2-" }], pack,
            new PdfFileOptions { Title = "Signing pack" }, cancellationToken);

        await using FileStream summary = File.Create("summary.pdf");
        await PdfFiles.ExtractAsync(contract, "1-3", summary, new PdfFileOptions { Title = "Contract summary", Language = "en" }, cancellationToken);
    }

    // invoices.md
    public static async Task InvoicesAsync(PdfDocument invoice, Stream output, FontCollection fonts, CancellationToken cancellationToken)
    {
        FacturXInvoice facturX = FacturX.Read(await File.ReadAllBytesAsync("invoice.xml", cancellationToken));
        await facturX.ApplyTo(invoice).RenderAsync(output, facturX.ApplyTo(new RenderOptions { Fonts = fonts, Conformance = PdfConformance.PdfUA1 }), cancellationToken);
    }

    // markdown.md
    public static async Task MarkdownAsync(Stream output, FontCollection fonts, CancellationToken cancellationToken)
    {
        MarkdownResult result = MarkdownDocument.Parse("# Minutes\n\nPresent: **Kari**, Ola.\n\n- Budget\n- Hiring", new MarkdownOptions { Title = "Minutes 14 November", Language = "nb" });
        await result.Document.RenderAsync(output, new RenderOptions { Fonts = fonts }, cancellationToken);
    }

    // agents.md, without the tools (the tools are in GuideSnippets.Agents.cs, apart from Zorg.Pdf.List)
    public static ImmutableArray<PdfDiagnostic> AgentsWithoutTools(string answer)
    {
        string schema = PdfJsonSchema.Current;
        DocumentParseResult parsed = PdfDocument.TryParseJson(answer);
        if (!parsed.IsSuccess)
        {
            return parsed.Diagnostics;
        }

        return [];
    }
}
