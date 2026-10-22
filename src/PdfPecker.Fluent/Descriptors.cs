using PdfPecker.Fluent.Model;

namespace PdfPecker.Fluent;

/// <summary>Adds items to a column.</summary>
public sealed class ColumnDescriptor
{
    private readonly ColumnElement _column;

    internal ColumnDescriptor(ColumnElement column) => _column = column;

    /// <summary>The space between items.</summary>
    public void Spacing(float value, Unit unit = Unit.Point) => _column.Spacing = Length.Points(value, unit, nameof(value));

    /// <summary>A new item below the previous one.</summary>
    public IContainer Item()
    {
        var slot = new Container();
        _column.Items.Add(slot);
        return slot;
    }
}

/// <summary>Adds items to a row.</summary>
public sealed class RowDescriptor
{
    private readonly RowElement _row;

    internal RowDescriptor(RowElement row) => _row = row;

    /// <summary>The space between items.</summary>
    public void Spacing(float value, Unit unit = Unit.Point) => _row.Spacing = Length.Points(value, unit, nameof(value));

    /// <summary>An item that shares the width left over by the constant and auto items, in proportion to its size.</summary>
    public IContainer RelativeItem(float size = 1) => Add(RowItemKind.Relative, Length.Weight(size, nameof(size)));

    /// <summary>An item of a fixed width.</summary>
    public IContainer ConstantItem(float size, Unit unit = Unit.Point) => Add(RowItemKind.Constant, Length.Points(size, unit, nameof(size)));

    /// <summary>An item as wide as its content.</summary>
    public IContainer AutoItem() => Add(RowItemKind.Auto, 0);

    private Container Add(RowItemKind kind, float size)
    {
        var slot = new Container();
        _row.Items.Add((kind, size, slot));
        return slot;
    }
}

/// <summary>Defines a table's columns and adds its cells.</summary>
public sealed class TableDescriptor
{
    private readonly TableElement _table;

    internal TableDescriptor(TableElement table) => _table = table;

    /// <summary>The columns, from left to right.</summary>
    public void ColumnsDefinition(Action<TableColumnsDefinitionDescriptor> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        handler(new TableColumnsDefinitionDescriptor(_table));
    }

    /// <summary>Header rows, repeated at the top of every page the table spans.</summary>
    public void Header(Action<TableCellDescriptor> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        handler(new TableCellDescriptor(_table.Header));
    }

    /// <summary>Footer rows, repeated at the bottom of every page the table spans.</summary>
    public void Footer(Action<TableCellDescriptor> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        handler(new TableCellDescriptor(_table.Footer));
    }

    /// <summary>The next cell. Cells fill the rows from left to right, then top to bottom.</summary>
    public ITableCellContainer Cell() => TableCellDescriptor.Add(_table.Body);
}

/// <summary>Adds cells to a table's header or footer.</summary>
public sealed class TableCellDescriptor
{
    private readonly List<TableCell> _cells;

    internal TableCellDescriptor(List<TableCell> cells) => _cells = cells;

    /// <summary>The next cell. Cells fill the rows from left to right, then top to bottom.</summary>
    public ITableCellContainer Cell() => Add(_cells);

    internal static TableCell Add(List<TableCell> cells)
    {
        var cell = new TableCell();
        cells.Add(cell);
        return cell;
    }
}

/// <summary>Defines a table's columns.</summary>
public sealed class TableColumnsDefinitionDescriptor
{
    private readonly TableElement _table;

    internal TableColumnsDefinitionDescriptor(TableElement table) => _table = table;

    /// <summary>A column of a fixed width.</summary>
    public void ConstantColumn(float width, Unit unit = Unit.Point) => Add(false, Length.Points(width, unit, nameof(width)));

    /// <summary>A column that shares the width left over by the constant columns, in proportion to its size.</summary>
    public void RelativeColumn(float size = 1) => Add(true, Length.Weight(size, nameof(size)));

    private void Add(bool relative, float size)
    {
        if (_table.Columns.Count >= 1000) throw new InvalidOperationException("A table can have at most 1,000 columns.");
        _table.Columns.Add((relative, size));
    }
}

/// <summary>Places and spans table cells.</summary>
public static class TableCellExtensions
{
    /// <summary>The largest row number, row span or column number a cell can have.</summary>
    internal const uint MaxIndex = 100_000;

    /// <summary>Spans the cell over several columns.</summary>
    public static ITableCellContainer ColumnSpan(this ITableCellContainer cell, uint columns)
    {
        Cell(cell).ColumnSpan = Index(columns, nameof(columns));
        return cell;
    }

    /// <summary>Spans the cell over several rows.</summary>
    public static ITableCellContainer RowSpan(this ITableCellContainer cell, uint rows)
    {
        Cell(cell).RowSpan = Index(rows, nameof(rows));
        return cell;
    }

    /// <summary>Places the cell in a given row, counted from 1.</summary>
    public static ITableCellContainer Row(this ITableCellContainer cell, uint row)
    {
        Cell(cell).Row = Index(row, nameof(row));
        return cell;
    }

