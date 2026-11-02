using PdfPecker.Fluent.Model;

namespace PdfPecker.Fluent.Html;

/// <summary>Places table cells on a grid: in order from left to right and top to bottom, or where a cell says.</summary>
internal static class TableLayout
{
    /// <summary>
    /// The cell that starts at each (row, column), the number of rows, and a test for grid positions a span covers.
    /// </summary>
    /// <exception cref="InvalidOperationException">A cell does not fit, cells overlap, or the table is too large.</exception>
    public static Dictionary<(int Row, int Column), TableCell> Place(List<TableCell> cells, int columns, out int rows, out Func<int, int, bool> covered)
    {
        var grid = new List<bool[]>();
        var placed = new Dictionary<(int, int), TableCell>();
        int row = 0, column = 0;
        rows = 0;

        foreach (var cell in cells)
        {
            int span = cell.ColumnSpan;
            if (span > columns)
                throw new InvalidOperationException($"A cell spans {span} columns, but the table has {columns}.");

            if (cell.Row != null || cell.Column != null)
            {
                row = cell.Row - 1 ?? row;
                if (cell.Column is { } c)
                {
                    column = c - 1;
                    if (column + span > columns)
                        throw new InvalidOperationException($"A cell in column {c} spanning {span} columns does not fit in {columns} columns.");
                    if (!Fits(grid, row, column, span, cell.RowSpan, columns))
                        throw new InvalidOperationException($"Two table cells overlap at row {row + 1}, column {c}.");
                }
                else
                {
                    column = 0;
                    while (!Fits(grid, row, column, span, cell.RowSpan, columns))
                    {
                        if (++column + span > columns)
                            throw new InvalidOperationException($"A cell spanning {span} columns does not fit in row {row + 1}.");
                    }
                }
            }
            else
            {
                while (column + span > columns || !Fits(grid, row, column, span, cell.RowSpan, columns))
                {
                    if (++column + span > columns)
                    {
                        row++;
                        column = 0;
                    }
                }
            }

            Mark(grid, row, column, span, cell.RowSpan, columns);
            placed.Add((row, column), cell);
            rows = Math.Max(rows, row + 1);
            column += span;
            if (column >= columns)
            {
                row++;
                column = 0;
            }
        }

        var final = grid;
        covered = (r, c) => r < final.Count && final[r][c];
        return placed;
    }

    /// <summary>
    /// The cells, by where they start, that grow down to the last of <paramref name="rows"/> rows as QuestPDF's
    /// ExtendLastCellsToTableBottom makes them: the cell that ends lowest in each column, when it ends above the last
    /// row and is the lowest in every column it spans. The grid below such a cell is empty, so the longer span covers
    /// no other cell.
    /// </summary>
    public static HashSet<(int Row, int Column)> ExtendLastCells(Dictionary<(int Row, int Column), TableCell> placed, int columns, int rows)
    {
        var lowest = new (int Row, int Column, int End)?[columns];
        foreach (var ((row, column), cell) in placed)
        {
            int end = Math.Min(row + cell.RowSpan, rows);
            for (int c = column; c < column + cell.ColumnSpan && c < columns; c++)
                if (lowest[c] is not { } l || end > l.End) lowest[c] = (row, column, end);
        }
        var extended = new HashSet<(int, int)>();
        foreach (var l in lowest)
        {
            if (l is not { } last || last.End >= rows || extended.Contains((last.Row, last.Column))) continue;
            var cell = placed[(last.Row, last.Column)];
            bool lowestInAll = true;
            for (int c = last.Column; c < last.Column + cell.ColumnSpan && c < columns; c++)
                if (lowest[c] is not { } other || other.Row != last.Row || other.Column != last.Column) lowestInAll = false;
            if (lowestInAll) extended.Add((last.Row, last.Column));
        }
        return extended;
    }

    /// <summary>
    /// For each column, the row of the cell <see cref="ExtendLastCells"/> grew over it, below which the grid is that
    /// cell's; -1 for the other columns.
    /// </summary>
    public static int[] ExtendedFrom(HashSet<(int Row, int Column)> extended, Dictionary<(int Row, int Column), TableCell> placed, int columns)
    {
        var from = new int[columns];
        Array.Fill(from, -1);
        foreach (var (r, c) in extended)
            for (int k = c; k < c + placed[(r, c)].ColumnSpan && k < columns; k++) from[k] = r;
        return from;
    }

    private static bool Fits(List<bool[]> grid, int row, int column, int span, int rowSpan, int columns)
    {
        for (int r = row; r < row + rowSpan && r < grid.Count; r++)
            for (int c = column; c < column + span; c++)
                if (grid[r][c]) return false;
        return true;
    }

    private static void Mark(List<bool[]> grid, int row, int column, int span, int rowSpan, int columns)
    {
        long needed = (long)(row + rowSpan) * columns;
        if (needed > HtmlWriter.MaxTableGrid)
            throw new InvalidOperationException("The table is too large: rows times columns must stay under 4,000,000.");
        while (grid.Count < row + rowSpan) grid.Add(new bool[columns]);
        for (int r = row; r < row + rowSpan; r++)
            for (int c = column; c < column + span; c++)
                grid[r][c] = true;
    }
}
