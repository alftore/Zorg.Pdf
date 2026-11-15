using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Zorg.Pdf.Layout;
using Zorg.Pdf.Validation;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// Text boxes: paragraphs at a fixed place on the current page, outside the flow. Text that doesn't fit is laid out
/// again smaller (every run scaled by one factor, in 0.25 pt steps of the body size) or cut at a whole line. Tagged
/// as a Div of Ps in content order; a border is a Layout artifact.
/// </summary>
internal sealed partial class RenderEngine
{
    private const double DefaultMinTextSize = 6;

    private static readonly string[] PostenFonts = ["Arial", "Courier New", "Verdana"];
    private const double ShrinkStep = 0.25;

    /// <summary>Multiplies every text size while a text box is laid out smaller; 1 otherwise.</summary>
    private double _textScale = 1;

    private void PlaceTextBox(int index, TextBox box)
    {
        string path = $"/content/{index}";
        (double x, double y, double width, double height, Thickness padding) = box.Resolve()!.Value; // Validated; bound templates leave only paragraphs.
        double innerWidth = width - padding.Left.Points - padding.Right.Points;
        double innerHeight = height - padding.Top.Points - padding.Bottom.Points;
        Paragraph[] paragraphs = [.. box.Content.IsDefault ? [] : box.Content.OfType<Paragraph>()];
        double body = paragraphs.Length == 0 ? 10 : _styles.For(paragraphs[0]).Size?.Points ?? 10;
        if (box.Preset == TextBoxPreset.NorwegianWindow)
        {
            CheckPostenAddress(paragraphs, path);
        }

        double scale = 1;
        if (box.Overflow == TextOverflow.ShrinkToFit && TextHeight(Layout(1, report: false), 1) > innerHeight + 0.01)
        {
            // The fewest 0.25 pt steps down that make it fit, by bisection: a smaller size never needs more room.
            double min = Math.Min(box.MinSize?.Points ?? DefaultMinTextSize, body);
            int most = (int)Math.Floor((body - min) / ShrinkStep);
            int low = 1, high = most;
            while (low < high)
            {
                int middle = (low + high) / 2;
                double trial = (body - (middle * ShrinkStep)) / body;
                if (TextHeight(Layout(trial, report: false), trial) <= innerHeight + 0.01)
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }

            scale = most < 1 ? 1 : (body - (low * ShrinkStep)) / body;
        }

        if (scale < 1)
        {
            _sink.Info(DiagnosticCodes.TextShrunk, string.Create(CultureInfo.InvariantCulture, $"The text was made smaller to fit the text box: {body * scale:0.##} pt instead of {body:0.##} pt."), path);
        }

        List<PreparedText> texts = Layout(scale, report: true);
        double top = _pageGeometry.Height - y;
        double left = x + padding.Left.Points;
        double cursor = top - padding.Top.Points;
        double bottom = top - height + padding.Bottom.Points;
        if (box.BorderColor is { } color)
        {
            double line = box.BorderWidth?.Points ?? 0.5;
            if (line > 0)
            {
                _content.BeginArtifact("Layout"u8, []).SaveState().SetLineWidth(line).SetStrokeRgb(color.R / 255.0, color.G / 255.0, color.B / 255.0)
                    .StrokeRectangle(x, top - height, width, height)
                    .RestoreState().EndMarkedContent();
                (_stroke, _strokeWidth) = (null, 1);
            }
        }

        Tagger.Element div = _tagger.Begin(StructureRole.Div);
        (int Paragraph, int Lines)? dropped = null;
        for (int p = 0; p < texts.Count; p++)
        {
            Tagger.Element? element = null;
            bool open = false;
            foreach (LayoutLine line in texts[p].Lines)
            {
                if (box.Overflow != TextOverflow.Visible && cursor - line.Height < bottom - 0.01)
                {
                    dropped = dropped is { } d ? (d.Paragraph, d.Lines + 1) : (p, 1);
                    continue;
                }

                if (line.Runs.Count > 0)
                {
                    if (!open)
                    {
                        element ??= _tagger.Begin(StructureRole.P, div, texts[p].Language);
                        _content.BeginMarkedContent(Tagger.Name(StructureRole.P), _tagger.Mark(element, _page));
                        BeginText();
                        open = true;
                    }

                    DrawRuns(line, left + line.Offset, cursor - line.Ascent);
                }

                cursor -= line.Height;
            }

            if (open)
            {
                EndText();
            }

            if (element is not null)
            {
                _tagger.End(element, _documentWriter.Pages);
            }

            cursor -= texts[p].SpaceAfter * scale;
        }

        _tagger.End(div, _documentWriter.Pages);
        if (dropped is ({ } first, int count))
        {
            string even = box.Overflow == TextOverflow.ShrinkToFit ? string.Create(CultureInfo.InvariantCulture, $", even at {body * scale:0.##} pt") : "";
            _sink.Warning(DiagnosticCodes.TextOverflow, string.Create(CultureInfo.InvariantCulture, $"The text doesn't fit the text box{even}: {count} {(count == 1 ? "line" : "lines")} from this paragraph on were left out. Shorten the text, make the box larger or lower minSize."), $"{path}/content/{first}", _page + 1);
        }
        else if (box.Overflow == TextOverflow.Visible && cursor + (texts.Count > 0 ? texts[^1].SpaceAfter * scale : 0) < bottom - 0.01)
        {
            _sink.Warning(DiagnosticCodes.TextOverflow, string.Create(CultureInfo.InvariantCulture, $"The text runs {bottom - cursor:0.#} pt past the bottom of the text box."), path, _page + 1);
        }

        _pageEmpty = false; // Content that follows starts below anything above it, as on a page that has text.

        List<PreparedText> Layout(double factor, bool report)
        {
            _textScale = factor;
            try
            {
                var laidOut = new List<PreparedText>(paragraphs.Length);
                for (int p = 0; p < paragraphs.Length; p++)
                {
                    // Lines of an address sit close: no space after a paragraph unless it asks for some.
                    laidOut.Add(LayoutText(paragraphs[p], _styles.For(paragraphs[p]), $"{path}/content/{p}", null, report, innerWidth) with { SpaceAfter = paragraphs[p].SpaceAfter?.Points ?? 0 });
                }

                return laidOut;
            }
            finally
            {
                _textScale = 1;
            }
        }
    }

