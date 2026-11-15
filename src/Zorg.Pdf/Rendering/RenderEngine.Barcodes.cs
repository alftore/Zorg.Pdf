using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Barcodes;
using Zorg.Pdf.Layout;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// Barcodes: QR codes as runs of dark modules, linear codes as bars, all filled rectangles in black, with the quiet
/// zones the symbologies need kept white. Tagged as a Figure with its alt text, or a Layout artifact when decorative.
/// </summary>
internal sealed partial class RenderEngine
{
    private const double DefaultQrSize = 85.04;       // 3 cm.
    private const double DefaultModule = 0.9354;      // 0.33 mm, EAN-13's nominal module.
    private const double DefaultBarHeight = 42.52;    // 15 mm.
    private const double BarcodeSpaceAfter = 6;

    /// <summary>The module grid or bars for a barcode, or an exception naming what's wrong with its data.</summary>
    internal static object EncodeBarcode(Barcode barcode)
    {
        string data = barcode.Data ?? "";
        return barcode.Kind switch
        {
            BarcodeKind.Qr => QrEncoder.Encode(data, barcode.ErrorCorrection),
            BarcodeKind.Code128 => Code128.Encode(data),
            BarcodeKind.Gs1_128 => Code128.EncodeGs1(data),
            _ => Ean13.Encode(data),
        };
    }

    private async ValueTask PlaceBarcodeAsync(Barcode barcode, CancellationToken cancellationToken)
    {
        object code;
        try
        {
            code = EncodeBarcode(barcode);
        }
        catch (ArgumentException)
        {
            return; // Reported before the render started.
        }

        bool[,]? qr = code as bool[,];
        var linear = code as LinearCode;
        PreparedText? text = null;
        double width, height, barHeight = 0;
        if (qr is not null)
        {
            width = height = barcode.Size?.Points ?? DefaultQrSize;
        }
        else
        {
            int modules = linear!.QuietLeft + linear.Modules + linear.QuietRight;
            width = barcode.Width?.Points ?? (modules * DefaultModule);
            barHeight = barcode.Height?.Points ?? DefaultBarHeight;
            if (barcode.ShowText)
            {
                text = LayoutText(new Paragraph(linear.Text) { Style = barcode.Style, Align = TextAlign.Center }, _styles.For(barcode), "/", fields: null, report: false, width: width);
            }

            height = barHeight + (text is null ? 0 : text.Lines.Sum(l => l.Height) + 1);
        }

        width = Math.Min(width, _geometry.ContentWidth);
        if (!_pageEmpty && _y - height < _geometry.Bottom)
        {
            await NewPageAsync(cancellationToken).ConfigureAwait(false);
        }

        double x = barcode.Align switch
        {
            TextAlign.Center => _geometry.Left + ((_geometry.ContentWidth - width) / 2),
            TextAlign.Right => _geometry.Right - width,
            _ => _geometry.Left,
        };
        double top = _y, bottom = _y - height;
        Tagger.Element? figure = null;
        if (barcode.Decorative)
        {
            _content.BeginArtifact("Layout"u8, default);
        }
        else
        {
            figure = _tagger.Begin(StructureRole.Figure);
            figure.Alt = barcode.Alt;
            figure.BBox = (x, bottom, x + width, top);
            _content.BeginMarkedContent(Tagger.Name(StructureRole.Figure), _tagger.Mark(figure, _page));
        }

        _content.SaveState().SetFillGray(0);
        if (qr is not null)
        {
            DrawQr(qr, x, top, width);
        }
        else
        {
            DrawBars(linear!, x, top, width, barHeight);
        }

        _content.RestoreState();
        if (text is not null)
        {
            double baseline = top - barHeight - 1;
            BeginText();
            foreach (LayoutLine line in text.Lines)
            {
                baseline -= line.Ascent;
                DrawRuns(line, x + line.Offset, baseline);
                baseline -= line.Height - line.Ascent;
            }

            EndTextObject(); // The figure's marked content stays open.
        }

        _content.EndMarkedContent();
        if (figure is not null)
        {
            _tagger.End(figure, _documentWriter.Pages);
        }

        _y = bottom - BarcodeSpaceAfter;
        _pageEmpty = false;
    }

    /// <summary>The dark modules, row by row, each run as one rectangle; four modules of quiet zone around.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1814:Prefer jagged arrays over multidimensional", Justification = "A QR symbol is a square grid of modules.")]
    private void DrawQr(bool[,] modules, double left, double top, double side)
    {
        int size = modules.GetLength(0);
        double module = side / (size + 8);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size;)
            {
                if (!modules[y, x])
                {
                    x++;
                    continue;
                }

                int run = x;
                while (run < size && modules[y, run])
                {
                    run++;
                }

                _content.Rectangle(left + ((x + 4) * module), top - ((y + 5) * module), (run - x) * module, module);
                x = run;
            }
        }

        _content.Fill();
    }

    /// <summary>The bars, from the left quiet zone, each as a rectangle.</summary>
    private void DrawBars(LinearCode code, double left, double top, double width, double height)
    {
        double module = width / (code.QuietLeft + code.Modules + code.QuietRight);
        double cursor = left + (code.QuietLeft * module);
        for (int i = 0; i < code.Widths.Length; i++)
        {
            double w = code.Widths[i] * module;
            if (i % 2 == 0)
            {
                _content.Rectangle(cursor, top - height, w, height);
            }

            cursor += w;
        }

        _content.Fill();
    }
}
