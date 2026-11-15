using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Barcodes;
using Zorg.Pdf.Layout;
using Zorg.Pdf.Validation;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// The Swiss QR-bill's payment part and receipt, laid out as SIX's guidelines (v2.4, 3.3 to 3.6) and style guide say:
/// a 210 × 105 mm strip at the page's bottom edge, the receipt 62 mm wide on the left, 5 mm margins, the 46 mm code
/// with its 7 mm Swiss cross, headings bold and 2 pt smaller than their values, corner marks for blank fields, and lines
/// with "Separate before paying in" above them, as a PDF printed on plain paper needs. Text is tagged as paragraphs,
/// the code as a figure, the lines and marks as artifacts.
/// </summary>
internal sealed partial class RenderEngine
{
    private const double Mm = 72 / 25.4;
    private const double StripHeight = 105 * Mm;
    private const double ReceiptWidth = 62 * Mm;

    /// <summary>Pages with a QR-bill: their footer is left out, as the strip takes the page's bottom.</summary>
    private readonly HashSet<int> _slipPages = [];

    /// <summary>The fonts the guidelines allow (3.4), by the start of their PostScript names.</summary>
    private static readonly string[] SlipFonts = ["Arial", "Helvetica", "Frutiger", "LiberationSans"];

    private static readonly Dictionary<string, string[]> SlipTexts = new(StringComparer.Ordinal)
    {
        // Payment part, Receipt, Account / Payable to, Reference, Additional information, Payable by, Payable by (name/address), Currency, Amount, Acceptance point, Separate before paying in.
        ["en"] = ["Payment part", "Receipt", "Account / Payable to", "Reference", "Additional information", "Payable by", "Payable by (name/address)", "Currency", "Amount", "Acceptance point", "Separate before paying in"],
        ["de"] = ["Zahlteil", "Empfangsschein", "Konto / Zahlbar an", "Referenz", "Zusätzliche Informationen", "Zahlbar durch", "Zahlbar durch (Name/Adresse)", "Währung", "Betrag", "Annahmestelle", "Vor der Einzahlung abzutrennen"],
        ["fr"] = ["Section paiement", "Récépissé", "Compte / Payable à", "Référence", "Informations supplémentaires", "Payable par", "Payable par (nom/adresse)", "Monnaie", "Montant", "Point de dépôt", "A détacher avant le versement"],
        ["it"] = ["Sezione pagamento", "Ricevuta", "Conto / Pagabile a", "Riferimento", "Informazioni supplementari", "Pagabile da", "Pagabile da (nome/indirizzo)", "Valuta", "Importo", "Punto di accettazione", "Da staccare prima del versamento"],
        ["rm"] = ["Part da pajament", "Quittanza", "Conto / Da pajar a", "Referenza", "Infurmaziuns supplementaras", "Da pajar da", "Da pajar da (num/adressa)", "Valuta", "Import", "Post da recepziun", "Da distatgar avant che pajar"],
    };

