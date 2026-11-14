using System;
using System.IO;
using BenchmarkDotNet.Running;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using Zorg.Pdf.Benchmarks;
using Zorg.Pdf.Spike;

if (args.Length > 0 && args[0] == "--write")
{
    // Writes both invoices next to each other, to compare them by eye and by size.
    string folder = args.Length > 1 ? args[1] : ".";
    byte[] font = await File.ReadAllBytesAsync(@"C:\Windows\Fonts\arial.ttf");
    GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    foreach (int rows in new[] { 90, 1000 })
    {
        using (FileStream zorg = File.Create(Path.Combine(folder, $"zorg-{rows}.pdf")))
        {
            await FlowDocument.RenderAsync(zorg, font, SpikeInvoice.Options, SpikeInvoice.Blocks(rows));
        }

        var renderer = new PdfDocumentRenderer { Document = await InvoiceBenchmarks.BuildMigraDocAsync(rows) };
        renderer.RenderDocument();
        int pages = renderer.PdfDocument.PageCount; // Before saving: a saved PdfDocument can't be read any more.
        await renderer.PdfDocument.SaveAsync(Path.Combine(folder, $"migradoc-{rows}.pdf"));
        Console.WriteLine($"{rows} rows: Zorg.Pdf {new FileInfo(Path.Combine(folder, $"zorg-{rows}.pdf")).Length:N0} bytes, " +
            $"MigraDoc {new FileInfo(Path.Combine(folder, $"migradoc-{rows}.pdf")).Length:N0} bytes, {pages} MigraDoc pages");
    }

    return;
}

BenchmarkSwitcher.FromAssembly(typeof(InvoiceBenchmarks).Assembly).Run(args);