    /// <summary>Places the cell in a given column, counted from 1.</summary>
    public static ITableCellContainer Column(this ITableCellContainer cell, uint column)
    {
        Cell(cell).Column = Index(column, nameof(column));
        return cell;
    }

    private static TableCell Cell(ITableCellContainer cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return cell as TableCell ?? throw new ArgumentException("Only cells that PdfPecker.Fluent creates can be used.", nameof(cell));
    }

    private static int Index(uint value, string paramName)
    {
        if (value is 0 or > MaxIndex) throw new ArgumentOutOfRangeException(paramName, value, "The value must be from 1 to 100,000.");
        return (int)value;
    }
}

/// <summary>How an image fills its slot.</summary>
public sealed class ImageDescriptor
{
    private readonly ImageElement _image;

    internal ImageDescriptor(ImageElement image) => _image = image;

    /// <summary>Scales the image to the slot's width, keeping its proportions (the default).</summary>
    public ImageDescriptor FitWidth() => Scale(ImageScaling.FitWidth);

    /// <summary>Scales the image to the slot's height, keeping its proportions.</summary>
    public ImageDescriptor FitHeight() => Scale(ImageScaling.FitHeight);

    /// <summary>Scales the image to fit inside the slot, keeping its proportions.</summary>
    public ImageDescriptor FitArea() => Scale(ImageScaling.FitArea);

    /// <summary>Stretches the image to the slot's width and height.</summary>
    public ImageDescriptor FitUnproportionally() => Scale(ImageScaling.FitUnproportionally);

    /// <summary>A text that describes the image for screen readers and PDF/UA.</summary>
    public ImageDescriptor AlternativeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _image.AlternativeText = text;
        return this;
    }

    private ImageDescriptor Scale(ImageScaling scaling)
    {
        _image.Scaling = scaling;
        return this;
    }
}

/// <summary>The colour of a line.</summary>
public sealed class LineDescriptor
{
    private readonly LineElement _line;

    internal LineDescriptor(LineElement line) => _line = line;

    /// <summary>The line colour (black by default).</summary>
    public LineDescriptor LineColor(Color color)
    {
        _line.Color = color;
        return this;
    }

    /// <summary>
    /// Draws the line dashed. The pattern's first entry is the dash length: dashes no longer than the line is thick are
    /// drawn as dots. The exact lengths are the renderer's, as in a browser.
    /// </summary>
    /// <exception cref="ArgumentException">The pattern is empty or has a negative or non-finite length.</exception>
    public LineDescriptor LineDashPattern(float[] dashPattern, Unit unit = Unit.Point)
    {
        ArgumentNullException.ThrowIfNull(dashPattern);
        if (dashPattern.Length == 0) throw new ArgumentException("Give at least one dash length.", nameof(dashPattern));
        float dash = Length.Points(dashPattern[0], unit, nameof(dashPattern));
        foreach (var length in dashPattern) Length.Points(length, unit, nameof(dashPattern));
        _line.Style = dash <= _line.Thickness ? "dotted" : "dashed";
        return this;
    }
}

/// <summary>Adds layers: one primary layer that takes space, and others drawn under or over it in the order added.</summary>
public sealed class LayersDescriptor
{
    private readonly LayersElement _layers;

    internal LayersDescriptor(LayersElement layers) => _layers = layers;

    /// <summary>A layer that covers the primary layer's area, drawn over the layers added before it.</summary>
    public IContainer Layer() => Add(false);

    /// <summary>The layer whose content sets the size of all layers. There must be exactly one.</summary>
    /// <exception cref="InvalidOperationException">The layers already have a primary layer.</exception>
    public IContainer PrimaryLayer()
    {
        foreach (var (primary, _) in _layers.Layers)
            if (primary) throw new InvalidOperationException("Layers can have only one primary layer.");
        return Add(true);
    }

    private Container Add(bool primary)
    {
        var slot = new Container();
        _layers.Layers.Add((primary, slot));
        return slot;
    }
}

/// <summary>Adds items laid out like words in a paragraph: side by side, wrapping onto new lines.</summary>
public sealed class InlinedDescriptor
{
    private readonly InlinedElement _inlined;

    internal InlinedDescriptor(InlinedElement inlined) => _inlined = inlined;

    /// <summary>The space between items and between lines.</summary>
    public void Spacing(float value, Unit unit = Unit.Point)
    {
        HorizontalSpacing(value, unit);
        VerticalSpacing(value, unit);
    }

    /// <summary>The space between items on a line.</summary>
    public void HorizontalSpacing(float value, Unit unit = Unit.Point) => _inlined.HorizontalSpacing = Length.Points(value, unit, nameof(value));

    /// <summary>The space between lines.</summary>
    public void VerticalSpacing(float value, Unit unit = Unit.Point) => _inlined.VerticalSpacing = Length.Points(value, unit, nameof(value));

    /// <summary>Lines start at the left (the default).</summary>
    public void AlignLeft() => _inlined.Justify = "flex-start";

    /// <summary>Lines are centred.</summary>
    public void AlignCenter() => _inlined.Justify = "center";

    /// <summary>Lines end at the right.</summary>
    public void AlignRight() => _inlined.Justify = "flex-end";

