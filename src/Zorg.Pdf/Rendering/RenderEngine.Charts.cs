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
        if (chart.Kind == ChartKind.Pie)
        {
            DrawPie(chart, style, left, bottom, width, height);
            return;
        }

        int count = chart.Categories.Length;
        bool isLine = chart.Kind == ChartKind.Line;
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
            for (int c = 0; c < count && isLine; c++)
            {
                double labelTop = plotBottom - (gap / 4);
                foreach (LayoutLine label in labels[c].Lines)
                {
                    DrawLine(label, plotLeft + (band * c) + 1, labelTop - label.Ascent);
                    labelTop -= label.Height;
                }
            }

            if (isLine)
            {
                Axis(plotLeft, Y(Math.Clamp(0, axisLow, axisHigh)), plotRight, Y(Math.Clamp(0, axisLow, axisHigh)));
                for (int s = 0; s < chart.Series.Length; s++)
                {
                    Color color = SeriesColor(chart, s);
                    _content.SaveState().SetLineWidth(1.5).SetLineJoin(1).SetLineCap(1).SetStrokeRgb(color.R / 255.0, color.G / 255.0, color.B / 255.0);
                    for (int c = 0; c < count; c++)
                    {
                        double x = plotLeft + (band * (c + 0.5)), y = Y(Math.Clamp(Value(chart.Series[s], c), axisLow, axisHigh));
                        _ = c == 0 ? _content.MoveTo(x, y) : _content.LineTo(x, y);
                    }

                    _content.Stroke().RestoreState();
                    for (int c = 0; c < count; c++)
                    {
                        Marker(s, color, plotLeft + (band * (c + 0.5)), Y(Math.Clamp(Value(chart.Series[s], c), axisLow, axisHigh)), gap * 0.55);
                    }
                }

                return;
            }

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

    /// <summary>A series' marker, its shape by its index so colour isn't the only difference: circle, square, triangle, diamond.</summary>
    private void Marker(int series, Color color, double x, double y, double size)
    {
        double r = size / 2;
        _content.SaveState().SetFillRgb(color.R / 255.0, color.G / 255.0, color.B / 255.0).SetLineWidth(0.75).SetStrokeGray(1);
        switch (series % 4)
        {
            case 0:
                Arc(x, y, r, 0, 2 * Math.PI, moveTo: true);
                break;
            case 1:
                _content.Rectangle(x - (r * 0.9), y - (r * 0.9), r * 1.8, r * 1.8);
                break;
            case 2:
                _content.MoveTo(x, y + r).LineTo(x + (r * 0.95), y - (r * 0.75)).LineTo(x - (r * 0.95), y - (r * 0.75)).ClosePath();
                break;
            default:
                _content.MoveTo(x, y + r).LineTo(x + r, y).LineTo(x, y - r).LineTo(x - r, y).ClosePath();
                break;
        }

        _content.FillAndStroke().RestoreState();
    }

    /// <summary>A circular arc of radius <paramref name="r"/> from angle <paramref name="from"/> to <paramref name="to"/> (radians, counterclockwise), as cubic Béziers of 90° or less.</summary>
    private void Arc(double cx, double cy, double r, double from, double to, bool moveTo)
    {
        int parts = Math.Max(1, (int)Math.Ceiling(Math.Abs(to - from) / (Math.PI / 2)));
        double step = (to - from) / parts;
        double k = 4.0 / 3 * Math.Tan(step / 4);
        if (moveTo)
        {
            _content.MoveTo(cx + (r * Math.Cos(from)), cy + (r * Math.Sin(from)));
        }

        for (int i = 0; i < parts; i++)
        {
            double a0 = from + (step * i), a1 = a0 + step;
            (double c0, double s0, double c1, double s1) = (Math.Cos(a0), Math.Sin(a0), Math.Cos(a1), Math.Sin(a1));
            _content.CurveTo(cx + (r * (c0 - (k * s0))), cy + (r * (s0 + (k * c0))), cx + (r * (c1 + (k * s1))), cy + (r * (s1 - (k * c1))), cx + (r * c1), cy + (r * s1));
        }
    }

    /// <summary>A pie: slices clockwise from twelve o'clock, white lines between them, and a legend with each share in percent.</summary>
    private void DrawPie(Chart chart, TextStyle style, double left, double bottom, double width, double height)
    {
        double gap = style.Size?.Points ?? 8;
        double top = bottom + height;
        if (chart.Title is { Length: > 0 } title)
        {
            LayoutLine titleLine = Line(title, style with { Weight = FontWeight.Bold }, width, TextAlign.Center);
            DrawLine(titleLine, left, top - titleLine.Ascent);
            top -= titleLine.Height + (gap / 2);
        }

        ChartSeries series = chart.Series[0];
        double total = Enumerable.Range(0, chart.Categories.Length).Sum(c => Math.Max(0, Value(series, c)));
        CultureInfo culture = Culture(_document.Language);
        var entries = new List<(Color, string, int)>();
        for (int c = 0; c < chart.Categories.Length; c++)
        {
            double share = total > 0 ? Math.Max(0, Value(series, c)) / total : 0;
            entries.Add((series.Color is { } own && chart.Categories.Length == 1 ? own : ChartPalette[c % ChartPalette.Length], $"{chart.Categories[c]} {share.ToString("P0", culture)}", -1));
        }

        double floor = bottom;
        if (chart.Legend ?? true)
        {
            floor += DrawLegend(entries, style, left, bottom, width) + (gap / 2);
        }

        double r = Math.Max(1, Math.Min(width, top - floor) / 2) - 1;
        double cx = left + (width / 2), cy = floor + ((top - floor) / 2);
        double angle = Math.PI / 2; // Twelve o'clock, going clockwise: angles decrease.
        for (int c = 0; c < chart.Categories.Length && total > 0; c++)
        {
            double sweep = Math.Max(0, Value(series, c)) / total * 2 * Math.PI;
            if (sweep <= 0)
            {
                continue;
            }

            Color color = entries[c].Item1;
            _content.SaveState().SetFillRgb(color.R / 255.0, color.G / 255.0, color.B / 255.0).SetLineWidth(1).SetStrokeGray(1).SetLineJoin(1);
            if (sweep >= (2 * Math.PI) - 1e-9)
            {
                Arc(cx, cy, r, angle, angle - (2 * Math.PI), moveTo: true);
                _content.ClosePath();
            }
            else
            {
                _content.MoveTo(cx, cy).LineTo(cx + (r * Math.Cos(angle)), cy + (r * Math.Sin(angle)));
                Arc(cx, cy, r, angle, angle - sweep, moveTo: false);
                _content.ClosePath();
            }

            _content.FillAndStroke().RestoreState();
            angle -= sweep;
        }
    }

    private static double Value(ChartSeries series, int category) =>
        category < series.Values.Length && double.IsFinite(series.Values[category]) ? series.Values[category] : 0;

    private static (double Low, double High) ValueRange(Chart chart)
    {
        if (chart.Kind == ChartKind.Line)
        {
            // A line shows change: the axis starts near the data, not at 0, unless the data comes close to it.
            double min = double.MaxValue, max = double.MinValue;
            foreach (ChartSeries series in chart.Series)
            {
                for (int c = 0; c < chart.Categories.Length; c++)
                {
                    (min, max) = (Math.Min(min, Value(series, c)), Math.Max(max, Value(series, c)));
                }
            }

            if (min > 0 && min < (max - min))
            {
                min = 0;
            }

            return max > min ? (min, max) : (min - 1, max + 1);
        }

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
        return DrawLegend([.. chart.Series.Select((s, i) => (SeriesColor(chart, i), s.Name ?? "", chart.Kind == ChartKind.Line ? i : -1))], style, left, bottom, width);
    }

    /// <summary>A legend of (colour, text, marker or -1 for a square) items in centred rows; returns the height used.</summary>
    private double DrawLegend(List<(Color Color, string Text, int Marker)> entries, TextStyle style, double left, double bottom, double width)
    {
        double size = style.Size?.Points ?? 8;
        var items = entries.Select(e => (e.Color, Text: Line(e.Text, style, 1e6, TextAlign.Left), e.Marker)).ToList();
        var rows = new List<List<(Color Color, LayoutLine Text, int Marker)>> { new() };
        double used = 0;
        foreach ((Color color, LayoutLine text, int marker) in items)
        {
            double itemWidth = size + 3 + text.Width + size;
            if (used + itemWidth > width && rows[^1].Count > 0)
            {
                rows.Add([]);
                used = 0;
            }

            rows[^1].Add((color, text, marker));
            used += itemWidth;
        }

        double lineHeight = items[0].Text.Height;
        double y = bottom + (lineHeight * (rows.Count - 1));
        foreach (List<(Color Color, LayoutLine Text, int Marker)> row in rows)
        {
            double rowWidth = row.Sum(i => size + 3 + i.Text.Width + size) - size;
            double x = left + ((width - rowWidth) / 2);
            foreach ((Color color, LayoutLine text, int marker) in row)
            {
                if (marker < 0)
                {
                    FillBox(color, x, y + (lineHeight * 0.2), size * 0.8, size * 0.8);
                }
                else
                {
                    Marker(marker, color, x + (size * 0.4), y + (lineHeight * 0.2) + (size * 0.4), size * 0.6);
                }

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
