using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Layout;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// Bar charts: a value axis with round ticks and light grid lines, a category axis with wrapped labels, bars side by
/// side or stacked, an optional title and legend. Everything is drawn in the chart's own area, as vectors, inside one
/// Figure with the chart's alt text (or a Layout artifact when decorative).
/// </summary>
internal sealed partial class RenderEngine
{
    private const double DefaultChartHeight = 198.43; // 7 cm.
    private const double ChartSpaceAfter = 8;

    // Okabe and Ito's palette, which people with the common kinds of colour blindness can tell apart.
    private static readonly Color[] ChartPalette =
    [
        new(0x00, 0x72, 0xB2), new(0xE6, 0x9F, 0x00), new(0x00, 0x9E, 0x73), new(0xCC, 0x79, 0xA7),
        new(0x56, 0xB4, 0xE9), new(0xD5, 0x5E, 0x00), new(0xF0, 0xE4, 0x42), new(0x00, 0x00, 0x00),
    ];

    private async ValueTask PlaceChartAsync(Chart chart, CancellationToken cancellationToken)
    {
        if (chart.Categories.IsEmpty || chart.Series.IsEmpty)
        {
            return; // Reported by validation, or an empty bound chart.
        }

        double width = Math.Min(chart.Width?.Points ?? _geometry.ContentWidth, _geometry.ContentWidth);
        double height = Math.Min(chart.Height?.Points ?? DefaultChartHeight, _geometry.ContentHeight);
        if (!_pageEmpty && _y - height < _geometry.Bottom)
        {
            await NewPageAsync(cancellationToken).ConfigureAwait(false);
        }

        double left = _geometry.Left, top = _y, bottom = _y - height;
        Tagger.Element? figure = null;
        if (chart.Decorative)
        {
            _content.BeginArtifact("Layout"u8, default);
        }
        else
        {
            figure = _tagger.Begin(StructureRole.Figure);
            figure.Alt = chart.Alt;
            figure.BBox = (left, bottom, left + width, top);
            _content.BeginMarkedContent(Tagger.Name(StructureRole.Figure), _tagger.Mark(figure, _page));
        }

        DrawChart(chart, left, bottom, width, height);
        _content.EndMarkedContent();
        if (figure is not null)
        {
            _tagger.End(figure, _documentWriter.Pages);
        }

        _y = bottom - ChartSpaceAfter;
        _pageEmpty = false;
    }

