using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Zorg.Pdf.Validation;

/// <summary>
/// Checks a document's shape and values without rendering: everything that can be known before the first byte is
/// written. Every problem is a diagnostic with a JSON pointer; nothing throws for a bad document.
/// </summary>
internal sealed partial class DocumentValidator
{
    private readonly List<PdfDiagnostic> _diagnostics = [];
    private readonly ImmutableDictionary<string, TextStyle> _styles;

    private DocumentValidator(PdfDocument document)
    {
        _styles = StyleSheet.Default.Styles.SetItems(document.Styles?.Styles ?? ImmutableDictionary<string, TextStyle>.Empty);
    }

    public static ImmutableArray<PdfDiagnostic> Validate(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var validator = new DocumentValidator(document);
        validator.Document(document);
        return [.. validator._diagnostics];
    }

    private void Document(PdfDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Title))
        {
            Error(DiagnosticCodes.NoTitle, "The document needs a title: viewers show it in place of the file name, and PDF/UA requires one.", "/title");
        }

        if (document.Language is null || !LanguageTag().IsMatch(document.Language))
        {
            Error(DiagnosticCodes.BadLanguage, $"\"{document.Language}\" isn't a language tag. Use a BCP 47 tag such as \"en\", \"nb-NO\" or \"de-CH\"; screen readers pick their voice from it.", "/language");
        }

        Page(document.Page ?? PageSettings.A4, "/page");

        if (document.Styles is { } sheet)
        {
            foreach ((string name, TextStyle style) in sheet.Styles)
            {
                Style(style, $"/styles/styles/{Escape(name)}");
            }
        }

        Band(document.Header, "/header");
        Band(document.Footer, "/footer");
        if (document.Content.IsDefault)
        {
            Error(DiagnosticCodes.MissingProperty, "The content is missing. Use an empty array for a document with no blocks.", "/content");
            return;
        }

        for (int i = 0; i < document.Content.Length; i++)
        {
            Block(document.Content[i], $"/content/{i}");
        }
    }

    private void Page(PageSettings page, string path)
    {
        bool landscape = page.Orientation == PageOrientation.Landscape;
        double width = landscape ? page.Size.Height.Points : page.Size.Width.Points;
        double height = landscape ? page.Size.Width.Points : page.Size.Height.Points;
        bool sizeOk = Positive(page.Size.Width, $"{path}/size/width") & Positive(page.Size.Height, $"{path}/size/height");
        Thickness m = page.Margin;
        bool marginsOk = NotNegative(m.Top, $"{path}/margin/top") & NotNegative(m.Right, $"{path}/margin/right")
            & NotNegative(m.Bottom, $"{path}/margin/bottom") & NotNegative(m.Left, $"{path}/margin/left");
        if (sizeOk && marginsOk && (width - m.Left.Points - m.Right.Points < 36 || height - m.Top.Points - m.Bottom.Points < 36))
        {
            Error(DiagnosticCodes.NoContentArea, Invariant($"The margins leave less than half an inch for content on a {width:0.#} × {height:0.#} pt page."), $"{path}/margin");
        }
    }

    private void Band(PageBand? band, string path)
    {
        if (band is null)
        {
            return;
        }

        if (band.Content.IsDefault)
        {
            Error(DiagnosticCodes.MissingProperty, "A page band needs \"content\".", $"{path}/content");
            return;
        }

        for (int i = 0; i < band.Content.Length; i++)
        {
            Block block = band.Content[i];
            if (block is not (Paragraph or Zorg.Pdf.Image or Spacer))
            {
                Error(DiagnosticCodes.BandContent, "Headers and footers hold paragraphs, images and spacers only.", $"{path}/content/{i}");
                continue;
            }

            Block(block, $"{path}/content/{i}");
        }
    }

    private void Block(Block? block, string path)
    {
        if (block is null)
        {
            Error(DiagnosticCodes.MissingProperty, "A block is null.", path);
            return;
        }

        if (block.Style is { } style && !_styles.ContainsKey(style))
        {
            Warning(DiagnosticCodes.UnknownStyle, $"There's no style named \"{style}\", so the block's default style is used. Add it to \"styles\" or use a built-in name.", $"{path}/style");
        }

        switch (block)
        {
            case Heading heading:
                if (heading.Level is < 1 or > 6)
                {
                    Error(DiagnosticCodes.HeadingLevel, $"Heading level {heading.Level} isn't 1 to 6.", $"{path}/level");
                }

                Inlines(heading.Inlines, $"{path}/inlines");
                break;

            case Paragraph paragraph:
                Inlines(paragraph.Inlines, $"{path}/inlines");
                if (paragraph.SpaceAfter is { } space)
                {
                    NotNegative(space, $"{path}/spaceAfter");
                }

                break;

            case Table table:
                Table(table, path);
                break;

            case Image image:
                Image(image, path);
                break;

            case Spacer spacer:
                NotNegative(spacer.Height, $"{path}/height");
                break;
        }
    }

    private void Table(Table table, string path)
    {
        if (table.Columns.IsDefaultOrEmpty)
        {
            Error(DiagnosticCodes.NoColumns, "A table needs at least one column.", $"{path}/columns");
            return;
        }

        for (int c = 0; c < table.Columns.Length; c++)
        {
            Column column = table.Columns[c];
            if (column is null)
            {
                Error(DiagnosticCodes.MissingProperty, "A column is null.", $"{path}/columns/{c}");
                continue;
            }

            if (!double.IsFinite(column.Width.Value) || column.Width.Value <= 0)
            {
                Error(DiagnosticCodes.BadColumnWidth, column.Width.IsStar ? "A column's share must be more than 0, such as \"*\" or \"2*\"." : "A fixed column width must be more than 0.", $"{path}/columns/{c}/width");
            }
        }

        switch (table.Rows)
        {
            case InlineTableRows inline when !inline.Rows.IsDefault:
                for (int r = 0; r < inline.Rows.Length; r++)
                {
                    ImmutableArray<Cell> row = inline.Rows[r];
                    if (row.IsDefault || row.Length != table.Columns.Length)
                    {
                        Error(DiagnosticCodes.CellCount, Invariant($"Row {r} has {(row.IsDefault ? 0 : row.Length)} cells; the table has {table.Columns.Length} columns."), $"{path}/rows/{r}");
                        continue;
                    }

                    for (int c = 0; c < row.Length; c++)
                    {
                        if (row[c] is null)
                        {
                            Error(DiagnosticCodes.MissingProperty, "A cell is null. Use \"\" for an empty cell.", $"{path}/rows/{r}/{c}");
                            continue;
                        }

                        Inlines(row[c].Inlines, $"{path}/rows/{r}/{c}/inlines");
                    }
                }

                break;

            case BoundTableRows bound when string.IsNullOrWhiteSpace(bound.Slot):
                Error(DiagnosticCodes.EmptySlot, "A data slot needs a name.", $"{path}/rows/$data");
                break;

            case null:
                Error(DiagnosticCodes.MissingProperty, "The rows are null. Leave \"rows\" out for a table with only a header.", $"{path}/rows");
                break;
        }
    }

    private void Image(Image image, string path)
    {
        if (!image.Decorative && string.IsNullOrWhiteSpace(image.Alt))
        {
            Error(DiagnosticCodes.NoAltText, "An image needs \"alt\" text that says what it shows, or \"decorative\": true if it adds nothing to the text.", $"{path}/alt");
        }

        if (image.Width is { } width)
        {
            Positive(width, $"{path}/width");
        }

        if (image.Height is { } height)
        {
            Positive(height, $"{path}/height");
        }

        if (image.Source is SlotImageSource slot && string.IsNullOrWhiteSpace(slot.Slot))
        {
            Error(DiagnosticCodes.EmptySlot, "A data slot needs a name.", $"{path}/source/$data");
        }
        else if (image.Source is null)
        {
            Error(DiagnosticCodes.MissingProperty, "An image needs a source.", $"{path}/source");
        }
    }

    private void Inlines(ImmutableArray<Inline> inlines, string path)
    {
        if (inlines.IsDefault)
        {
            Error(DiagnosticCodes.MissingProperty, "The inlines are missing. Use an array of text, such as [\"Hello\"].", path);
            return;
        }

        for (int i = 0; i < inlines.Length; i++)
        {
            switch (inlines[i])
            {
                case null:
                    Error(DiagnosticCodes.MissingProperty, "An inline is null.", $"{path}/{i}");
                    break;
                case TextRun { Style: { } style }:
                    Style(style, $"{path}/{i}/style");
                    break;
            }
        }
    }

    private void Style(TextStyle style, string path)
    {
        if (style.Size is { } size)
        {
            Positive(size, $"{path}/size");
        }

        if (style.LineHeight is { } lineHeight && (!double.IsFinite(lineHeight) || lineHeight <= 0 || lineHeight > 10))
        {
            Error(DiagnosticCodes.BadLineHeight, Invariant($"Line height {lineHeight} isn't between 0 and 10. It's a multiple of the font size, such as 1.2."), $"{path}/lineHeight");
        }
    }

    private bool Positive(Length length, string path)
    {
        if (double.IsFinite(length.Points) && length.Points > 0 && length.Points < 1e6)
        {
            return true;
        }

        Error(DiagnosticCodes.BadLength, Invariant($"{length.Points} pt isn't a usable size here: it must be more than 0 and less than 1,000,000 pt."), path);
        return false;
    }

    private bool NotNegative(Length length, string path)
    {
        if (double.IsFinite(length.Points) && length.Points >= 0 && length.Points < 1e6)
        {
            return true;
        }

        Error(DiagnosticCodes.BadLength, Invariant($"{length.Points} pt isn't a usable length here: it must be 0 or more and less than 1,000,000 pt."), path);
        return false;
    }

    private void Error(string code, string message, string path) => _diagnostics.Add(new PdfDiagnostic(code, DiagnosticSeverity.Error, message, path));

    private void Warning(string code, string message, string path) => _diagnostics.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message, path));

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{1,8})*$")]
    private static partial Regex LanguageTag();
}
