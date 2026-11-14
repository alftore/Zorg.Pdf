using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Pdf.Spike;

/// <summary>
/// The spike's reference document: an invoice with a heading, address paragraphs, a table of
/// many lines that arrive asynchronously, and a closing paragraph. Shared by tests, the AOT smoke
/// sample and the benchmark, so they all measure the same thing.
/// </summary>
internal static class SpikeInvoice
{
    public static FlowOptions Options { get; } = new("Invoice 2026-0042", "en");

    public static IEnumerable<FlowBlock> Blocks(int rows, int yieldEvery = 25)
    {
        yield return new FlowParagraph("Invoice 2026-0042", TextAlign.Left, 22, 10, "H1");
        yield return new FlowParagraph("Zorg Consulting AS\nStorgata 1, 0155 Oslo\nOrg. no. 999 999 999", TextAlign.Left, 10, 8);
        yield return new FlowParagraph("Bill to: Blåbær & Syltetøy AB, Kungsgatan 12, 111 43 Stockholm", TextAlign.Right, 10, 14);
        yield return new FlowParagraph(
            "Thank you for your order. This invoice covers the consulting work below. Payment is due within 30 days. " +
            "Questions about the invoice? Answer this e-mail or call us, quoting the invoice number.",
            TextAlign.Justify,
            10,
            12);
        yield return new FlowTable(
            [
                new FlowColumn("#", 0.6, TextAlign.Right),
                new FlowColumn("Description", 5),
                new FlowColumn("Hours", 1.1, TextAlign.Right),
                new FlowColumn("Rate", 1.3, TextAlign.Right),
                new FlowColumn("Amount", 1.6, TextAlign.Right),
            ],
            LinesAsync(rows, yieldEvery));
        yield return new FlowParagraph(
            string.Create(CultureInfo.InvariantCulture, $"Total for {rows} lines: {Total(rows):N2} NOK. VAT is included where it applies."),
            TextAlign.Right,
            11,
            6);
    }

    /// <summary>Rows as a data source would deliver them: asynchronously, in batches.</summary>
    private static async IAsyncEnumerable<IReadOnlyList<string>> LinesAsync(int rows, int yieldEvery, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 1; i <= rows; i++)
        {
            if (i % yieldEvery == 0)
            {
                await Task.Yield();
            }

            cancellationToken.ThrowIfCancellationRequested();
            double hours = Hours(i);
            double rate = Rate(i);
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
            yield return
            [
                i.ToString(CultureInfo.InvariantCulture),
                description,
                hours.ToString("N1", CultureInfo.InvariantCulture),
                rate.ToString("N2", CultureInfo.InvariantCulture),
                (hours * rate).ToString("N2", CultureInfo.InvariantCulture),
            ];
        }
    }

    private static double Hours(int i) => 0.5 + (i * 37 % 16 / 2.0);

    private static double Rate(int i) => i % 3 == 0 ? 1450 : 1250;

    private static double Total(int rows)
    {
        double total = 0;
        for (int i = 1; i <= rows; i++)
        {
            total += Hours(i) * Rate(i);
        }

        return total;
    }
}
