using System;
using System.Collections.Generic;

namespace Zorg.Pdf.Rendering;

/// <summary>A cell placed in the grid: its first column, how many columns and rows it covers, and its index in the row.</summary>
internal sealed record GridCell(Cell Cell, int Column, int ColumnSpan, int RowSpan, int Index)
{
    /// <summary>Shortened when the table ends before the span does.</summary>
    public int RowSpan { get; set; } = RowSpan;
}

/// <summary>
/// Places each row's cells in the columns left free by row spans from the rows above, left to right, as HTML tables
/// do. Validation and layout share it, so a row the validator accepts is laid out the same way.
/// </summary>
internal sealed class TableGrid(int columns)
{
    private readonly int[] _covered = new int[columns]; // Rows below the current one that each column is still covered for.

    public int Columns { get; } = columns;

    /// <summary>A span from an earlier row still covers a column of the next row.</summary>
    public bool IsOpen => Array.Exists(_covered, rows => rows > 0);

    /// <summary>
    /// Places a row. <c>Free</c> is how many columns the rows above left free, <c>Covered</c> how many the cells fill
    /// (a column span is cut where it would cover a taken column), and <c>Dropped</c> how many cells found no column.
    /// </summary>
    public (List<GridCell> Cells, int Free, int Covered, int Dropped) Place(IReadOnlyList<Cell> cells)
    {
        bool[] taken = new bool[Columns];
        int free = 0;
        for (int c = 0; c < Columns; c++)
        {
            taken[c] = _covered[c] > 0;
            free += taken[c] ? 0 : 1;
        }

        var placed = new List<GridCell>(cells.Count);
        int[] next = new int[Columns];
        for (int c = 0; c < Columns; c++)
        {
            next[c] = Math.Max(0, _covered[c] - 1);
        }

        int column = 0, covered = 0, dropped = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            while (column < Columns && taken[column])
            {
                column++;
            }

            if (column >= Columns)
            {
                dropped++;
                continue;
            }

            Cell cell = cells[i] ?? new Cell("");
            int span = 1;
            while (span < Math.Max(1, cell.ColumnSpan) && column + span < Columns && !taken[column + span])
            {
                span++;
            }

            int rowSpan = Math.Max(1, cell.RowSpan);
            placed.Add(new GridCell(cell, column, span, rowSpan, i));
            for (int c = column; c < column + span; c++)
            {
                taken[c] = true;
                next[c] = rowSpan - 1;
            }

            covered += span;
            column += span;
        }

        Array.Copy(next, _covered, Columns);
        return (placed, free, covered, dropped);
    }

    /// <summary>The columns a row left uncovered, after <see cref="Place"/>: where padding cells go.</summary>
    public static List<int> Gaps(int columns, IReadOnlyList<GridCell> placed, IReadOnlyCollection<int> takenFromAbove)
    {
        bool[] taken = new bool[columns];
        foreach (int c in takenFromAbove)
        {
            taken[c] = true;
        }

        foreach (GridCell cell in placed)
        {
            for (int c = cell.Column; c < cell.Column + cell.ColumnSpan; c++)
            {
                taken[c] = true;
            }
        }

        var gaps = new List<int>();
        for (int c = 0; c < columns; c++)
        {
            if (!taken[c])
            {
                gaps.Add(c);
            }
        }

        return gaps;
    }

    /// <summary>The columns covered by spans from rows above, for the row about to be placed.</summary>
    public List<int> CoveredFromAbove()
    {
        var columns = new List<int>();
        for (int c = 0; c < Columns; c++)
        {
            if (_covered[c] > 0)
            {
                columns.Add(c);
            }
        }

        return columns;
    }

    /// <summary>Ends the table: spans that run past the last row stop there.</summary>
    public void Close() => Array.Clear(_covered);
}
