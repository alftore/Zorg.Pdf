using System;
using System.IO;
using System.Linq;
using System.Text;
using Zorg.Pdf;
using Zorg.Pdf.Fonts;

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
            new Paragraph(["Runs: plain, ", new TextRun("bold") { Style = TextStyle.Bold }, ", ", new TextRun("italic") { Style = TextStyle.Italic }, ", ", new TextRun("bold italic") { Style = new TextStyle { Weight = FontWeight.Bold, FontStyle = FontStyle.Italic } }, ", ", new TextRun("large red") { Style = new TextStyle { Size = Length.Pt(16), Color = Color.Parse("#C00000") } }, " and plain again."]),
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
    string text = Readable(asynchronous.ToArray());
    Check("render", result.PageCount == 2 && text.Contains("/S /H1", StringComparison.Ordinal) && text.Contains("<pdfuaid:part>1</pdfuaid:part>", StringComparison.Ordinal));
    // Warnings only: the font has one face, so bold and italic are synthesised and reported as Info (ZPDF-F003).
    PdfDiagnostic[] warnings = [.. result.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info)];
    Check("render diagnostics", warnings is [{ Code: "ZPDF-T001", Path: "/content/11/inlines/0" }] && result.Diagnostics.Any(d => d.Code == "ZPDF-F003"));
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

// 0.1 slices 4 and 5: a tagged invoice with a table whose rows arrive asynchronously, over several pages, with a
// header and a "Page X of Y" footer.
{
    var invoiceDocument = new PdfDocument("Invoice 2026-0042")
    {
        Header = new PageBand([new Paragraph("Blåbær & Syltetøy AB · Invoice 2026-0042")]),
        Footer = new PageBand([new Paragraph(["Page ", Field.PageNumber, " of ", Field.PageCount]) { Align = TextAlign.Right }]),
        Content =
        [
            new Heading("Invoice 2026-0042"),
            new Paragraph("Bill to: Blåbær & Syltetøy AB, Kungsgatan 12, 111 43 Stockholm") { Align = TextAlign.Right },
            new Table(
            [
                new Column("#") { Width = ColumnWidth.Star(0.6), Align = TextAlign.Right },
                new Column("Description") { Width = ColumnWidth.Star(5) },
                new Column("Hours") { Width = ColumnWidth.Star(1.1), Align = TextAlign.Right },
                new Column("Amount") { Width = ColumnWidth.Fixed(Length.Cm(2.5)), Align = TextAlign.Right },
            ])
            {
                Rows = TableRows.Bound("lines"),
            },
            new Paragraph([new TextRun("Total: 54 375,00 NOK") { Style = TextStyle.Bold }]) { Align = TextAlign.Right },
        ],
    };
    var invoiceOptions = new RenderOptions
    {
        Fonts = FontCollection.Empty.Add(new FontFamily("Body") { Regular = FontSource.FromBytes(font) }),
        Conformance = PdfConformance.PdfUA1,
        Data = PdfData.Empty.WithRows("lines", InvoiceLines(90), i =>
        [
            i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            i % 3 == 0 ? "Blåbærsyltetøy-integrasjon (Norwegian characters æ ø å)" : "Architecture review of the reporting pipeline, with written recommendations and a follow-up workshop",
            (0.5 + (i * 37 % 16 / 2.0)).ToString("N1", System.Globalization.CultureInfo.InvariantCulture),
            ((0.5 + (i * 37 % 16 / 2.0)) * 1250).ToString("N2", System.Globalization.CultureInfo.InvariantCulture),
        ]),
    };
    using var invoice = new MemoryStream();
    RenderResult flow = invoiceDocument.RenderAsync(invoice, invoiceOptions).AsTask().GetAwaiter().GetResult();
    string invoiceText = Readable(invoice.ToArray());
    Check("tagged invoice", flow.PageCount >= 2 && invoiceText.Contains("/StructTreeRoot", StringComparison.Ordinal) && invoiceText.Contains("/S /TH", StringComparison.Ordinal) && !flow.HasWarnings);
    Check("header and footer", invoiceText.Contains("/Contents [", StringComparison.Ordinal));
    if (saveTo is not null)
    {
        File.WriteAllBytes(Path.Combine(saveTo, "invoice.pdf"), invoice.ToArray());
    }
}

