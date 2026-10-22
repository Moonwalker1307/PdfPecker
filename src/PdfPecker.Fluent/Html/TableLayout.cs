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
