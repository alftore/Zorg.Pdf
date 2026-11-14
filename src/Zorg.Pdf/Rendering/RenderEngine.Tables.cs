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
/// Rows never split across pages. The header row stays with the first body row and, on later pages, repeats as a
/// pagination artifact, so a screen reader hears it once. Rules and the header fill are layout artifacts.
/// </summary>
internal sealed partial class RenderEngine
{
    /// <summary>Most warnings of one kind reported per table, so a million bad rows don't make a million diagnostics.</summary>
    private const int MaxRowWarnings = 10;

    private async ValueTask PlaceTableAsync(int index, Table table, CancellationToken cancellationToken)
    {
        string path = $"/content/{index}";
        TableStyle look = TableStyle.Default.Merge(table.TableStyle);
        double padding = look.CellPadding?.Points ?? 3;
        double[] widths = ColumnWidths(table, path);
        TextStyle headerStyle = _styles.TableHeader();
        TextStyle bodyStyle = _styles.TableCell(table);

        ImmutableArray<Cell> headerCells = [.. table.Columns.Select(c => new Cell(c.Header) { Align = c.Align })];
        TableRow header = LayoutRow(headerCells, table, headerStyle, widths, padding, bound: false, $"{path}/columns", 0);
        var warnings = new int[3];

        IAsyncEnumerator<IReadOnlyList<Cell>> rows = RowSource(table).GetAsyncEnumerator(cancellationToken);
        await using (rows.ConfigureAwait(false))
        {
            bool bound = table.Rows is BoundTableRows;
            int rowIndex = 0;
            TableRow? next = await NextRowAsync().ConfigureAwait(false);

            // Keep the header with the first row, unless together they don't fit even an empty page.
            double needed = header.Height + (next?.Height ?? 0);
            if (!_pageEmpty && _y - needed < _geometry.Bottom && needed <= _geometry.ContentHeight)
            {
                await NewPageAsync(cancellationToken).ConfigureAwait(false);
            }

            Tagger.Element element = _tagger.Begin(StructureRole.Table);
            DrawRow(header, element, look, isHeader: true, tagged: true);
            while (next is { } row)
            {
                if (row.Height > _geometry.ContentHeight && warnings[0]++ < MaxRowWarnings)
                {
                    _sink.Warning(DiagnosticCodes.RowTooTall, string.Create(CultureInfo.InvariantCulture, $"The row is {row.Height:0.#} pt tall, more than the {_geometry.ContentHeight:0.#} pt a page has, so it runs past the bottom margin. Rows don't split across pages yet."), row.Path, _page + 1);
                }

                if (_y - row.Height < _geometry.Bottom && !_pageEmpty)
                {
                    await NewPageAsync(cancellationToken).ConfigureAwait(false);
                    if (table.RepeatHeader)
                    {
                        DrawRow(header, element, look, isHeader: true, tagged: false);
                    }
                }

                DrawRow(row, element, look, isHeader: false, tagged: true);
                await _writer.FlushIfNeededAsync(cancellationToken).ConfigureAwait(false);
                next = await NextRowAsync().ConfigureAwait(false);
            }

            _tagger.End(element, _documentWriter.Pages);
            _y -= (bodyStyle.Size?.Points ?? 10) * 0.8;

            async ValueTask<TableRow?> NextRowAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await rows.MoveNextAsync().ConfigureAwait(false))
                {
                    return null;
                }

                if (rowIndex >= _options.Limits.MaxTableRows)
                {
                    throw new PdfLimitException(string.Create(CultureInfo.InvariantCulture, $"The table at {path} has more than {_options.Limits.MaxTableRows:N0} rows (RenderOptions.Limits.MaxTableRows). The output has the pages written so far and isn't a complete file."));
                }

                IReadOnlyList<Cell> cells = rows.Current ?? [];
                string rowPath = $"{path}/rows/{rowIndex}";
                if (cells.Count != widths.Length)
                {
                    if (warnings[1]++ < MaxRowWarnings)
                    {
                        _sink.Warning(DiagnosticCodes.RowCellCount, string.Create(CultureInfo.InvariantCulture, $"Row {rowIndex} has {cells.Count} cells; the table has {widths.Length} columns, so it's {(cells.Count < widths.Length ? "padded with empty cells" : "cut")}."), rowPath);
                    }

                    cells = [.. cells.Take(widths.Length), .. Enumerable.Repeat(new Cell(""), Math.Max(0, widths.Length - cells.Count))];
                }

                return LayoutRow([.. cells.Select(c => c ?? new Cell(""))], table, bodyStyle, widths, padding, bound, rowPath, rowIndex++);
            }
        }
    }

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

    /// <summary>Lays out one row's cells at their column widths; the row is as tall as its tallest cell.</summary>
    private TableRow LayoutRow(ImmutableArray<Cell> cells, Table table, TextStyle style, double[] widths, double padding, bool bound, string path, int index)
    {
        var laidOut = new LayoutLine[cells.Length][];
        double height = 0;
        for (int c = 0; c < cells.Length; c++)
        {
            if (bound)
            {
                WarnMissingGlyphs(cells[c].Inlines, style, $"{path}/{c}");
            }

            TextAlign align = cells[c].Align ?? table.Columns[c].Align;
            List<TextSegment> segments = Segments(cells[c].Inlines.IsDefault ? [] : cells[c].Inlines, style, out _);
            ParagraphLayout layout = ParagraphLayout.Create(segments, Math.Max(1, widths[c] - (2 * padding)), align, style.LineHeight ?? 1.2);
            foreach (TextDiagnostic diagnostic in layout.Diagnostics)
            {
                _sink.Warning(diagnostic.Code, "A word in the cell is wider than its column, so it was broken between characters. Widen the column or shorten the word.", $"{path}/{c}");
            }

            laidOut[c] = [.. layout.Lines];
            height = Math.Max(height, laidOut[c].Sum(l => l.Height));
        }

        return new TableRow(laidOut, widths, height + (2 * padding), padding, path, index);
    }

    /// <summary>Text from a data slot is only seen now; a character no font has is a warning, as it can't stop a render that has started.</summary>
    private void WarnMissingGlyphs(ImmutableArray<Inline> inlines, TextStyle style, string path)
    {
        if (inlines.IsDefault)
        {
            return;
        }

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

    /// <summary>Draws a row at the current position: background and rule as artifacts, then each cell's text.</summary>
    private void DrawRow(TableRow row, Tagger.Element table, TableStyle look, bool isHeader, bool tagged)
    {
        double top = _y;
        double left = _geometry.Left;
        double width = row.Widths.Sum();
        double rule = (look.RuleWidth?.Points ?? 0.3) * (isHeader ? 2.5 : 1);
        if ((isHeader && look.HeaderFill is not null) || rule > 0)
        {
            _content.BeginArtifact("Layout"u8, []).SaveState();
            if (isHeader && look.HeaderFill is { } fill)
            {
                _content.SetFillRgb(fill.R / 255.0, fill.G / 255.0, fill.B / 255.0).FillRectangle(left, top - row.Height, width, row.Height);
            }

            if (rule > 0 && look.RuleColor is { } color)
            {
                _content.SetLineWidth(rule).SetStrokeRgb(color.R / 255.0, color.G / 255.0, color.B / 255.0).StrokeLine(left, top - row.Height, left + width, top - row.Height);
            }

            _content.RestoreState().EndMarkedContent();
            (_stroke, _strokeWidth) = (null, 1); // Restored: what was set inside q/Q is gone.
        }

        Tagger.Element? element = tagged ? _tagger.Begin(StructureRole.TR, table) : null;
        double x = left;
        for (int c = 0; c < row.Cells.Length; c++)
        {
            Tagger.Element? cell = element is null ? null : _tagger.Begin(isHeader ? StructureRole.TH : StructureRole.TD, element);
            LayoutLine[] lines = row.Cells[c];
            if (lines.Any(l => l.Runs.Count > 0))
            {
                if (cell is null)
                {
                    _content.BeginArtifact("Pagination"u8, []); // A repeated header row: the first one is the tagged one.
                }
                else
                {
                    _content.BeginMarkedContent(Tagger.Name(isHeader ? StructureRole.TH : StructureRole.TD), _tagger.Mark(cell, _page));
                }

                BeginText();
                double lineTop = top - row.Padding;
                foreach (LayoutLine line in lines)
                {
                    if (line.Runs.Count > 0)
                    {
                        DrawRuns(line, x + row.Padding + line.Offset, lineTop - line.Ascent);
                    }

                    lineTop -= line.Height;
                }

                EndText();
            }

            if (cell is not null)
            {
                _tagger.End(cell, _documentWriter.Pages);
            }

            x += row.Widths[c];
        }

        if (element is not null)
        {
            _tagger.End(element, _documentWriter.Pages);
        }

        _y -= row.Height;
        _pageEmpty = false;
    }

    /// <summary>A laid-out row: each cell's lines, the column widths, its height with padding, and where it is in the document.</summary>
    private sealed record TableRow(LayoutLine[][] Cells, double[] Widths, double Height, double Padding, string Path, int Index);
}