// 0.1 slice 6: images. A PNG with transparency (decoded, with a soft mask) as a figure, an opaque PNG (passed
// through) as decoration, and the same logo from a data slot in the header.
{
    byte[] logo = Png(96, 48, (x, y) => [(byte)(x * 2), (byte)(y * 5), 160, (byte)(x < 48 ? 255 : 96)], alpha: true);
    byte[] stripe = Png(200, 8, (x, y) => [(byte)x, 90, (byte)(255 - x)], alpha: false);
    var imageDocument = new PdfDocument("Images")
    {
        Header = new PageBand([new Image(ImageSource.FromSlot("logo")) { Alt = "Zorg logo", Height = Length.Cm(1) }]),
        Content =
        [
            new Heading("Images"),
            new Image(ImageSource.FromSlot("logo")) { Alt = "The Zorg logo, larger", Width = Length.Cm(8), Align = TextAlign.Center },
            new Image(ImageSource.FromBytes(stripe)) { Decorative = true, Width = Length.Cm(17) },
            new Paragraph("Text after the images."),
        ],
    };
    var imageOptions = new RenderOptions
    {
        Fonts = FontCollection.Empty.Add(new FontFamily("Body") { Regular = FontSource.FromBytes(font) }),
        Conformance = PdfConformance.PdfUA1,
        Data = PdfData.Empty.WithImage("logo", logo),
    };
    using var images = new MemoryStream();
    RenderResult drawn = imageDocument.RenderAsync(images, imageOptions).AsTask().GetAwaiter().GetResult();
    string imageText = Readable(images.ToArray());
    Check("images", !drawn.HasWarnings && imageText.Contains("/S /Figure", StringComparison.Ordinal) && imageText.Contains("/SMask", StringComparison.Ordinal) && imageText.Contains("/Predictor 15", StringComparison.Ordinal));
    if (saveTo is not null)
    {
        File.WriteAllBytes(Path.Combine(saveTo, "images.pdf"), images.ToArray());
    }
}

// 0.1 slice 9: the agent tools, called as a chat client calls them, with JSON arguments.
{
    var tools = Zorg.Pdf.Extensions.AI.PdfTools.Create(new Zorg.Pdf.Extensions.AI.PdfToolOptions
    {
        Render = new RenderOptions { Fonts = FontCollection.Empty.Add(new FontFamily("Body") { Regular = FontSource.FromBytes(font) }) },
    });
    using var draft = System.Text.Json.JsonDocument.Parse("""{ "title": "Agent draft", "content": [{ "type": "heading", "inlines": ["Hello"] }, { "type": "paragraph", "inlines": ["From a tool call."] }] }""");
    var arguments = new Microsoft.Extensions.AI.AIFunctionArguments { ["document"] = draft.RootElement };
    object? schema = tools[0].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments()).AsTask().GetAwaiter().GetResult();
    object? validated = tools[1].InvokeAsync(arguments).AsTask().GetAwaiter().GetResult();
    object? rendered = tools[2].InvokeAsync(arguments).AsTask().GetAwaiter().GetResult();
    Check("agent tools", schema is System.Text.Json.JsonElement s && s.TryGetProperty("schema", out _)
        && validated is System.Text.Json.JsonElement v && v.GetProperty("valid").GetBoolean()
        && rendered is System.Text.Json.JsonElement r && r.GetProperty("rendered").GetBoolean() && r.GetProperty("pages").GetInt32() == 1);
}

