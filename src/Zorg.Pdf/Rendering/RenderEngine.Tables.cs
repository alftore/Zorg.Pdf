using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Layout;
using Zorg.Pdf.Validation;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// Tables: a header row of column titles, then body rows from the document or from a data slot, read one at a time.
/// Cells can span columns and rows; rows joined by a row span form a group that stays on one page. A row that fits
/// on a page never splits; a row taller than a page splits line by line, each cell tagged once across the pages. The
/// header row stays with the first body row and, on later pages, repeats as a pagination artifact, so a screen reader
/// hears it once. Rules and the header fill are layout artifacts.
/// </summary>
internal sealed partial class RenderEngine
{
    /// <summary>Most warnings of one kind reported per table, so a million bad rows don't make a million diagnostics.</summary>
    private const int MaxRowWarnings = 10;

    private async ValueTask PlaceTableAsync(int index, Table table, CancellationToken cancellationToken)
    {
        string path = $"/content/{index}";
        var tableLayout = new TableLayout(table, TableStyle.Default.Merge(table.TableStyle), ColumnWidths(table, path));
        TextStyle headerStyle = _styles.TableHeader();
        TextStyle bodyStyle = _styles.TableCell(table);

        List<GridCell> headerCells = [.. table.Columns.Select((c, i) => new GridCell(new Cell(c.Header) { Align = c.Align }, i, 1, 1, i))];
        TableRow header = LayoutRow(headerCells, tableLayout, headerStyle, bound: false, $"{path}/columns", 0);
        header.Height = header.BaseHeight;
        var warnings = new int[3];
        var grid = new TableGrid(tableLayout.Widths.Length);

        IAsyncEnumerator<IReadOnlyList<Cell>> rows = RowSource(table).GetAsyncEnumerator(cancellationToken);
        await using (rows.ConfigureAwait(false))
        {
            bool bound = table.Rows is BoundTableRows;
            int rowIndex = 0;
            List<TableRow>? group = await NextGroupAsync().ConfigureAwait(false);
            double room = _geometry.ContentHeight - (table.RepeatHeader ? header.Height : 0);

            // Keep the header with the first group (or the first lines of a row that splits), unless together they
            // don't fit even an empty page.
            double needed = header.Height + (group is null ? 0 : Math.Min(GroupHeight(group), FirstPartHeight(group[0], tableLayout)));
            if (!_pageEmpty && _y - needed < _geometry.Bottom && needed <= _geometry.ContentHeight)
            {
                await NewPageAsync(cancellationToken).ConfigureAwait(false);
            }

            Tagger.Element element = _tagger.Begin(StructureRole.Table);
            DrawGroup([header], element, tableLayout, isHeader: true, tagged: true);
            while (group is { } current)
            {
                double height = GroupHeight(current);
                bool splits = current.Count == 1 && height > room;
                if (_y - height < _geometry.Bottom && !_pageEmpty && (!splits || _y - FirstPartHeight(current[0], tableLayout) < _geometry.Bottom))
                {
                    await NewTablePageAsync().ConfigureAwait(false);
                }

                if (splits)
                {
                    await DrawSplitRowAsync(current[0], element, tableLayout, NewTablePageAsync).ConfigureAwait(false);
                }
                else
                {
                    if (height > room && warnings[0]++ < MaxRowWarnings)
                    {
                        _sink.Warning(DiagnosticCodes.RowTooTall, string.Create(CultureInfo.InvariantCulture, $"The {current.Count} rows joined by a row span are {height:0.#} pt tall together, more than the {room:0.#} pt a page has, so they run past the bottom margin. Rows joined by a span don't split across pages."), current[0].Path, _page + 1);
                    }

                    DrawGroup(current, element, tableLayout, isHeader: false, tagged: true);
                }

                await _writer.FlushIfNeededAsync(cancellationToken).ConfigureAwait(false);
                group = await NextGroupAsync().ConfigureAwait(false);
            }

            _tagger.End(element, _documentWriter.Pages);
            _y -= (bodyStyle.Size?.Points ?? 10) * 0.8;

            async ValueTask NewTablePageAsync()
            {
                await NewPageAsync(cancellationToken).ConfigureAwait(false);
                if (table.RepeatHeader)
                {
                    DrawGroup([header], element, tableLayout, isHeader: true, tagged: false);
                }
            }

            // The next row, with the rows a row span joins to it.
            async ValueTask<List<TableRow>?> NextGroupAsync()
            {
                List<TableRow>? rowsInGroup = null;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await rows.MoveNextAsync().ConfigureAwait(false))
                    {
                        grid.Close();
                        return rowsInGroup is null ? null : Resolve(rowsInGroup, tableLayout);
                    }

                    if (rowIndex >= _options.Limits.MaxTableRows)
                    {
                        throw new PdfLimitException(string.Create(CultureInfo.InvariantCulture, $"The table at {path} has more than {_options.Limits.MaxTableRows:N0} rows (RenderOptions.Limits.MaxTableRows). The output has the pages written so far and isn't a complete file."));
                    }

                    IReadOnlyList<Cell> cells = rows.Current ?? [];
                    string rowPath = $"{path}/rows/{rowIndex}";
                    List<int> fromAbove = grid.CoveredFromAbove();
                    (List<GridCell> placed, int free, int covered, int dropped) = grid.Place(cells);
                    if ((covered != free || dropped > 0) && warnings[1]++ < MaxRowWarnings)
                    {
                        _sink.Warning(DiagnosticCodes.RowCellCount, string.Create(CultureInfo.InvariantCulture, $"Row {rowIndex} fills {covered} of the {free} columns it has, so it's {(dropped > 0 ? $"cut by {dropped} cells" : "padded with empty cells")}."), rowPath);
                    }

                    foreach (int gap in TableGrid.Gaps(tableLayout.Widths.Length, placed, fromAbove))
                    {
                        placed.Add(new GridCell(new Cell(""), gap, 1, 1, -1));
                    }

                    placed.Sort((a, b) => a.Column.CompareTo(b.Column));
                    (rowsInGroup ??= []).Add(LayoutRow(placed, tableLayout, bodyStyle, bound, rowPath, rowIndex++));
                    if (!grid.IsOpen)
                    {
                        return Resolve(rowsInGroup, tableLayout);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Settles a group's row heights: spans that run past the group's last row stop there, and a spanning cell taller
    /// than the rows it covers makes its last row taller.
    /// </summary>
    private static List<TableRow> Resolve(List<TableRow> group, TableLayout table)
    {
        foreach (TableRow row in group)
        {
            row.Height = row.BaseHeight;
        }

        for (int r = 0; r < group.Count; r++)
        {
            foreach (TableCellLayout cell in group[r].Cells)
            {
                cell.Grid.RowSpan = Math.Min(cell.Grid.RowSpan, group.Count - r);
                if (cell.Grid.RowSpan > 1)
                {
                    double have = 0;
                    for (int i = r; i < r + cell.Grid.RowSpan; i++)
                    {
                        have += group[i].Height;
                    }

                    double need = cell.ContentHeight + (2 * table.Padding);
                    if (need > have)
                    {
                        group[r + cell.Grid.RowSpan - 1].Height += need - have;
                    }
                }
            }
        }

        return group;
    }

    private static double GroupHeight(List<TableRow> group) => group.Sum(r => r.Height);

    /// <summary>The least a row needs to start on a page: padding and each cell's first line.</summary>
    private static double FirstPartHeight(TableRow row, TableLayout table) =>
        (2 * table.Padding) + row.Cells.Max(c => c.Lines.Length > 0 ? c.Lines[0].Height : 0);

    /// <summary>Column widths in points: fixed columns first, the rest shared by the star columns.</summary>
    private double[] ColumnWidths(Table table, string path)
    {
        double available = _geometry.ContentWidth;
        double fixedTotal = table.Columns.Where(c => !c.Width.IsStar).Sum(c => c.Width.Value);
        double shares = table.Columns.Where(c => c.Width.IsStar).Sum(c => c.Width.Value);
        double scale = 1;
        if (fixedTotal > available)
        {
            scale = available / fixedTotal;
            _sink.Warning(DiagnosticCodes.ColumnsTooWide, string.Create(CultureInfo.InvariantCulture, $"The fixed columns add up to {fixedTotal:0.#} pt, more than the {available:0.#} pt the page has, so they're scaled to fit and the share (*) columns get nothing."), $"{path}/columns");
        }

        double free = Math.Max(0, available - (fixedTotal * scale));
        return [.. table.Columns.Select(c => Math.Max(1, c.Width.IsStar ? (shares > 0 ? free * c.Width.Value / shares : 0) : c.Width.Value * scale))];
    }

    /// <summary>Where a table's rows come from: the document, or a data slot (Preflight made sure it exists).</summary>
    private IAsyncEnumerable<IReadOnlyList<Cell>> RowSource(Table table) => table.Rows switch
    {
        InlineTableRows inline => InlineRows(inline.Rows.IsDefault ? [] : inline.Rows),
        BoundTableRows bound => _options.Data.Rows[bound.Slot].Rows,
        _ => InlineRows([]),
    };

    private static async IAsyncEnumerable<IReadOnlyList<Cell>> InlineRows(ImmutableArray<ImmutableArray<Cell>> rows)
    {
        await Task.CompletedTask.ConfigureAwait(false); // Completes synchronously, so a sync render never waits.
        foreach (ImmutableArray<Cell> row in rows)
        {
            yield return row;
        }
    }

    /// <summary>Lays out one row's cells at the width of the columns they cover.</summary>
    private TableRow LayoutRow(List<GridCell> placed, TableLayout table, TextStyle style, bool bound, string path, int index)
    {
        var cells = new List<TableCellLayout>(placed.Count);
        double height = 0;
        bool any = false;
        foreach (GridCell grid in placed)
        {
            string cellPath = grid.Index >= 0 ? $"{path}/{grid.Index}" : path;
            ImmutableArray<Inline> inlines = grid.Cell.Inlines.IsDefault ? [] : grid.Cell.Inlines;
            if (bound)
            {
                WarnMissingGlyphs(inlines, style, cellPath);
            }

            double width = table.Width(grid.Column, grid.ColumnSpan);
            TextAlign align = grid.Cell.Align ?? table.Source.Columns[grid.Column].Align;
            List<TextSegment> segments = Segments(inlines, style, out _);
            ParagraphLayout layout = ParagraphLayout.Create(segments, Math.Max(1, width - (2 * table.Padding)), align, style.LineHeight ?? 1.2);
            foreach (TextDiagnostic diagnostic in layout.Diagnostics)
            {
                _sink.Warning(diagnostic.Code, "A word in the cell is wider than its column, so it was broken between characters. Widen the column or shorten the word.", cellPath);
            }

            var cell = new TableCellLayout(grid, [.. layout.Lines]);
            cells.Add(cell);
            if (grid.RowSpan == 1)
            {
                height = Math.Max(height, cell.ContentHeight);
                any = true;
            }
        }

        // A row of nothing but cells spanning down is still a line tall.
        double empty = (style.Size?.Points ?? 10) * (style.LineHeight ?? 1.2);
        return new TableRow(cells, (any ? height : empty) + (2 * table.Padding), path, index);
    }

    /// <summary>Text from a data slot is only seen now; a character no font has is a warning, as it can't stop a render that has started.</summary>
    private void WarnMissingGlyphs(ImmutableArray<Inline> inlines, TextStyle style, string path)
    {
        foreach (Inline inline in inlines)
        {
            if (inline is not TextRun run || Preflight.Face(Preflight.RunStyle(style, run), _styles, _defaultFamily) is not { } face || !_fonts.TryGet(face, out FontChain chain))
            {
                continue;
            }

            int at = 0;
            foreach (Rune rune in run.Text.EnumerateRunes())
            {
                if (rune.Value >= 0x20 && !chain.Covers(rune.Value) && _missingInData.Add(rune.Value))
                {
                    _sink.Warning(DiagnosticCodes.MissingGlyph, Preflight.MissingGlyphMessage(rune, at, chain), path);
                }

                at += rune.Utf16SequenceLength;
            }
        }
    }

    /// <summary>
    /// Draws whole rows from the current position: the header fill, each cell's text at the top of the rows it spans,
    /// and a rule under each row where no span continues below it.
    /// </summary>
    private void DrawGroup(List<TableRow> group, Tagger.Element table, TableLayout layout, bool isHeader, bool tagged)
    {
        if (isHeader && layout.Look.HeaderFill is { } fill)
        {
            _content.BeginArtifact("Layout"u8, []).SaveState()
                .SetFillRgb(fill.R / 255.0, fill.G / 255.0, fill.B / 255.0)
                .FillRectangle(_geometry.Left, _y - GroupHeight(group), layout.Widths.Sum(), GroupHeight(group))
                .RestoreState().EndMarkedContent();
        }

        if (!isHeader && layout.Look.StripeFill is { } stripe && group.Exists(row => row.Index % 2 == 1))
        {
            _content.BeginArtifact("Layout"u8, []).SaveState().SetFillRgb(stripe.R / 255.0, stripe.G / 255.0, stripe.B / 255.0);
            double top = _y;
            foreach (TableRow row in group)
            {
                if (row.Index % 2 == 1)
                {
                    _content.FillRectangle(_geometry.Left, top - row.Height, layout.Widths.Sum(), row.Height);
                }

                top -= row.Height;
            }

            _content.RestoreState().EndMarkedContent();
        }

        // Which columns a rule may cross under each row: not one a cell spans down past it.
        bool[][] open = [.. group.Select(_ => new bool[layout.Widths.Length])];
        for (int r = 0; r < group.Count; r++)
        {
            foreach (TableCellLayout cell in group[r].Cells)
            {
                for (int below = r; below < r + cell.Grid.RowSpan - 1; below++)
                {
                    for (int c = cell.Grid.Column; c < cell.Grid.Column + cell.Grid.ColumnSpan; c++)
                    {
                        open[below][c] = true;
                    }
                }
            }
        }

        for (int r = 0; r < group.Count; r++)
        {
            TableRow row = group[r];
            Tagger.Element? element = tagged ? _tagger.Begin(StructureRole.TR, table) : null;
            foreach (TableCellLayout cell in row.Cells)
            {
                Tagger.Element? tag = element is null ? null : BeginCell(element, cell, isHeader);
                DrawCellLines(cell, layout, _y, cell.Lines.Length, isHeader, tag);
                if (tag is not null)
                {
                    _tagger.End(tag, _documentWriter.Pages);
                }
            }

            if (element is not null)
            {
                _tagger.End(element, _documentWriter.Pages);
            }

            _y -= row.Height;
            bool[] continues = open[r];
            DrawRule(layout, isHeader, c => !continues[c]);
            _pageEmpty = false;
        }
    }

    /// <summary>
    /// Draws a row taller than a page: as many lines of each cell as fit, then a new page (with the header) and the
    /// rest. Each cell is one TD across the pages; the rule goes under the last part only.
    /// </summary>
    private async ValueTask DrawSplitRowAsync(TableRow row, Tagger.Element table, TableLayout layout, Func<ValueTask> newPage)
    {
        Tagger.Element element = _tagger.Begin(StructureRole.TR, table);
        Tagger.Element[] tags = [.. row.Cells.Select(cell => BeginCell(element, cell, isHeader: false))];
        while (true)
        {
            double available = _y - _geometry.Bottom - (2 * layout.Padding);
            double used = 0;
            bool done = true;
            for (int i = 0; i < row.Cells.Count; i++)
            {
                TableCellLayout cell = row.Cells[i];
                int end = cell.Next;
                double height = 0;
                while (end < cell.Lines.Length && height + cell.Lines[end].Height <= available)
                {
                    height += cell.Lines[end++].Height;
                }

                if (end == cell.Next && end < cell.Lines.Length && _pageEmpty)
                {
                    height += cell.Lines[end++].Height; // A line taller than an empty page: drawn anyway, so the row ends.
                }

                DrawCellLines(cell, layout, _y, end, isHeader: false, tags[i]);
                cell.Next = end;
                used = Math.Max(used, height);
                done &= end >= cell.Lines.Length;
            }

            _y -= used + (2 * layout.Padding);
            _pageEmpty = false;
            if (done)
            {
                DrawRule(layout, isHeader: false, _ => true);
                break;
            }

            await newPage().ConfigureAwait(false);
        }

        foreach (Tagger.Element tag in tags)
        {
            _tagger.End(tag, _documentWriter.Pages);
        }

        _tagger.End(element, _documentWriter.Pages);
    }

    private Tagger.Element BeginCell(Tagger.Element row, TableCellLayout cell, bool isHeader)
    {
        Tagger.Element tag = _tagger.Begin(isHeader ? StructureRole.TH : StructureRole.TD, row);
        (tag.ColumnSpan, tag.RowSpan) = (cell.Grid.ColumnSpan, cell.Grid.RowSpan);
        return tag;
    }

    /// <summary>Draws a cell's lines from where it got to up to <paramref name="end"/>, below <paramref name="top"/>.</summary>
    private void DrawCellLines(TableCellLayout cell, TableLayout layout, double top, int end, bool isHeader, Tagger.Element? tag)
    {
        int start = cell.Next;
        if (!cell.Lines.AsSpan(start, end - start).ToArray().Any(l => l.Runs.Count > 0))
        {
            return;
        }

        if (tag is null)
        {
            _content.BeginArtifact("Pagination"u8, []); // A repeated header row: the first one is the tagged one.
        }
        else
        {
            _content.BeginMarkedContent(Tagger.Name(isHeader ? StructureRole.TH : StructureRole.TD), _tagger.Mark(tag, _page));
        }

        BeginText();
        double x = layout.Left(_geometry.Left, cell.Grid.Column) + layout.Padding;
        double lineTop = top - layout.Padding;
        for (int i = start; i < end; i++)
        {
            LayoutLine line = cell.Lines[i];
            if (line.Runs.Count > 0)
            {
                DrawRuns(line, x + line.Offset, lineTop - line.Ascent);
            }

            lineTop -= line.Height;
        }

        EndText();
    }

    /// <summary>A rule at the current position under the columns <paramref name="crosses"/> allows, joined into segments.</summary>
    private void DrawRule(TableLayout layout, bool isHeader, Func<int, bool> crosses)
    {
        double width = (layout.Look.RuleWidth?.Points ?? 0.3) * (isHeader ? 2.5 : 1);
        if (width <= 0 || layout.Look.RuleColor is not { } color)
        {
            return;
        }

        _content.BeginArtifact("Layout"u8, []).SaveState().SetLineWidth(width).SetStrokeRgb(color.R / 255.0, color.G / 255.0, color.B / 255.0);
        int c = 0;
        while (c < layout.Widths.Length)
        {
            if (!crosses(c))
            {
                c++;
                continue;
            }

            int from = c;
            while (c < layout.Widths.Length && crosses(c))
            {
                c++;
            }

            double left = layout.Left(_geometry.Left, from);
            _content.StrokeLine(left, _y, left + layout.Width(from, c - from), _y);
        }

        _content.RestoreState().EndMarkedContent();
        (_stroke, _strokeWidth) = (null, 1); // Restored: what was set inside q/Q is gone.
    }

    /// <summary>A table's look and column widths, and where each column starts.</summary>
    private sealed class TableLayout(Table source, TableStyle look, double[] widths)
    {
        public Table Source { get; } = source;

        public TableStyle Look { get; } = look;

        public double[] Widths { get; } = widths;

        public double Padding { get; } = look.CellPadding?.Points ?? 3;

        public double Left(double tableLeft, int column) => tableLeft + Width(0, column);

        public double Width(int column, int span)
        {
            double width = 0;
            for (int c = column; c < column + span; c++)
            {
                width += Widths[c];
            }

            return width;
        }
    }

    /// <summary>A cell laid out at the width it covers, and how far a split row has drawn it.</summary>
    private sealed class TableCellLayout(GridCell grid, LayoutLine[] lines)
    {
        public GridCell Grid { get; } = grid;

        public LayoutLine[] Lines { get; } = lines;

        public double ContentHeight { get; } = lines.Sum(l => l.Height);

        public int Next { get; set; }
    }

    /// <summary>
    /// A laid-out row: its cells, its height without spans (<see cref="BaseHeight"/>) and once its group is settled
    /// (<see cref="Height"/>), and where it is in the document.
    /// </summary>
    private sealed class TableRow(List<TableCellLayout> cells, double baseHeight, string path, int index)
    {
        public List<TableCellLayout> Cells { get; } = cells;

        public double BaseHeight { get; } = baseHeight;

        public double Height { get; set; }

        public string Path { get; } = path;

        public int Index { get; } = index;
    }
}
