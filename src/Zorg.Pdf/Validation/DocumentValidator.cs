using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Zorg.Pdf.Validation;

/// <summary>
/// Checks a document's shape and values without rendering: everything that can be known before the first byte is
/// written. Every problem is a diagnostic with a JSON pointer; nothing throws for a bad document.
/// </summary>
internal sealed partial class DocumentValidator
{
    private readonly List<PdfDiagnostic> _diagnostics = [];
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal); // Id to the path that has it.
    private readonly HashSet<string> _anchorPaths = new(StringComparer.Ordinal); // Where a block may have an Id.
    private readonly List<(string Id, string Path)> _internalLinks = []; // Checked against the ids at the end.
    private string? _noLinksIn; // Where links can't go yet, while validating it.
    private bool _inLink;
    private string? _tocPath; // The table of contents seen so far: one per document.
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
        foreach ((string id, string path) in validator._internalLinks)
        {
            if (!validator._ids.ContainsKey(id))
            {
                validator.Error(DiagnosticCodes.BadLink, $"The link goes to \"#{id}\", but no block has that id. Give the block \"id\": \"{id}\".", path);
            }
        }

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
        _page = Rendering.PageGeometry.From(document.Page ?? PageSettings.A4);
        if (document.OutlineLevels is < 0 or > 6)
        {
            Error(DiagnosticCodes.BadOutline, Invariant($"outlineLevels is 0 (no bookmarks) to 6; {document.OutlineLevels} isn't."), "/outlineLevels");
        }

        for (int i = 0; i < (document.Content.IsDefault ? 0 : document.Content.Length); i++)
        {
            _anchorPaths.Add($"/content/{i}");
        }

        if (document.Styles is { } sheet)
        {
            foreach ((string name, TextStyle style) in sheet.Styles)
            {
                Style(style, $"/styles/styles/{Escape(name)}");
            }
        }

        var attachmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < document.Attachments.Length; i++)
        {
            Attachment attachment = document.Attachments[i];
            string at = Invariant($"/attachments/{i}");
            if (string.IsNullOrWhiteSpace(attachment.Name) || attachment.Name.Length > 255 || attachment.Name.IndexOfAny(['/', '\\', ':']) >= 0 || System.Linq.Enumerable.Any(attachment.Name, char.IsControl))
            {
                Error(DiagnosticCodes.BadAttachment, "An attachment's name is a file name such as \"invoice.xml\": no folders, colons or control characters, at most 255 characters.", $"{at}/name");
            }
            else if (!attachmentNames.Add(attachment.Name))
            {
                Error(DiagnosticCodes.BadAttachment, $"Two attachments are named \"{attachment.Name}\"; names are unique.", $"{at}/name");
            }

            if (attachment.Source is null)
            {
                Error(DiagnosticCodes.BadAttachment, "An attachment needs a source: its bytes or a data slot.", $"{at}/source");
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(attachment.MimeType ?? "", "^[a-zA-Z0-9][a-zA-Z0-9!#$&^_.+-]{0,63}/[a-zA-Z0-9][a-zA-Z0-9!#$&^_.+-]{0,126}$"))
            {
                Error(DiagnosticCodes.BadAttachment, $"\"{attachment.MimeType}\" isn't a media type such as \"application/xml\".", $"{at}/mimeType");
            }
        }

        if (document.Background is { } background)
        {
            if (background.Source is null)
            {
                Error(DiagnosticCodes.BadBackground, "A background needs a source: the PDF's bytes or a data slot.", "/background/source");
            }

            if (background.FirstPage < 0 || background.OtherPages < 0)
            {
                Error(DiagnosticCodes.BadBackground, "A background's pages are numbered from 1, or 0 for none.", background.FirstPage < 0 ? "/background/firstPage" : "/background/otherPages");
            }
        }

        if (document.Watermark is { } watermark)
        {
            if ((watermark.Text is null) == (watermark.Image is null) || watermark.Text is { Length: 0 })
            {
                Error(DiagnosticCodes.BadWatermark, "A watermark has text or an image: one of them, not both.", "/watermark");
            }

            if (watermark.Opacity is < 0 or > 1 || double.IsNaN(watermark.Opacity))
            {
                Error(DiagnosticCodes.BadWatermark, Invariant($"Opacity is 0 (invisible) to 1 (solid); {watermark.Opacity} isn't."), "/watermark/opacity");
            }

            if (!double.IsFinite(watermark.Angle))
            {
                Error(DiagnosticCodes.BadWatermark, "The angle is a number of degrees.", "/watermark/angle");
            }

            if (watermark.Size is { } size)
            {
                Positive(size, "/watermark/size");
            }

            if (watermark.Image is SlotImageSource slot && string.IsNullOrWhiteSpace(slot.Slot))
            {
                Error(DiagnosticCodes.EmptySlot, "A data slot needs a name.", "/watermark/image/$data");
            }
        }

        Band(document.Header, "/header");
        Band(document.Footer, "/footer");
        if (document.Content.IsDefault)
        {
            Error(DiagnosticCodes.MissingProperty, "The content is missing. Use an empty array for a document with no blocks.", "/content");
            return;
        }

        int previousLevel = 0;
        for (int i = 0; i < document.Content.Length; i++)
        {
            Block(document.Content[i], $"/content/{i}");
            if (document.Content[i] is Heading { Level: >= 1 and <= 6 } heading)
            {
                // Screen readers navigate by heading level; a skipped level reads as a missing section.
                if (heading.Level > previousLevel + 1)
                {
                    string expected = previousLevel == 0 ? "The first heading should be level 1" : Invariant($"After a level {previousLevel} heading, the next level down is {previousLevel + 1}");
                    Warning(DiagnosticCodes.HeadingSkipsLevel, Invariant($"{expected}; this one is level {heading.Level}, so a level is skipped. PDF/UA documents must not skip heading levels."), $"/content/{i}/level");
                }

                previousLevel = heading.Level;
            }
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
            if (block is not (Paragraph or Zorg.Pdf.Image or Spacer or Repeat or Conditional))
            {
                Error(DiagnosticCodes.BandContent, "Headers and footers hold paragraphs, images and spacers only.", $"{path}/content/{i}");
                continue;
            }

            Block(block, $"{path}/content/{i}");
        }
    }

    private Rendering.PageGeometry _page;

    private void Block(Block? block, string path)
    {
        if (block is null)
        {
            Error(DiagnosticCodes.MissingProperty, "A block is null.", path);
            return;
        }

        if (block.Id is { } id)
        {
            Id(id, path);
        }

        if (block is Conditional { Content.IsDefault: false } shown && _anchorPaths.Contains(path))
        {
            // A top-level when's blocks end up at the top level, so they can be linked to.
            for (int b = 0; b < shown.Content.Length; b++)
            {
                _anchorPaths.Add($"{path}/content/{b}");
            }
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

            case Barcode barcode:
                Barcode(barcode, path);
                break;

            case Chart chart:
                Chart(chart, path);
                break;

            case SignatureField field:
                SignatureField(field, path);
                break;

            case Spacer spacer:
                NotNegative(spacer.Height, $"{path}/height");
                break;

            case List list:
                List(list, path, 1);
                break;

            case TextBox box:
                TextBox(box, path);
                break;

            case SectionBreak section:
                if (section.StartAt is < 1)
                {
                    Error(DiagnosticCodes.BadSection, Invariant($"startAt is 1 or more; {section.StartAt} isn't."), $"{path}/startAt");
                }
                else if (section.StartAt > MaxStartAt(section.Numbering))
                {
                    // Letters repeat (a, …, z, aa, …), so a large start makes every label long.
                    Error(DiagnosticCodes.BadSection, Invariant($"startAt is at most {MaxStartAt(section.Numbering):N0} for this numbering; {section.StartAt:N0} isn't."), $"{path}/startAt");
                }

                if (section.Prefix is { } prefix && (prefix.Length > 32 || System.Linq.Enumerable.Any(prefix, char.IsControl)))
                {
                    Error(DiagnosticCodes.BadSection, "A prefix is a few characters of text, such as \"A-\".", $"{path}/prefix");
                }

                if (!_anchorPaths.Contains(path))
                {
                    Error(DiagnosticCodes.BadSection, "A section break goes at the top level of the content (or in a top-level when).", path);
                }

                Band(section.Header, $"{path}/header");
                Band(section.Footer, $"{path}/footer");
                break;

            case TableOfContents toc:
                if (toc.MaxLevel is < 1 or > 6)
                {
                    Error(DiagnosticCodes.BadTableOfContents, Invariant($"maxLevel is 1 to 6; {toc.MaxLevel} isn't."), $"{path}/maxLevel");
                }

                if (toc.Title is { } tocTitle && string.IsNullOrWhiteSpace(tocTitle))
                {
                    Error(DiagnosticCodes.BadTableOfContents, "A title has text; leave it out for none.", $"{path}/title");
                }

                if (!_anchorPaths.Contains(path))
                {
                    Error(DiagnosticCodes.BadTableOfContents, "A table of contents goes at the top level of the content (or in a top-level when), not in a list, box, repeat or band.", path);
                }
                else if (_tocPath is { } first)
                {
                    Error(DiagnosticCodes.BadTableOfContents, $"There's already a table of contents at {first}; a document has one.", path);
                }
                else
                {
                    _tocPath = path;
                }

                break;

            case Repeat repeat:
                if (string.IsNullOrWhiteSpace(repeat.Items))
                {
                    Error(DiagnosticCodes.BadTemplate, "A repeat needs \"items\": the path of an array in the model.", $"{path}/items");
                }

                TemplateContent(repeat.Content, path, "A repeat");
                break;

            case Conditional conditional:
                if (string.IsNullOrWhiteSpace(conditional.Test))
                {
                    Error(DiagnosticCodes.BadTemplate, "A when needs \"test\": the path of a value in the model.", $"{path}/test");
                }

                TemplateContent(conditional.Content, path, "A when");
                break;
        }
    }

    private void TemplateContent(System.Collections.Immutable.ImmutableArray<Block> content, string path, string what)
    {
        if (content.IsDefaultOrEmpty)
        {
            Error(DiagnosticCodes.BadTemplate, $"{what} needs \"content\": the blocks it shows.", $"{path}/content");
            return;
        }

        for (int b = 0; b < content.Length; b++)
        {
            Block(content[b], $"{path}/content/{b}");
        }
    }

    /// <summary>The deepest a list may nest: six levels, as HTML's renderers and most style guides go.</summary>
    internal const int MaxListDepth = 6;

    private void List(List list, string path, int depth)
    {
        if (list.Indent is { } indent)
        {
            NotNegative(indent, $"{path}/indent");
        }

        if (list.Start < 0)
        {
            Error(DiagnosticCodes.ListShape, Invariant($"A list starts at {list.Start}; numbering starts at 0 or more."), $"{path}/start");
        }

        if (list.Items.IsDefaultOrEmpty)
        {
            Error(DiagnosticCodes.ListShape, "A list needs at least one item.", $"{path}/items");
            return;
        }

        for (int i = 0; i < list.Items.Length; i++)
        {
            string itemPath = $"{path}/items/{i}";
            if (list.Items[i] is not { Content.IsDefaultOrEmpty: false } item)
            {
                Error(DiagnosticCodes.ListShape, "A list item needs content: a string, or paragraphs and lists.", itemPath);
                continue;
            }

            for (int b = 0; b < item.Content.Length; b++)
            {
                string blockPath = $"{itemPath}/content/{b}";
                switch (item.Content[b])
                {
                    case Paragraph paragraph:
                        Block(paragraph, blockPath);
                        break;
                    case List nested when depth >= MaxListDepth:
                        Error(DiagnosticCodes.ListContent, Invariant($"Lists nest at most {MaxListDepth} deep."), blockPath);
                        break;
                    case List nested:
                        if (nested.Style is { } style && !_styles.ContainsKey(style))
                        {
                            Warning(DiagnosticCodes.UnknownStyle, $"There's no style named \"{style}\", so the list's default style is used. Add it to \"styles\" or use a built-in name.", $"{blockPath}/style");
                        }

                        List(nested, blockPath, depth + 1);
                        break;
                    case Repeat or Conditional:
                        Block(item.Content[b], blockPath); // Checked as template parts; what they hold is checked once bound.
                        break;
                    default:
                        Error(DiagnosticCodes.ListContent, "A list item holds paragraphs and nested lists only. Put headings, tables and images between lists.", blockPath);
                        break;
                }
            }
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

        if (table.TableStyle is { } look)
        {
            if (look.RuleWidth is { } rule)
            {
                NotNegative(rule, $"{path}/tableStyle/ruleWidth");
            }

            if (look.CellPadding is { } padding)
            {
                NotNegative(padding, $"{path}/tableStyle/cellPadding");
            }
        }

        switch (table.Rows)
        {
            case InlineTableRows inline when !inline.Rows.IsDefault:
                var grid = new Rendering.TableGrid(table.Columns.Length);
                for (int r = 0; r < inline.Rows.Length; r++)
                {
                    ImmutableArray<Cell> row = inline.Rows[r];
                    (_, int free, int covered, int dropped) = grid.Place(row.IsDefault ? [] : row);
                    if (covered != free || dropped > 0)
                    {
                        string spans = free == table.Columns.Length ? "" : Invariant($" ({table.Columns.Length - free} are taken by row spans from above)");
                        Error(DiagnosticCodes.CellCount, Invariant($"Row {r} fills {covered} of the {free} columns it has{spans}; {(dropped > 0 ? Invariant($"{dropped} cells don't fit") : "every column needs a cell")}. Count each cell's columnSpan."), $"{path}/rows/{r}");
                    }

                    if (row.IsDefault)
                    {
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
                        if (row[c].ColumnSpan < 1 || row[c].RowSpan < 1)
                        {
                            Error(DiagnosticCodes.CellCount, "A cell's columnSpan and rowSpan are 1 or more.", $"{path}/rows/{r}/{c}");
                        }
                    }
                }

                break;

            case RepeatedTableRows repeated:
                if (string.IsNullOrWhiteSpace(repeated.Items))
                {
                    Error(DiagnosticCodes.BadTemplate, "Repeated rows need \"repeat\": the path of an array in the model.", $"{path}/rows/repeat");
                }

                if (repeated.Cells.IsDefaultOrEmpty)
                {
                    Error(DiagnosticCodes.BadTemplate, "Repeated rows need \"cells\": the cells of each row.", $"{path}/rows/cells");
                    break;
                }

                int filled = 0;
                for (int c = 0; c < repeated.Cells.Length; c++)
                {
                    if (repeated.Cells[c] is not { } cell)
                    {
                        Error(DiagnosticCodes.MissingProperty, "A cell is null. Use \"\" for an empty cell.", $"{path}/rows/cells/{c}");
                        continue;
                    }

                    Inlines(cell.Inlines, $"{path}/rows/cells/{c}/inlines");
                    filled += Math.Max(cell.ColumnSpan, 1);
                    if (cell.ColumnSpan < 1 || cell.RowSpan != 1)
                    {
                        Error(DiagnosticCodes.CellCount, "A repeated row's cells span 1 or more columns and exactly 1 row: each row is made on its own.", $"{path}/rows/cells/{c}");
                    }
                }

                if (filled != table.Columns.Length)
                {
                    Error(DiagnosticCodes.CellCount, Invariant($"The repeated row fills {filled} of the {table.Columns.Length} columns; every column needs a cell. Count each cell's columnSpan."), $"{path}/rows/cells");
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

    private void Link(Link link, string path)
    {
        string? where = _noLinksIn ?? (path.Contains("/rows/", StringComparison.Ordinal) ? "table cells"
            : path.StartsWith("/header", StringComparison.Ordinal) || path.StartsWith("/footer", StringComparison.Ordinal) || path.Contains("/header/", StringComparison.Ordinal) || path.Contains("/footer/", StringComparison.Ordinal) ? "headers and footers"
            : null);
        if (where is not null)
        {
            Error(DiagnosticCodes.BadLink, $"Links in {where} aren't supported yet; put the link in a paragraph.", path);
        }

        if (_inLink)
        {
            Error(DiagnosticCodes.BadLink, "A link can't hold another link.", path);
        }

        string target = link.Target ?? "";
        if (target.StartsWith('#'))
        {
            _internalLinks.Add((target[1..], $"{path}/link"));
        }
        else if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "http" or "mailto"))
        {
            Error(DiagnosticCodes.BadLink, $"\"{target}\" isn't a link target: use https://..., http://..., mailto:... or # and a block's id.", $"{path}/link");
        }

        if (link.Description is { } description && string.IsNullOrWhiteSpace(description))
        {
            Error(DiagnosticCodes.BadLink, "A link's description says what it does; leave it out to use the link's text.", $"{path}/description");
        }

        if (link.Inlines.IsDefaultOrEmpty)
        {
            Error(DiagnosticCodes.BadLink, "A link needs \"inlines\": the text to click.", $"{path}/inlines");
            return;
        }

        _inLink = true;
        Inlines(link.Inlines, $"{path}/inlines");
        _inLink = false;
    }

    private void Id(string id, string path)
    {
        if (!IdPattern().IsMatch(id))
        {
            Error(DiagnosticCodes.BadId, $"\"{id}\" isn't an id: use letters, digits, -, _, . and :, starting with a letter or digit, up to 128 characters.", $"{path}/id");
        }
        else if (_ids.TryGetValue(id, out string? first))
        {
            Error(DiagnosticCodes.BadId, $"The id \"{id}\" is also used at {first}; an id names one block.", $"{path}/id");
        }
        else if (!_anchorPaths.Contains(path))
        {
            Error(DiagnosticCodes.BadId, "Ids work on top-level blocks, and blocks in a top-level when, so far: a repeat would repeat the id, and blocks in lists, tables, boxes and bands have no destination yet.", $"{path}/id");
        }
        else
        {
            _ids[id] = path;
        }
    }

    private void TextBox(TextBox box, string path)
    {
        if (box.Resolve() is not { } place)
        {
            Error(DiagnosticCodes.BadTextBox, "A text box needs a place: a \"preset\" such as \"din5008B\", or \"x\", \"y\", \"width\" and \"height\".", path);
        }
        else if (place.Width <= 0 || place.Height <= 0)
        {
            Error(DiagnosticCodes.BadTextBox, "A text box's width and height are more than zero.", place.Width <= 0 ? $"{path}/width" : $"{path}/height");
        }
        else if (place.X < 0 || place.Y < 0 || place.X >= _page.Width || place.Y >= _page.Height)
        {
            Error(DiagnosticCodes.BadTextBox, Invariant($"The text box starts at {place.X:0.#} pt, {place.Y:0.#} pt, which isn't on the {_page.Width:0.#} × {_page.Height:0.#} pt page. x and y are measured from the paper's top-left corner."), place.X < 0 || place.X >= _page.Width ? $"{path}/x" : $"{path}/y");
        }
        else
        {
            if (place.X + place.Width > _page.Width + 0.01 || place.Y + place.Height > _page.Height + 0.01)
            {
                Warning(DiagnosticCodes.TextBoxOffPage, "The text box reaches past the edge of the page, so part of it can't be seen.", path);
            }

            if (place.Padding.Left.Points + place.Padding.Right.Points >= place.Width || place.Padding.Top.Points + place.Padding.Bottom.Points >= place.Height)
            {
                Error(DiagnosticCodes.BadTextBox, "The text box's padding leaves no room for text.", $"{path}/padding");
            }
        }

        if (box.MinSize is { } min && min.Points <= 0)
        {
            Error(DiagnosticCodes.BadTextBox, "A text box's minSize is more than zero.", $"{path}/minSize");
        }

        if (box.BorderWidth is { } border)
        {
            NotNegative(border, $"{path}/borderWidth");
        }

        if (box.Content.IsDefaultOrEmpty)
        {
            Error(DiagnosticCodes.BadTextBox, "A text box needs \"content\": its paragraphs.", $"{path}/content");
            return;
        }

        _noLinksIn = "text boxes";
        for (int b = 0; b < box.Content.Length; b++)
        {
            string blockPath = $"{path}/content/{b}";
            if (box.Content[b] is not (Paragraph or Repeat or Conditional or null))
            {
                Error(DiagnosticCodes.BadTextBox, "A text box holds paragraphs only (or repeats and whens that make them).", blockPath);
                continue;
            }

            Block(box.Content[b], blockPath);
        }

        _noLinksIn = null;
    }

    private void Chart(Chart chart, string path)
    {
        if (!chart.Decorative && string.IsNullOrWhiteSpace(chart.Alt))
        {
            Error(DiagnosticCodes.NoAltText, "A chart needs \"alt\" text that says what it shows (its point, not every number), or \"decorative\": true.", $"{path}/alt");
        }

        bool inline = !chart.Categories.IsDefaultOrEmpty || !chart.Series.IsDefaultOrEmpty;
        if (inline == (chart.Bind is not null))
        {
            Error(DiagnosticCodes.BadChart, "A chart needs \"categories\" and \"series\", or \"bind\": one of them.", path);
        }
        else if (inline)
        {
            ChartData(chart, path, (code, message, at) => Error(code, message, at));
        }

        if (chart.Minimum is { } min && chart.Maximum is { } max && !(max > min))
        {
            Error(DiagnosticCodes.BadChart, "A chart's maximum is above its minimum.", $"{path}/maximum");
        }

        foreach ((Length? length, string name) in new[] { (chart.Width, "width"), (chart.Height, "height") })
        {
            if (length is { } l)
            {
                Positive(l, $"{path}/{name}");
            }
        }
    }

    /// <summary>Checks a chart's categories and series: also used for data bound from a template's model.</summary>
    internal static void ChartData(Chart chart, string path, Action<string, string, string> error)
    {
        if (chart.Categories.IsDefaultOrEmpty || chart.Categories.Length > 200)
        {
            error(DiagnosticCodes.BadChart, "A chart has 1 to 200 categories.", $"{path}/categories");
        }

        if (chart.Series.IsDefaultOrEmpty || chart.Series.Length > 12)
        {
            error(DiagnosticCodes.BadChart, "A chart has 1 to 12 series.", $"{path}/series");
            return;
        }

        if (chart.Kind == ChartKind.Pie && chart.Series.Length != 1)
        {
            error(DiagnosticCodes.BadChart, "A pie chart has one series: its slices are the categories.", $"{path}/series");
        }

        if (chart.Horizontal && chart.Kind is ChartKind.Line or ChartKind.Pie)
        {
            error(DiagnosticCodes.BadChart, "Only bar charts lie down: \"horizontal\" is for bar and stackedBar.", $"{path}/horizontal");
        }

        int categories = chart.Categories.IsDefault ? 0 : chart.Categories.Length;
        for (int s = 0; s < chart.Series.Length; s++)
        {
            ChartSeries? series = chart.Series[s];
            string at = Invariant($"{path}/series/{s}");
            if (series is null || series.Values.IsDefault || series.Values.Length != categories)
            {
                error(DiagnosticCodes.BadChart, Invariant($"A series has one value per category: {categories}."), $"{at}/values");
            }
            else if (System.Linq.Enumerable.Any(series.Values, v => !double.IsFinite(v)) || (chart.Kind is ChartKind.StackedBar or ChartKind.Pie && System.Linq.Enumerable.Any(series.Values, v => v < 0)))
            {
                error(DiagnosticCodes.BadChart, chart.Kind is ChartKind.StackedBar or ChartKind.Pie ? "A stacked or pie chart's values are numbers of 0 or more." : "A chart's values are finite numbers.", $"{at}/values");
            }
            else if (chart.Kind == ChartKind.Pie && System.Linq.Enumerable.Sum(series.Values) <= 0)
            {
                error(DiagnosticCodes.BadChart, "A pie chart's values add up to more than 0.", $"{at}/values");
            }
        }
    }

    private void SignatureField(SignatureField field, string path)
    {
        if (!ValidFieldName(field.Name))
        {
            Error(DiagnosticCodes.BadSignatureField, "A signature field needs a \"name\" of letters, digits, '-' and '_' (up to 64), such as \"approver\".", $"{path}/name");
        }
        else if (!_signatureFields.Add(field.Name))
        {
            Error(DiagnosticCodes.BadSignatureField, $"There's already a signature field named \"{field.Name}\"; each needs its own name.", $"{path}/name");
        }

        foreach ((Length? length, string name) in new[] { (field.Width, "width"), (field.Height, "height") })
        {
            if (length is { } l)
            {
                Positive(l, $"{path}/{name}");
            }
        }
    }

    /// <summary>A signature field's name: letters, digits, '-' and '_', so it's a whole field name (no '.' for nesting).</summary>
    internal static bool ValidFieldName(string? name) =>
        name is { Length: > 0 and <= 64 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private void Barcode(Barcode barcode, string path)
    {
        if (!barcode.Decorative && string.IsNullOrWhiteSpace(barcode.Alt))
        {
            Error(DiagnosticCodes.NoAltText, "A barcode needs \"alt\" text that says what it's for (\"Payment QR code for invoice 42\"), or \"decorative\": true.", $"{path}/alt");
        }

        if ((barcode.Data is null) == (barcode.Bind is null))
        {
            Error(DiagnosticCodes.BadBarcode, "A barcode needs \"data\" or \"bind\": one of them.", path);
        }
        else if (barcode.Data is { } data)
        {
            try
            {
                _ = Rendering.RenderEngine.EncodeBarcode(barcode);
            }
            catch (ArgumentException ex)
            {
                Error(DiagnosticCodes.BadBarcode, ex.Message.Split(" (Parameter", 2)[0], $"{path}/data");
            }

            _ = data;
        }

        foreach ((Length? length, string name) in new[] { (barcode.Size, "size"), (barcode.Width, "width"), (barcode.Height, "height") })
        {
            if (length is { } l)
            {
                Positive(l, $"{path}/{name}");
            }
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
                case Link link:
                    Link(link, $"{path}/{i}");
                    break;
                case Field when _inLink:
                    Error(DiagnosticCodes.BadLink, "A link holds text and line breaks, not page number fields.", $"{path}/{i}");
                    break;
                case Bind bind:
                    if (string.IsNullOrWhiteSpace(bind.Path))
                    {
                        Error(DiagnosticCodes.BadTemplate, "A binding needs a path, such as \"customer.name\".", $"{path}/{i}/bind");
                    }

                    if (bind.Style is { } bound)
                    {
                        Style(bound, $"{path}/{i}/style");
                    }

                    break;
            }
        }
    }

    private void Style(TextStyle style, string path)
    {
        if (style.Language is { } language && !LanguageTag().IsMatch(language))
        {
            Error(DiagnosticCodes.BadLanguage, $"\"{language}\" isn't a language tag. Use a BCP 47 tag such as \"de\", \"nb-NO\" or \"fr-CA\".", $"{path}/language");
        }

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

    private readonly HashSet<string> _signatureFields = new(StringComparer.Ordinal);

    private void Error(string code, string message, string path) => _diagnostics.Add(new PdfDiagnostic(code, DiagnosticSeverity.Error, message, path));

    private void Warning(string code, string message, string path) => _diagnostics.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message, path));

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{1,8})*$")]
    private static partial Regex LanguageTag();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$")]
    private static partial Regex IdPattern();

    /// <summary>The largest start a section's numbering may have: 260 (zzzzzzzzzz) for letters, a billion for the rest.</summary>
    internal static int MaxStartAt(PageNumberStyle? numbering) =>
        numbering is PageNumberStyle.LowerLetter or PageNumberStyle.UpperLetter ? 260 : 1_000_000_000;
}
