using System;
using Zorg.Pdf.Layout;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// The watermark: one content stream every page shares, drawn before the page's own (behind) or after it and the
/// bands (over), centred on the page and turned about its centre. It's wrapped in q/Q, so it neither inherits nor
/// leaks graphics state, and it's a Pagination/Watermark artifact. Its opacity is an ExtGState in every page's
/// resources.
/// </summary>
internal sealed partial class RenderEngine
{
    /// <summary>The share of the page a watermark of automatic size may span along its angle.</summary>
    private const double WatermarkReach = 0.8;

    private static readonly byte[] WatermarkStateName = "GW"u8.ToArray();

    private LayoutLine? _watermarkLine;
    private ImageSet.Entry? _watermarkImage;
    private (double Width, double Height) _watermarkImageSize;
    private PdfReference? _watermarkStream;
    private PdfReference? _watermarkState;

    /// <summary>Lays out the watermark before the first page, so every page's resources can name its font or image.</summary>
    private void PrepareWatermark()
    {
        if (_document.Watermark is not { } watermark)
        {
            return;
        }

        if (watermark.Text is { Length: > 0 } text)
        {
            TextStyle style = _styles.Named("watermark").Merge(new TextStyle { Color = watermark.Color ?? Color.Gray(0x80) });
            double size = watermark.Size?.Points ?? AutoWatermarkSize(text, style, watermark.Angle);
            style = style.Merge(new TextStyle { Size = Length.Pt(size) });
            PreparedText laidOut = LayoutText(new Paragraph(text), style, "/watermark/text", null, report: true, width: 1e6);
            if (laidOut.Lines is not [{ } line, ..])
            {
                return;
            }

            _watermarkLine = line;
            foreach (LineRun run in line.Runs)
            {
                if (!_bandFonts.Contains(run.Font))
                {
                    _bandFonts.Add(run.Font);
                }
            }

            _bandFonts.Sort((a, b) => string.CompareOrdinal(a.Font.PostScriptName, b.Font.PostScriptName));
        }
        else if (watermark.Image is { } source && _images.TryGet(source, out ImageSet.Entry entry))
        {
            double width = watermark.Size?.Points ?? (_pageGeometry.Width / 2);
            _watermarkImage = entry;
            _watermarkImageSize = (width, width * entry.Height / entry.Width);
            if (!_bandImages.Contains(entry))
            {
                _bandImages.Add(entry);
            }
        }
        else
        {
            return; // Reported before the render started.
        }

        _watermarkStream = _writer.Allocate();
        _watermarkState = _writer.Allocate();
    }

    /// <summary>The font size at which the text spans <see cref="WatermarkReach"/> of the page along its angle.</summary>
    private double AutoWatermarkSize(string text, TextStyle style, double angle)
    {
        PreparedText probe = LayoutText(new Paragraph(text), style.Merge(new TextStyle { Size = Length.Pt(100) }), "/watermark/text", null, report: false, width: 1e6);
        double width = probe.Lines is [{ } line, ..] ? line.Width : 0;
        if (width <= 0)
        {
            return 72;
        }

        double radians = angle * Math.PI / 180;
        double cos = Math.Abs(Math.Cos(radians)), sin = Math.Abs(Math.Sin(radians));
        double reach = Math.Min(
            cos > 1e-6 ? WatermarkReach * _pageGeometry.Width / cos : double.MaxValue,
            sin > 1e-6 ? WatermarkReach * _pageGeometry.Height / sin : double.MaxValue);
        return Math.Clamp(100 * reach / width, 8, 400);
    }

    private PdfReference[] WatermarkUnder() =>
        _watermarkStream is { } stream && _document.Watermark?.Layer == WatermarkLayer.Behind ? [stream] : [];

    private PdfReference[] WatermarkOver() =>
        _watermarkStream is { } stream && _document.Watermark?.Layer != WatermarkLayer.Behind ? [stream] : [];

    private (byte[] Name, PdfReference State)? WatermarkState() =>
        _watermarkState is { } state ? (WatermarkStateName, state) : null;

    /// <summary>Draws and writes the watermark stream and its graphics state, after the last page.</summary>
    private void WriteWatermark()
    {
        if (_watermarkStream is not { } stream || _watermarkState is not { } state || _document.Watermark is not { } watermark)
        {
            return;
        }

        double radians = watermark.Angle * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        _content = new ContentStreamBuilder();
        _content.SaveState().SetGraphicsState(WatermarkStateName)
            .Transform(cos, sin, -sin, cos, _pageGeometry.Width / 2, _pageGeometry.Height / 2)
            .SetFillGray(0);
        (_fill, _stroke, _strokeWidth, _renderingMode) = (Color.Black, null, 1, 0);
        _content.BeginArtifact("Pagination"u8, "Watermark"u8);
        if (_watermarkLine is { } line)
        {
            BeginText();
            DrawRuns(line, -line.Width / 2, (line.Height / 2) - line.Ascent);
            EndText(); // Ends the artifact too.
        }
        else if (_watermarkImage is { } image)
        {
            (double width, double height) = _watermarkImageSize;
            DrawImage(image, -width / 2, -height / 2, width, height);
            _content.EndMarkedContent();
        }

        _content.RestoreState();
        _writer.BeginStreamObject(stream);
        _writer.EndStreamObject(_content.WrittenSpan, compress: true);

        double opacity = Math.Clamp(watermark.Opacity, 0, 1);
        _writer.BeginObject(state);
        _writer.BeginDictionary();
        _writer.Name("Type"u8);
        _writer.Name("ExtGState"u8);
        _writer.Name("ca"u8);
        _writer.Real(opacity);
        _writer.Name("CA"u8);
        _writer.Real(opacity);
        _writer.EndDictionary();
        _writer.EndObject();
    }
}