    /// <summary>Items spread over the whole line, the first at the left and the last at the right.</summary>
    public void AlignJustify() => _inlined.Justify = "space-between";

    /// <summary>Items spread over the whole line with equal space around each.</summary>
    public void AlignSpaceAround() => _inlined.Justify = "space-around";

    /// <summary>Items on a line share their top edge (the default).</summary>
    public void BaselineTop() => _inlined.Align = "flex-start";

    /// <summary>Items on a line are centred vertically.</summary>
    public void BaselineMiddle() => _inlined.Align = "center";

    /// <summary>Items on a line share their bottom edge.</summary>
    public void BaselineBottom() => _inlined.Align = "flex-end";

    /// <summary>A new item after the previous one.</summary>
    public IContainer Item()
    {
        var slot = new Container();
        _inlined.Items.Add(slot);
        return slot;
    }
}

/// <summary>Sets up content that flows through several columns.</summary>
public sealed class MultiColumnDescriptor
{
    private readonly MultiColumnElement _multiColumn;

    internal MultiColumnDescriptor(MultiColumnElement multiColumn) => _multiColumn = multiColumn;

    /// <summary>The number of columns, from 1 to 100 (2 by default).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not 1 to 100.</exception>
    public void Columns(int value = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
        _multiColumn.Columns = value;
    }

    /// <summary>The space between columns.</summary>
    public void Spacing(float value, Unit unit = Unit.Point) => _multiColumn.Spacing = Length.Points(value, unit, nameof(value));

    /// <summary>
    /// Whether the columns of the last page share the content equally (the default here) or fill one after another.
    /// </summary>
    public void BalanceHeight(bool enable = true) => _multiColumn.Balance = enable;

    /// <summary>The content that flows through the columns.</summary>
    public IContainer Content() => _multiColumn.Content;
}

/// <summary>The parts of a <c>Decoration</c>: content, and what comes before and after it on every page it spans.</summary>
public sealed class DecorationDescriptor
{
    private readonly DecorationElement _decoration;

    internal DecorationDescriptor(DecorationElement decoration) => _decoration = decoration;

    /// <summary>The part above the content, repeated at the top of every page the content spans.</summary>
    public IContainer Before() => _decoration.Before;

    /// <summary>The content, which flows over pages.</summary>
    public IContainer Content() => _decoration.Content;

    /// <summary>The part below the content, repeated at the bottom of every page the content spans.</summary>
    public IContainer After() => _decoration.After;
}

/// <summary>How a numbered list counts its items.</summary>
public enum ListNumbering
{
    /// <summary>1, 2, 3.</summary>
    Decimal,

    /// <summary>a, b, c.</summary>
    LowerAlpha,

    /// <summary>A, B, C.</summary>
    UpperAlpha,

    /// <summary>i, ii, iii.</summary>
    LowerRoman,

    /// <summary>I, II, III.</summary>
    UpperRoman,
}

/// <summary>Adds items to a list and sets its markers: bullets unless told otherwise.</summary>
public sealed class ListDescriptor
{
    private readonly ListElement _list;

    internal ListDescriptor(ListElement list) => _list = list;

    /// <summary>The space between items.</summary>
    public void Spacing(float value, Unit unit = Unit.Point) => _list.Spacing = Length.Points(value, unit, nameof(value));

    /// <summary>How far the items are indented; the markers sit in this space. The default is 18 points.</summary>
    public void Indent(float value, Unit unit = Unit.Point) => _list.Indent = Length.Points(value, unit, nameof(value));

    /// <summary>Round bullets, the default.</summary>
    public void Bullet()
    {
        _list.Ordered = false;
        _list.MarkerStyle = "disc";
    }

    /// <summary>Numbers the items, starting at <paramref name="start"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> is beyond ±1,000,000.</exception>
    public void Numbered(ListNumbering numbering = ListNumbering.Decimal, int start = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(start, -1_000_000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, 1_000_000);
        _list.Ordered = true;
        _list.Start = start;
        _list.MarkerStyle = numbering switch
        {
            ListNumbering.LowerAlpha => "lower-alpha",
            ListNumbering.UpperAlpha => "upper-alpha",
            ListNumbering.LowerRoman => "lower-roman",
            ListNumbering.UpperRoman => "upper-roman",
            ListNumbering.Decimal => "decimal",
            _ => throw new ArgumentOutOfRangeException(nameof(numbering), numbering, "Not a list numbering."),
        };
    }

    /// <summary>A marker of your own before every item, such as "–" or "✓".</summary>
    /// <exception cref="ArgumentException"><paramref name="marker"/> is empty or longer than 20 characters.</exception>
    public void Marker(string marker)
    {
        ArgumentException.ThrowIfNullOrEmpty(marker);
        if (marker.Length > 20) throw new ArgumentException("A list marker can be at most 20 characters.", nameof(marker));
        _list.Ordered = false;
        _list.MarkerStyle = Html.CssValue.String(Html.HtmlWriter.Clean(marker) + " ");
    }

    /// <summary>A new item below the previous one.</summary>
    public IContainer Item()
    {
        var slot = new Container();
        _list.Items.Add(slot);
        return slot;
    }
}