// 0.1 slice 8: every example in docs/examples, with its data, under PDF/UA-1. Found by walking up from the current
// folder, so it runs from the repository (CI, the Linux gate) and is skipped elsewhere.
string? examples = null;
for (DirectoryInfo? dir = new(Environment.CurrentDirectory); dir is not null && examples is null; dir = dir.Parent)
{
    string candidate = Path.Combine(dir.FullName, "docs", "examples");
    examples = Directory.Exists(candidate) ? candidate : null;
}

if (examples is null)
{
    Console.WriteLine("skip examples: no docs/examples above the current folder");
}
else
{
    foreach (string path in Directory.GetFiles(examples, "*.json").Order(StringComparer.Ordinal))
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string dataPath = Path.Combine(examples, "data", name + ".json");
        var exampleOptions = new RenderOptions
        {
            Fonts = FontCollection.Empty.Add(new FontFamily("Body") { Regular = FontSource.FromBytes(font) }),
            Conformance = PdfConformance.PdfUA1,
            Data = File.Exists(dataPath) ? PdfData.ParseJson(File.ReadAllText(dataPath)) : PdfData.Empty,
        };
        using var rendered = new MemoryStream();
        RenderResult example = PdfDocument.ParseJson(File.ReadAllText(path)).RenderAsync(rendered, exampleOptions).AsTask().GetAwaiter().GetResult();
        Check($"example {name}", !example.HasWarnings);
        if (saveTo is not null)
        {
            File.WriteAllBytes(Path.Combine(saveTo, $"example-{name}.pdf"), rendered.ToArray());
        }
    }
}

return failures == 0 ? 0 : 1;

// The file as text with every Flate stream inflated after it: objects sit in compressed object streams.
static string Readable(byte[] pdf)
{
    string raw = Encoding.Latin1.GetString(pdf);
    var text = new StringBuilder(raw);
    for (int at = raw.IndexOf("/FlateDecode", StringComparison.Ordinal); at >= 0; at = raw.IndexOf("/FlateDecode", at + 1, StringComparison.Ordinal))
    {
        const string Stream = "\nstream\n";
        int start = raw.IndexOf(Stream, at, StringComparison.Ordinal) + Stream.Length;
        try
        {
            using var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(pdf, start, pdf.Length - start), System.IO.Compression.CompressionMode.Decompress);
            using var inflated = new MemoryStream();
            zlib.CopyTo(inflated);
            text.Append('\n').Append(Encoding.Latin1.GetString(inflated.ToArray()));
        }
        catch (InvalidDataException)
        {
            // Not Flate data after all.
        }
    }

    return text.ToString();
}

// A PNG made in code, 8-bit RGB or RGBA, every row unfiltered.
static byte[] Png(int width, int height, Func<int, int, byte[]> pixel, bool alpha)
{
    int channels = alpha ? 4 : 3;
    var raw = new MemoryStream();
    for (int y = 0; y < height; y++)
    {
        raw.WriteByte(0);
        for (int x = 0; x < width; x++)
        {
            raw.Write(pixel(x, y), 0, channels);
        }
    }

    var compressed = new MemoryStream();
    using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
    {
        raw.Position = 0;
        raw.CopyTo(zlib);
    }

    var png = new MemoryStream();
    png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
    byte[] header = new byte[13];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
    (header[8], header[9]) = (8, (byte)(alpha ? 6 : 2));
    Chunk(png, "IHDR"u8, header);
    Chunk(png, "IDAT"u8, compressed.ToArray());
    Chunk(png, "IEND"u8, []);
    return png.ToArray();

    static void Chunk(MemoryStream png, ReadOnlySpan<byte> type, byte[] body)
    {
        byte[] length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        png.Write(length);
        byte[] typed = [.. type, .. body];
        png.Write(typed);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in typed)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
        png.Write(length);
    }
}

static async System.Collections.Generic.IAsyncEnumerable<int> InvoiceLines(int count)
{
    for (int i = 1; i <= count; i++)
    {
        if (i % 25 == 0)
        {
            await System.Threading.Tasks.Task.Yield();
        }

        yield return i;
    }
}
