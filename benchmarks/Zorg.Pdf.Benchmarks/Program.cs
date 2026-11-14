using System;
using System.IO;
using BenchmarkDotNet.Running;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using Zorg.Pdf.Benchmarks;

if (args.Length > 0 && args[0] == "--write")
{
    // Writes both invoices next to each other, to compare them by eye and by size.
    string folder = args.Length > 1 ? args[1] : ".";
    GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    foreach (int rows in new[] { 90, 1000 })
    {
        string zorgPath = Path.Combine(folder, $"zorg-{rows}.pdf");
        using (FileStream zorg = File.Create(zorgPath))
        {
            await BenchInvoice.Document.RenderAsync(zorg, InvoiceBenchmarks.Options(rows));
        }

        var renderer = new PdfDocumentRenderer { Document = InvoiceBenchmarks.BuildMigraDoc(rows) };
        renderer.RenderDocument();
        int pages = renderer.PdfDocument.PageCount; // Before saving: a saved PdfDocument can't be read any more.
        string migraPath = Path.Combine(folder, $"migradoc-{rows}.pdf");
        await renderer.PdfDocument.SaveAsync(migraPath);
        Console.WriteLine($"{rows} rows: Zorg.Pdf {new FileInfo(zorgPath).Length:N0} bytes, MigraDoc {new FileInfo(migraPath).Length:N0} bytes, {pages} MigraDoc pages");
    }

    return;
}

BenchmarkSwitcher.FromAssembly(typeof(InvoiceBenchmarks).Assembly).Run(args);
