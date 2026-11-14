using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using Zorg.Pdf.Fonts;
using MigraColumn = MigraDoc.DocumentObjectModel.Tables.Column;
using MigraParagraph = MigraDoc.DocumentObjectModel.Paragraph;
using MigraParagraphAlignment = MigraDoc.DocumentObjectModel.ParagraphAlignment;
using MigraTable = MigraDoc.DocumentObjectModel.Tables.Table;

namespace Zorg.Pdf.Benchmarks;

/// <summary>
/// The same invoice rendered by Zorg.Pdf's public API and by MigraDoc 6.2.4, the last MIT release of
/// PDFsharp/MigraDoc: the same text, columns, font (Arial) and rows. Not the same work: Zorg.Pdf also tags the
/// document for PDF/UA and streams it; MigraDoc writes an untagged file. Windows only, for Arial.
/// </summary>
[MemoryDiagnoser]
public class InvoiceBenchmarks
{
    [Params(90, 1000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup() => GlobalFontSettings.UseWindowsFontsUnderWindows = true;

    [Benchmark(Baseline = true)]
    public async Task<long> ZorgPdf()
    {
        using var output = new MemoryStream();
        RenderResult result = await BenchInvoice.Document.RenderAsync(output, Options(Rows));
        return result.BytesWritten;
    }

    [Benchmark]
    public async Task<long> MigraDoc()
    {
        var renderer = new PdfDocumentRenderer { Document = BuildMigraDoc(Rows) };
        renderer.RenderDocument();
        using var output = new MemoryStream();
        await renderer.PdfDocument.SaveAsync(output, false);
        return output.Length;
    }

    /// <summary>Fonts and fresh rows: the rows are a one-shot source, read as the table is laid out.</summary>
    internal static RenderOptions Options(int rows) => new()
    {
        Fonts = FontCollection.Empty.Add(new FontFamily("Arial")
        {
            Regular = FontSource.FromFile(@"C:\Windows\Fonts\arial.ttf"),
            Bold = FontSource.FromFile(@"C:\Windows\Fonts\arialbd.ttf"),
        }),
        Data = PdfData.Empty.WithRows("lines", Enumerable.Range(1, rows), BenchInvoice.Cells),
    };

    /// <summary>The same invoice in MigraDoc's document model.</summary>
    internal static Document BuildMigraDoc(int rows)
    {
        var document = new Document();
        document.Info.Title = BenchInvoice.Document.Title;
        Style normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 10;

        Section section = document.AddSection();
        section.PageSetup = document.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2);

        MigraParagraph title = section.AddParagraph(BenchInvoice.Document.Title);
        title.Format.Font.Size = 22;
        title.Format.Font.Bold = true;
        MigraParagraph intro = section.AddParagraph(BenchInvoice.Intro);
        intro.Format.Alignment = MigraParagraphAlignment.Justify;
        intro.Format.SpaceAfter = Unit.FromPoint(12);

        MigraTable table = section.AddTable();
        table.Format.Font.Size = 9;
        double width = Unit.FromCentimeter(21 - 4).Point;
        double shares = BenchInvoice.Columns.Sum(c => c.Share);
        foreach ((_, double share, bool right) in BenchInvoice.Columns)
        {
            MigraColumn column = table.AddColumn(Unit.FromPoint(width * share / shares));
            column.Format.Alignment = right ? MigraParagraphAlignment.Right : MigraParagraphAlignment.Left;
        }

        Row headerRow = table.AddRow();
        headerRow.HeadingFormat = true;
        headerRow.Format.Font.Bold = true;
        headerRow.Shading.Color = Colors.LightGray;
        for (int i = 0; i < BenchInvoice.Columns.Length; i++)
        {
            headerRow.Cells[i].AddParagraph(BenchInvoice.Columns[i].Header);
        }

        for (int i = 1; i <= rows; i++)
        {
            Row row = table.AddRow();
            row.Borders.Bottom.Width = 0.3;
            IReadOnlyList<Cell> cells = BenchInvoice.Cells(i);
            for (int c = 0; c < cells.Count; c++)
            {
                row.Cells[c].AddParagraph(((TextRun)cells[c].Inlines[0]).Text);
            }
        }

        section.AddParagraph("Payment is due within 30 days.").Format.Alignment = MigraParagraphAlignment.Right;
        return document;
    }
}

/// <summary>The benchmark invoice, shared by both engines.</summary>
internal static class BenchInvoice
{
    public const string Intro = "Thank you for your order. This invoice covers the consulting work below. Payment is due within 30 days.";

    public static readonly (string Header, double Share, bool Right)[] Columns = [("#", 0.6, true), ("Description", 5, false), ("Hours", 1.1, true), ("Rate", 1.3, true), ("Amount", 1.6, true)];

    public static readonly PdfDocument Document = new("Invoice 2026-0042")
    {
        Content =
        [
            new Heading("Invoice 2026-0042"),
            new Paragraph(Intro) { Align = TextAlign.Justify, SpaceAfter = Length.Pt(12) },
            new Table([.. Columns.Select(c => new Zorg.Pdf.Column(c.Header) { Width = ColumnWidth.Star(c.Share), Align = c.Right ? TextAlign.Right : TextAlign.Left })])
            {
                Rows = TableRows.Bound("lines"),
            },
            new Paragraph("Payment is due within 30 days.") { Align = TextAlign.Right },
        ],
    };

    public static IReadOnlyList<Cell> Cells(int i)
    {
        double hours = 0.5 + (i * 37 % 16 / 2.0);
        double rate = i % 3 == 0 ? 1450 : 1250;
        string description = (i % 7) switch
        {
            0 => "Architecture review of the reporting pipeline, with written recommendations and a follow-up workshop",
            1 => "Development: PDF/UA tagging",
            2 => "Code review",
            3 => "Meeting with the product team about the template format",
            4 => "Blåbærsyltetøy-integrasjon (Norwegian characters æ ø å)",
            5 => "Documentation",
            _ => "Testing on Linux and Windows",
        };
        return [i.ToString(CultureInfo.InvariantCulture), description, hours.ToString("N1", CultureInfo.InvariantCulture), rate.ToString("N2", CultureInfo.InvariantCulture), (hours * rate).ToString("N2", CultureInfo.InvariantCulture)];
    }
}
