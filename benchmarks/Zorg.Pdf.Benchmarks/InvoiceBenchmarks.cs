using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using Zorg.Pdf.Layout;
using Zorg.Pdf.Spike;
using MigraParagraphAlignment = MigraDoc.DocumentObjectModel.ParagraphAlignment;

namespace Zorg.Pdf.Benchmarks;

/// <summary>
/// The spike invoice (SpikeInvoice) rendered by Zorg.Pdf and by MigraDoc 6.2.4, the last MIT release of
/// PDFsharp/MigraDoc, with the same text, columns, font (Arial) and footer. Not the same work: Zorg.Pdf also tags the
/// document for PDF/UA and streams it; MigraDoc writes an untagged file. Windows only, for Arial.
/// </summary>
[MemoryDiagnoser]
public class InvoiceBenchmarks
{
    private byte[] _font = [];

    [Params(90, 1000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _font = File.ReadAllBytes(@"C:\Windows\Fonts\arial.ttf");
        GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    [Benchmark(Baseline = true)]
    public async Task<long> ZorgPdf()
    {
        using var output = new MemoryStream();
        await FlowDocument.RenderAsync(output, _font, SpikeInvoice.Options, SpikeInvoice.Blocks(Rows));
        return output.Length;
    }

    [Benchmark]
    public async Task<long> MigraDoc()
    {
        Document document = await BuildMigraDocAsync(Rows);
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        using var output = new MemoryStream();
        await renderer.PdfDocument.SaveAsync(output, false);
        return output.Length;
    }

    /// <summary>The same invoice in MigraDoc's document model, rows read from the same async source.</summary>
    internal static async Task<Document> BuildMigraDocAsync(int rows)
    {
        var document = new Document();
        document.Info.Title = SpikeInvoice.Options.Title;
        Style normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 10;

        Section section = document.AddSection();
        section.PageSetup = document.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2);

        Paragraph footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 8;
        footer.AddText("Page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();

        foreach (FlowBlock block in SpikeInvoice.Blocks(rows))
        {
            switch (block)
            {
                case FlowParagraph p:
                    Paragraph paragraph = section.AddParagraph(p.Text);
                    paragraph.Format.Font.Size = p.Size;
                    paragraph.Format.SpaceAfter = Unit.FromPoint(p.SpaceAfter);
                    paragraph.Format.Alignment = p.Align switch
                    {
                        TextAlign.Right => MigraParagraphAlignment.Right,
                        TextAlign.Center => MigraParagraphAlignment.Center,
                        TextAlign.Justify => MigraParagraphAlignment.Justify,
                        _ => MigraParagraphAlignment.Left,
                    };
                    break;

                case FlowTable t:
                    Table table = section.AddTable();
                    table.Format.Font.Size = t.Size;
                    double total = t.Columns.Sum(c => c.Share);
                    double width = Unit.FromCentimeter(21 - 4).Point;
                    foreach (FlowColumn c in t.Columns)
                    {
                        Column column = table.AddColumn(Unit.FromPoint(width * c.Share / total));
                        column.Format.Alignment = c.Align == TextAlign.Right ? MigraParagraphAlignment.Right : MigraParagraphAlignment.Left;
                    }

                    Row header = table.AddRow();
                    header.HeadingFormat = true;
                    header.Shading.Color = Colors.LightGray;
                    for (int i = 0; i < t.Columns.Count; i++)
                    {
                        header.Cells[i].AddParagraph(t.Columns[i].Header);
                    }

                    await foreach (IReadOnlyList<string> cells in t.Rows)
                    {
                        Row row = table.AddRow();
                        row.Borders.Bottom.Width = 0.3;
                        for (int i = 0; i < cells.Count; i++)
                        {
                            row.Cells[i].AddParagraph(cells[i]);
                        }
                    }

                    break;
            }
        }

        return document;
    }
}