    private void DrawChart(Chart chart, double left, double bottom, double width, double height)
    {
        TextStyle style = _styles.For(chart);
        int count = chart.Categories.Length;
        bool stacked = chart.Kind == ChartKind.StackedBar;
        double gap = style.Size?.Points ?? 8;

        // The value range: the data's (stacked: its sums), with 0 in it, widened to round ticks.
        (double low, double high) = ValueRange(chart);
        double step = NiceStep((high - low) / 5);
        double axisLow = chart.Minimum ?? (Math.Floor(low / step) * step);
        double axisHigh = chart.Maximum ?? (Math.Ceiling(high / step) * step);
        if (axisHigh <= axisLow)
        {
            axisHigh = axisLow + step;
        }

        var ticks = new List<double>();
        for (double v = Math.Ceiling(axisLow / step) * step; v <= axisHigh + (step / 1000); v += step)
        {
            ticks.Add(Math.Abs(v) < step / 1e6 ? 0 : v);
        }

        CultureInfo culture = Culture(_document.Language);
        string Label(double v) => v.ToString(chart.ValueFormat ?? "G", culture);

        // Title on top, legend at the bottom.
        double top = bottom + height;
        if (chart.Title is { Length: > 0 } title)
        {
            LayoutLine line = Line(title, style with { Weight = FontWeight.Bold }, width, TextAlign.Center);
            DrawLine(line, left, top - line.Ascent);
            top -= line.Height + (gap / 2);
        }

        double floor = bottom;
        bool legend = chart.Legend ?? chart.Series.Length > 1;
        if (legend)
        {
            floor += DrawLegend(chart, style, left, bottom, width) + (gap / 2);
        }

        // Axis label sizes: value labels along one side, wrapped category labels along the other.
        double valueLabelWidth = ticks.Max(t => Line(Label(t), style, 1e6, TextAlign.Left).Width);
        double lineHeight = Line("0", style, 1e6, TextAlign.Left).Height;
        if (!chart.Horizontal)
        {
            double plotLeft = left + valueLabelWidth + (gap / 2), plotRight = left + width - 2;
            double band = (plotRight - plotLeft) / count;
            List<PreparedText> labels = [.. chart.Categories.Select(c => LayoutText(new Paragraph(c ?? "") { Align = TextAlign.Center }, style, "/", fields: null, report: false, width: band - 2))];
            double labelHeight = labels.Max(l => l.Lines.Sum(x => x.Height));
            double plotBottom = floor + labelHeight + (gap / 2), plotTop = top - (lineHeight / 2);
            double Y(double v) => plotBottom + ((v - axisLow) / (axisHigh - axisLow) * (plotTop - plotBottom));
            Grid(ticks, t => (plotLeft, Y(t), plotRight, Y(t)), t => (left, Y(t) - (lineHeight / 3), valueLabelWidth, Label(t)), style);
            for (int c = 0; c < count; c++)
            {
                double x0 = plotLeft + (band * c);
                double barWidth = band * 0.7 / (stacked ? 1 : chart.Series.Length);
                double cursor = x0 + (band * 0.15);
                double stackBase = 0;
                for (int s = 0; s < chart.Series.Length; s++)
                {
                    double value = Value(chart.Series[s], c);
                    (double from, double to) = stacked ? (stackBase, stackBase + value) : (0, value);
                    stackBase = to;
                    double y0 = Y(Math.Clamp(Math.Min(from, to), axisLow, axisHigh)), y1 = Y(Math.Clamp(Math.Max(from, to), axisLow, axisHigh));
                    FillBox(SeriesColor(chart, s), cursor, y0, barWidth, y1 - y0);
                    if (!stacked)
                    {
                        cursor += barWidth;
                    }
                }

                double labelTop = plotBottom - (gap / 4);
                foreach (LayoutLine line in labels[c].Lines)
                {
                    DrawLine(line, x0 + 1, labelTop - line.Ascent);
                    labelTop -= line.Height;
                }
            }

            Axis(plotLeft, Y(Math.Clamp(0, axisLow, axisHigh)), plotRight, Y(Math.Clamp(0, axisLow, axisHigh)));
        }
        else
        {
            double categoryWidth = Math.Min(width * 0.35, chart.Categories.Max(c => Line(c ?? "", style, 1e6, TextAlign.Left).Width));
            double plotLeft = left + categoryWidth + (gap / 2), plotRight = left + width - (valueLabelWidth / 2) - 2;
            double plotBottom = floor + lineHeight + (gap / 4), plotTop = top - 2;
            double band = (plotTop - plotBottom) / count;
            double X(double v) => plotLeft + ((v - axisLow) / (axisHigh - axisLow) * (plotRight - plotLeft));
            Grid(ticks, t => (X(t), plotBottom, X(t), plotTop), t => (X(t) - (Line(Label(t), style, 1e6, TextAlign.Left).Width / 2), floor + (lineHeight / 4), 1e6, Label(t)), style);
            for (int c = 0; c < count; c++)
            {
                double yTop = plotTop - (band * c);
                double barHeight = band * 0.7 / (stacked ? 1 : chart.Series.Length);
                double cursor = yTop - (band * 0.15);
                double stackBase = 0;
                for (int s = 0; s < chart.Series.Length; s++)
                {
                    double value = Value(chart.Series[s], c);
                    (double from, double to) = stacked ? (stackBase, stackBase + value) : (0, value);
                    stackBase = to;
                    double x0 = X(Math.Clamp(Math.Min(from, to), axisLow, axisHigh)), x1 = X(Math.Clamp(Math.Max(from, to), axisLow, axisHigh));
                    FillBox(SeriesColor(chart, s), x0, cursor - barHeight, x1 - x0, barHeight);
                    if (!stacked)
                    {
                        cursor -= barHeight;
                    }
                }

                PreparedText label = LayoutText(new Paragraph(chart.Categories[c] ?? "") { Align = TextAlign.Right }, style, "/", fields: null, report: false, width: categoryWidth);
                double labelHeight = label.Lines.Sum(l => l.Height);
                double baseline = yTop - (band / 2) + (labelHeight / 2);
                foreach (LayoutLine line in label.Lines)
                {
                    DrawLine(line, left, baseline - line.Ascent);
                    baseline -= line.Height;
                }
            }

            Axis(X(Math.Clamp(0, axisLow, axisHigh)), plotBottom, X(Math.Clamp(0, axisLow, axisHigh)), plotTop);
        }
    }

    private static double Value(ChartSeries series, int category) =>
        category < series.Values.Length && double.IsFinite(series.Values[category]) ? series.Values[category] : 0;