    private async ValueTask PlaceSwissQrBillAsync(int index, SwissQrBill bill, CancellationToken cancellationToken)
    {
        string payload;
        try
        {
            payload = bill.ToPayload();
        }
        catch (ArgumentException)
        {
            return; // Reported before the render started.
        }

        // The strip and the note above it need the page's bottom 110 mm free.
        if (!_pageEmpty && _y < StripHeight + (5 * Mm))
        {
            await NewPageAsync(cancellationToken).ConfigureAwait(false);
        }

        _slipPages.Add(_page);
        string own = _document.Language.Split('-')[0];
        string language = bill.Language ?? SlipTexts.Keys.FirstOrDefault(k => string.Equals(k, own, StringComparison.OrdinalIgnoreCase)) ?? "en";
        string[] t = SlipTexts[language];
        TextStyle style = _styles.For(bill).Merge(new TextStyle { Color = Color.Black, Weight = FontWeight.Normal, FontStyle = FontStyle.Normal });

        if (Preflight.Face(style, _styles, _defaultFamily) is { } face && _fonts.TryGet(face, out FontChain chain)
            && !SlipFonts.Any(f => chain.Primary.Font.PostScriptName.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
        {
            _sink.Info(DiagnosticCodes.QrBillFont, $"The QR-bill is set in {chain.Primary.Font.PostScriptName}. SIX's guidelines allow only Arial, Frutiger, Helvetica or Liberation Sans: give the bill a style with one of them.", string.Create(CultureInfo.InvariantCulture, $"/content/{index}"));
        }

        Tagger.Element slip = _tagger.Begin(StructureRole.Div);
        SlipSeparators(t[10], style, slip);

        // The receipt: headings 6 pt bold, values 8 pt, 9 pt apart.
        SlipLines([(t[1], 11, true)], style, 5, 5, 52, 11, slip);
        var account = new List<(string, double, bool)> { (t[2], 6, true), (Iban(bill.Account), 8, false) };
        account.AddRange(AddressLines(bill.Creditor, 8));
        double baseline = SlipBlocks(style, 5, 12, 52, 9, slip, account, Reference(bill) is { } reference ? [(t[3], 6, true), (reference, 8, false)] : null, bill.Debtor is { } debtor ? [(t[5], 6, true), .. AddressLines(debtor, 8)] : [(t[6], 6, true)]);
        if (bill.Debtor is null)
        {
            CornerMarks(5, MmFromTop(baseline) + 1, 52, 20);
        }

        SlipAmount(bill, t, style, 5, 27, 6, 8, 11, 30, 10, slip);
        SlipLines([(t[9], 6, true)], style, 5, 82, 52, 8, slip, alignRight: true);

        // The payment part: title, code, amount, and the information beside them; headings 8 pt bold, values 10 pt, 11 pt apart.
        const double P = 62;
        SlipLines([(t[0], 11, true)], style, P + 5, 5, 51, 11, slip);
        SlipCode(payload, P + 5, 17, slip);
        SlipAmount(bill, t, style, P + 5, P + 20, 8, 10, 13, 40, 15, slip);
        var payable = new List<(string, double, bool)> { (t[2], 8, true), (Iban(bill.Account), 10, false) };
        payable.AddRange(AddressLines(bill.Creditor, 10));
        string? information = (bill.Message, bill.BillInformation) switch
        {
            ({ Length: > 0 } message, { Length: > 0 } billing) => message + "\n" + billing,
            ({ Length: > 0 } message, _) => message,
            (_, { Length: > 0 } billing) => billing,
            _ => null,
        };
        baseline = SlipBlocks(
            style, P + 56, 5, 87, 11, slip,
            payable,
            Reference(bill) is { } paymentReference ? [(t[3], 8, true), (paymentReference, 10, false)] : null,
            information is null ? null : [(t[4], 8, true), (information, 10, false)],
            bill.Debtor is { } paymentDebtor ? [(t[5], 8, true), .. AddressLines(paymentDebtor, 10)] : [(t[6], 8, true)]);
        if (bill.Debtor is null)
        {
            CornerMarks(P + 56, MmFromTop(baseline) + 1, 65, 25);
        }

        _tagger.End(slip, _documentWriter.Pages);
        _y = _geometry.Bottom; // Nothing more fits on this page.
        _pageEmpty = false;
    }

    /// <summary>A page y in points from a distance in millimetres below the strip's top.</summary>
    private static double FromTop(double mm) => StripHeight - (mm * Mm);

    private static double MmFromTop(double y) => (StripHeight - y) / Mm;

    /// <summary>The lines around the receipt and payment part, and the note above them.</summary>
    private void SlipSeparators(string note, TextStyle style, Tagger.Element parent)
    {
        double pageWidth = _pageGeometry.Width;
        _content.BeginArtifact("Layout"u8, default).SaveState().SetStrokeGray(0).SetLineWidth(0.5)
            .StrokeLine(0, StripHeight, pageWidth, StripHeight)
            .StrokeLine(ReceiptWidth, 0, ReceiptWidth, StripHeight)
            .RestoreState().EndMarkedContent();
        _stroke = null;
        TextStyle small = style.Merge(new TextStyle { Size = Length.Pt(7) });
        if (LayoutText(new Paragraph(note), small, "/", null, report: true, width: pageWidth).Lines is [{ } line, ..])
        {
            Tagger.Element p = _tagger.Begin(StructureRole.P, parent);
            _content.BeginMarkedContent(Tagger.Name(StructureRole.P), _tagger.Mark(p, _page));
            BeginText();
            DrawRuns(line, (pageWidth - line.Width) / 2, StripHeight + (1.5 * Mm));
            EndText();
            _tagger.End(p, _documentWriter.Pages);
        }
    }

    /// <summary>
    /// Draws the information blocks one under another, a blank line between them; each block is a heading and its
    /// value lines. Returns the last baseline, in page points.
    /// </summary>
    private double SlipBlocks(TextStyle style, double left, double top, double width, double spacing, Tagger.Element parent, params List<(string Text, double Size, bool Bold)>?[] blocks)
    {
        double baseline = 0;
        foreach (List<(string Text, double Size, bool Bold)>? block in blocks)
        {
            if (block is null)
            {
                continue;
            }

            baseline = SlipLines(block, style, left, top, width, spacing, parent);
            top = MmFromTop(baseline) + (spacing / Mm); // A blank line before the next block.
        }

        return baseline;
    }

    /// <summary>One paragraph of lines, wrapped to the width, from <paramref name="top"/> (mm below the strip's top). Returns the last baseline.</summary>
    private double SlipLines(List<(string Text, double Size, bool Bold)> lines, TextStyle style, double left, double top, double width, double spacing, Tagger.Element parent, bool alignRight = false)
    {
        Tagger.Element p = _tagger.Begin(StructureRole.P, parent);
        _content.BeginMarkedContent(Tagger.Name(StructureRole.P), _tagger.Mark(p, _page));
        BeginText();
        double baseline = double.NaN;
        foreach ((string text, double size, bool bold) in lines)
        {
            TextStyle lineStyle = style.Merge(new TextStyle { Size = Length.Pt(size), Weight = bold ? FontWeight.Bold : FontWeight.Normal });
            foreach (string part in text.Split('\n'))
            {
                foreach (LayoutLine line in LayoutText(new Paragraph(part), lineStyle, "/", null, report: true, width: width * Mm).Lines)
                {
                    baseline = double.IsNaN(baseline) ? FromTop(top) - line.Ascent : baseline - spacing;
                    double x = alignRight ? ((left + width) * Mm) - line.Width : left * Mm;
                    DrawRuns(line, x, baseline);
                }
            }
        }

        EndText();
        _tagger.End(p, _documentWriter.Pages);
        return baseline;
    }

    /// <summary>The amount section: "Currency" and "Amount" headings, their values, or corner marks for a blank amount.</summary>
    private void SlipAmount(SwissQrBill bill, string[] t, TextStyle style, double left, double amountLeft, double heading, double value, double spacing, double boxWidth, double boxHeight, Tagger.Element parent)
    {
        const double Top = 68;
        SlipLines([(t[7], heading, true), (bill.Currency, value, false)], style, left, Top, amountLeft - left - 1, spacing, parent);
        if (bill.Amount is { } amount)
        {
            var format = new NumberFormatInfo { NumberGroupSeparator = " ", NumberDecimalSeparator = "." };
            SlipLines([(t[8], heading, true), (amount.ToString("#,##0.00", format), value, false)], style, amountLeft, Top, 40, spacing, parent);
        }
        else
        {
            double after = MmFromTop(SlipLines([(t[8], heading, true)], style, amountLeft, Top, 40, spacing, parent));
            double boxLeft = left > 62 ? amountLeft + (51 - (amountLeft - left) - boxWidth) : left + 52 - boxWidth;
            CornerMarks(boxLeft, after + 1, boxWidth, boxHeight);
        }
    }

    /// <summary>The Swiss QR code, 46 mm without its quiet zone, with the Swiss cross over its centre.</summary>
    private void SlipCode(string payload, double left, double top, Tagger.Element parent)
    {
        bool[,] modules = QrEncoder.Encode(payload, QrErrorCorrection.M);
        int size = modules.GetLength(0);
        double side = 46 * Mm, module = side / size;
        double x = left * Mm, codeTop = FromTop(top);
        Tagger.Element figure = _tagger.Begin(StructureRole.Figure, parent);
        figure.Alt = "Swiss QR code for the payment";
        figure.BBox = (x, codeTop - side, x + side, codeTop);
        _content.BeginMarkedContent(Tagger.Name(StructureRole.Figure), _tagger.Mark(figure, _page));
        _content.SaveState().SetFillGray(0);
        DrawQr(modules, x - (4 * module), codeTop + (4 * module), module * (size + 8));

        // The cross: a white 7 mm square, a black one inside it, and a white cross in the Swiss flag's proportions.
        double cx = x + (side / 2), cy = codeTop - (side / 2);
        double outer = 7 * Mm, black = outer - (2 * 0.4 * Mm), arm = black * 6 / 32, span = black * 20 / 32;
        _content.SetFillGray(1).FillRectangle(cx - (outer / 2), cy - (outer / 2), outer, outer)
            .SetFillGray(0).FillRectangle(cx - (black / 2), cy - (black / 2), black, black)
            .SetFillGray(1).FillRectangle(cx - (span / 2), cy - (arm / 2), span, arm)
            .FillRectangle(cx - (arm / 2), cy - (span / 2), arm, span);
        _content.RestoreState().EndMarkedContent();
        _fill = Color.Black;
        _tagger.End(figure, _documentWriter.Pages);
    }

    /// <summary>Corner marks for a blank field: 3 mm at each corner, 0.75 pt (style guide, p. 7).</summary>
    private void CornerMarks(double left, double top, double width, double height)
    {
        double x0 = left * Mm, x1 = (left + width) * Mm, y0 = FromTop(top), y1 = FromTop(top + height), c = 3 * Mm;
        _content.BeginArtifact("Layout"u8, default).SaveState().SetStrokeGray(0).SetLineWidth(0.75)
            .MoveTo(x0, y0 - c).LineTo(x0, y0).LineTo(x0 + c, y0)
            .MoveTo(x1 - c, y0).LineTo(x1, y0).LineTo(x1, y0 - c)
            .MoveTo(x0, y1 + c).LineTo(x0, y1).LineTo(x0 + c, y1)
            .MoveTo(x1 - c, y1).LineTo(x1, y1).LineTo(x1, y1 + c)
            .Stroke().RestoreState().EndMarkedContent();
        _stroke = null;
    }

    private static string Iban(string account)
    {
        string compact = SwissQrRules.Compact(account).ToUpperInvariant();
        return string.Join(' ', Chunks(compact, 4, fromLeft: true));
    }

    /// <summary>A QR reference in a group of 2 and groups of 5, a creditor reference in groups of 4; null for none.</summary>
    private static string? Reference(SwissQrBill bill)
    {
        string reference = SwissQrRules.Compact(bill.Reference ?? "").ToUpperInvariant();
        return reference.Length == 0 ? null
            : SwissQrRules.IsQrIban(SwissQrRules.Compact(bill.Account).ToUpperInvariant()) ? string.Join(' ', Chunks(reference, 5, fromLeft: false))
            : string.Join(' ', Chunks(reference, 4, fromLeft: true));
    }

    private static IEnumerable<string> Chunks(string text, int size, bool fromLeft)
    {
        int first = fromLeft ? Math.Min(size, text.Length) : (text.Length % size is 0 ? size : text.Length % size);
        yield return text[..first];
        for (int i = first; i < text.Length; i += size)
        {
            yield return text.Substring(i, Math.Min(size, text.Length - i));
        }
    }

    /// <summary>Name, street and number, postal code and town, with the country first outside Switzerland and Liechtenstein.</summary>
    private static IEnumerable<(string, double, bool)> AddressLines(SwissQrAddress address, double size)
    {
        yield return (address.Name, size, false);
        string street = string.Join(' ', new[] { address.Street, address.HouseNumber }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (street.Length > 0)
        {
            yield return (street, size, false);
        }

        string country = address.Country is "CH" or "LI" ? "" : address.Country + "-";
        yield return (string.Create(CultureInfo.InvariantCulture, $"{country}{address.PostalCode} {address.Town}"), size, false);
    }
}