    /// <summary>The height of laid-out paragraphs with the space between them (not after the last), scaled.</summary>
    private static double TextHeight(List<PreparedText> texts, double scale) =>
        texts.Sum(t => t.Lines.Sum(l => l.Height)) + texts.Take(texts.Count - 1).Sum(t => t.SpaceAfter * scale);

    /// <summary>
    /// Posten's rules for an address read by machine (Bring KK-833.12.2022): two lines or more, no blank lines, the
    /// same font throughout, Arial or Courier New at 10 to 12 pt or Verdana at 9 to 12 pt, not bold, italic or underlined.
    /// The layout rules are warnings; the type rules, which decide the discount for machine-sorted mail, information.
    /// </summary>
    private void CheckPostenAddress(Paragraph[] paragraphs, string path)
    {
        string[] lines = [.. paragraphs.Select(p => string.Concat(p.Inlines.Select(i => i is TextRun run ? run.Text : i is LineBreak ? "\n" : "")))];
        int filled = lines.Count(l => !string.IsNullOrWhiteSpace(l)) + lines.Sum(l => l.Count(c => c == '\n'));
        if (filled < 2)
        {
            _sink.Warning(DiagnosticCodes.AddressLayout, "Posten needs an address of two lines or more: name, then street or box, then postcode and town alone on the last line.", path);
        }

        int first = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l)), last = Array.FindLastIndex(lines, l => !string.IsNullOrWhiteSpace(l));
        if (first >= 0 && lines[first..(last + 1)].Any(string.IsNullOrWhiteSpace))
        {
            _sink.Warning(DiagnosticCodes.AddressLayout, "The address has a blank line: Posten's machines read an address with even line spacing and no blank lines.", path);
        }

        var faces = new HashSet<(string? Family, double Size)>();
        bool emphasis = false;
        foreach (Paragraph paragraph in paragraphs)
        {
            TextStyle style = _styles.For(paragraph);
            foreach (TextRun run in paragraph.Inlines.OfType<TextRun>())
            {
                TextStyle merged = run.Style is { } own ? style.Merge(own) : style;
                faces.Add((merged.FontFamily ?? _defaultFamily, merged.Size?.Points ?? 10));
                emphasis |= merged.Weight is FontWeight.Bold || merged.FontStyle is FontStyle.Italic;
            }
        }

        bool sizeOk = faces.All(f => f.Family is "Verdana" ? f.Size is >= 9 and <= 12 : f.Size is >= 10 and <= 12);
        bool familyOk = faces.All(f => f.Family is not null && PostenFonts.Contains(f.Family, StringComparer.OrdinalIgnoreCase));
        if (emphasis || faces.Count > 1 || !sizeOk || !familyOk)
        {
            _sink.Info(DiagnosticCodes.AddressTypography, "For machine-sorted mail Posten wants the address in one font and size, Arial or Courier New at 10 to 12 pt or Verdana at 9 to 12 pt, not bold or italic.", path);
        }
    }
}