    private static (double Low, double High) ValueRange(Chart chart)
    {
        double low = 0, high = 0;
        for (int c = 0; c < chart.Categories.Length; c++)
        {
            double sum = 0;
            foreach (ChartSeries series in chart.Series)
            {
                double v = Value(series, c);
                sum += v;
                (low, high) = chart.Kind == ChartKind.StackedBar ? (low, Math.Max(high, sum)) : (Math.Min(low, v), Math.Max(high, v));
            }
        }

        return high > low ? (low, high) : (low, low + 1);
    }

    /// <summary>A round step near <paramref name="rough"/>: 1, 2, 2.5 or 5 times a power of ten.</summary>
    internal static double NiceStep(double rough)
    {
        if (!(rough > 0) || !double.IsFinite(rough))
        {
            return 1;
        }

        double power = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double fraction = rough / power;
        return power * (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 2.5 ? 2.5 : fraction <= 5 ? 5 : 10);
    }

    private static Color SeriesColor(Chart chart, int series) => chart.Series[series].Color ?? ChartPalette[series % ChartPalette.Length];

    private void FillBox(Color color, double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0)
        {
            return;
        }

        _content.SaveState().SetFillRgb(color.R / 255.0, color.G / 255.0, color.B / 255.0).Rectangle(x, y, w, h).Fill().RestoreState();
    }

    /// <summary>Light grid lines at the ticks, and their labels.</summary>
    private void Grid(List<double> ticks, Func<double, (double X0, double Y0, double X1, double Y1)> line, Func<double, (double X, double Y, double Width, string Text)> label, TextStyle style)
    {
        _content.SaveState().SetLineWidth(0.5).SetStrokeRgb(0.85, 0.85, 0.85);
        foreach (double t in ticks)
        {
            (double x0, double y0, double x1, double y1) = line(t);
            _content.MoveTo(x0, y0).LineTo(x1, y1);
        }

        _content.Stroke().RestoreState();
        foreach (double t in ticks)
        {
            (double x, double y, double w, string text) = label(t);
            LayoutLine l = Line(text, style, w < 1e5 ? w : 1e6, w < 1e5 ? TextAlign.Right : TextAlign.Left);
            DrawLine(l, x, y);
        }
    }

    private void Axis(double x0, double y0, double x1, double y1) =>
        _content.SaveState().SetLineWidth(0.75).SetStrokeGray(0).MoveTo(x0, y0).LineTo(x1, y1).Stroke().RestoreState();

    /// <summary>Swatches and series names in a row, wrapping; returns the height used.</summary>
    private double DrawLegend(Chart chart, TextStyle style, double left, double bottom, double width)
    {
        double size = style.Size?.Points ?? 8;
        var items = chart.Series.Select((s, i) => (Color: SeriesColor(chart, i), Text: Line(s.Name ?? "", style, 1e6, TextAlign.Left))).ToList();
        var rows = new List<List<(Color Color, LayoutLine Text)>> { new() };
        double used = 0;
        foreach ((Color color, LayoutLine text) in items)
        {
            double itemWidth = size + 3 + text.Width + size;
            if (used + itemWidth > width && rows[^1].Count > 0)
            {
                rows.Add([]);
                used = 0;
            }

            rows[^1].Add((color, text));
            used += itemWidth;
        }

        double lineHeight = items[0].Text.Height;
        double y = bottom + (lineHeight * (rows.Count - 1));
        foreach (List<(Color Color, LayoutLine Text)> row in rows)
        {
            double rowWidth = row.Sum(i => size + 3 + i.Text.Width + size) - size;
            double x = left + ((width - rowWidth) / 2);
            foreach ((Color color, LayoutLine text) in row)
            {
                FillBox(color, x, y + (lineHeight * 0.2), size * 0.8, size * 0.8);
                DrawLine(text, x + size + 3 - text.Offset, y + (lineHeight * 0.2));
                x += size + 3 + text.Width + size;
            }

            y -= lineHeight;
        }

        return lineHeight * rows.Count;
    }

    /// <summary>One line of text laid out in a width, wrapped lines dropped.</summary>
    private LayoutLine Line(string text, TextStyle style, double width, TextAlign align) =>
        LayoutText(new Paragraph(text) { Align = align }, style, "/", fields: null, report: false, width: width).Lines is [{ } line, ..] ? line : new LayoutLine([], 0, 0, 0, 0);

    private void DrawLine(LayoutLine line, double x, double baseline)
    {
        if (line.Runs.Count == 0)
        {
            return;
        }

        BeginText();
        DrawRuns(line, x + line.Offset, baseline);
        EndTextObject();
    }

    private static CultureInfo Culture(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
