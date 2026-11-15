using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Layout;
using Zorg.Pdf.Signing;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// Visible signature fields: a box in the flow, a widget annotation with its own appearance stream (a border, and the
/// label or who signed and when), tagged as a Form element with an OBJR, as PDF/UA-1 asks (7.18.4). The field named
/// in <see cref="PdfSignatureOptions.FieldName"/> carries the signature; the others are empty for later signers.
/// </summary>
internal sealed partial class RenderEngine
{
    private const double FieldSpaceAfter = 6;
    private const double FieldPadding = 4;

    /// <summary>The fields on the current page, written with it.</summary>
    private readonly List<PendingField> _pageFields = [];

    /// <summary>Every visible field, for the AcroForm's /Fields.</summary>
    private readonly List<PdfReference> _formFields = [];

    private async ValueTask PlaceSignatureFieldAsync(SignatureField field, CancellationToken cancellationToken)
    {
        double width = Math.Min(field.Width?.Points ?? Length.Mm(60).Points, _geometry.ContentWidth);
        double height = Math.Min(field.Height?.Points ?? Length.Mm(20).Points, _geometry.ContentHeight);
        if (!_pageEmpty && _y - height < _geometry.Bottom)
        {
            await NewPageAsync(cancellationToken).ConfigureAwait(false);
        }

        double x = field.Align switch
        {
            TextAlign.Center => _geometry.Left + ((_geometry.ContentWidth - width) / 2),
            TextAlign.Right => _geometry.Right - width,
            _ => _geometry.Left,
        };
        double top = _y, bottom = _y - height;
        bool signed = _signatureVisible && _options.Signature!.FieldName == field.Name;
        PdfReference widget = signed ? _signatureField : _writer.Allocate();
        PdfReference appearance = _writer.Allocate();
        string label = string.IsNullOrWhiteSpace(field.Label) ? "Signature" : field.Label;

        Tagger.Element form = _tagger.Begin(StructureRole.Form);
        int key = _tagger.ObjectKey(form);
        Tagger.AddObject(form, widget, _page);
        _tagger.End(form, _documentWriter.Pages);

        WriteFieldAppearance(appearance, field, width, height, signed ? SignedLines(_options.Signature!) : [label], dim: !signed);
        _pageFields.Add(new PendingField(widget, appearance, field.Name, label, (x, bottom, x + width, top), key, signed));
        _formFields.Add(widget);
        _y = bottom - FieldSpaceAfter;
        _pageEmpty = false;
    }

    /// <summary>What a signed field shows: who, when, and the reason and place if given.</summary>
    private static string[] SignedLines(PdfSignatureOptions signature)
    {
        string name = signature.Name ?? signature.Signer.Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        DateTimeOffset time = signature.SigningTime!.Value;
        var lines = new List<string>
        {
            $"Digitally signed by {name}",
            string.Create(CultureInfo.InvariantCulture, $"Date: {time:yyyy-MM-dd HH:mm} {(time.Offset < TimeSpan.Zero ? '-' : '+')}{time.Offset.Duration():hh\\:mm}"),
        };
        if (signature.Reason is { Length: > 0 } reason)
        {
            lines.Add($"Reason: {reason}");
        }

        if (signature.Location is { Length: > 0 } location)
        {
            lines.Add($"Location: {location}");
        }

        return [.. lines];
    }

    /// <summary>
    /// Draws a field's appearance into its own form XObject: a grey border, then the lines from the top, as many as
    /// fit. The page's drawing state is put back afterwards, as the field is placed mid-page.
    /// </summary>
    private void WriteFieldAppearance(PdfReference stream, SignatureField field, double width, double height, string[] text, bool dim)
    {
        TextStyle style = _styles.For(field);
        double size = Math.Clamp(Math.Min(style.Size?.Points ?? 8, (height - (2 * FieldPadding)) / (text.Length * 1.25)), 4, 12);
        style = style.Merge(new TextStyle { Size = Length.Pt(size), Color = dim ? Color.Gray(0x55) : Color.Black });

        (ContentStreamBuilder content, Color fill, Color? stroke, double strokeWidth, int mode, EmbeddedFont? font, double fontSize) =
            (_content, _fill, _stroke, _strokeWidth, _renderingMode, _textFont, _textSize);
        var pageFonts = new HashSet<EmbeddedFont>(_pageFonts);
        _content = new ContentStreamBuilder();
        (_fill, _stroke, _strokeWidth, _renderingMode) = (Color.Black, null, 1, 0);
        var used = new SortedSet<EmbeddedFont>(Comparer<EmbeddedFont>.Create((a, b) => _fonts.Reference(a).Number.CompareTo(_fonts.Reference(b).Number)));
        try
        {
            _content.SaveState().SetStrokeGray(0.6).SetLineWidth(0.75).StrokeRectangle(0.375, 0.375, width - 0.75, height - 0.75);
            BeginText();
            double baseline = height - FieldPadding;
            foreach (string paragraph in text)
            {
                foreach (LayoutLine line in LayoutText(new Paragraph(paragraph), style, "/", null, report: true, width: width - (2 * FieldPadding)).Lines)
                {
                    if (baseline - line.Height < FieldPadding - 0.01)
                    {
                        break; // The box is full.
                    }

                    baseline -= line.Ascent;
                    DrawRuns(line, FieldPadding + line.Offset, baseline);
                    baseline -= line.Height - line.Ascent;
                    used.UnionWith(line.Runs.Select(r => r.Font));
                }
            }

            EndTextObject();
            _content.RestoreState();

            _writer.BeginStreamObject(stream);
            _writer.Name("Type"u8);
            _writer.Name("XObject"u8);
            _writer.Name("Subtype"u8);
            _writer.Name("Form"u8);
            _writer.Name("BBox"u8);
            _writer.BeginArray();
            _writer.Integer(0);
            _writer.Integer(0);
            _writer.Real(width);
            _writer.Real(height);
            _writer.EndArray();
            _writer.Name("Resources"u8);
            _writer.BeginDictionary();
            _writer.Name("Font"u8);
            _writer.BeginDictionary();
            foreach (EmbeddedFont usedFont in used)
            {
                _writer.Name(_fonts.Use(usedFont, _writer));
                _writer.Reference(_fonts.Reference(usedFont));
            }

            _writer.EndDictionary();
            _writer.EndDictionary();
            _writer.EndStreamObject(_content.WrittenSpan, compress: true);
        }
        finally
        {
            (_content, _fill, _stroke, _strokeWidth, _renderingMode, _textFont, _textSize) = (content, fill, stroke, strokeWidth, mode, font, fontSize);
            _pageFonts.IntersectWith(pageFonts); // The appearance's fonts are its own resources, not the page's.
        }
    }

    /// <summary>Writes the current page's field widgets and returns their references for its <c>/Annots</c>.</summary>
    private PdfReference[] WriteFieldWidgets()
    {
        PdfReference[] widgets = [.. _pageFields.Select(f => f.Widget)];
        foreach (PendingField field in _pageFields)
        {
            _documentWriter.WriteSignatureWidget(field.Widget, _page, field.Name, field.Label, field.Rect, field.Appearance, field.Key, field.Signed ? _writer.SignatureReference : null);
        }

        _pageFields.Clear();
        return widgets;
    }

    private sealed record PendingField(PdfReference Widget, PdfReference Appearance, string Name, string Label, (double Left, double Bottom, double Right, double Top) Rect, int Key, bool Signed);
}
