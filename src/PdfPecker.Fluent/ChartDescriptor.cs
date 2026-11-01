using System.Globalization;
using PdfPecker.Fluent.Html;

namespace PdfPecker.Fluent;

/// <summary>The kind of chart that <see cref="ContentExtensions.Chart"/> draws.</summary>
public enum ChartType
{
    /// <summary>Vertical bars, one group per category (the default).</summary>
    Column,

    /// <summary>Horizontal bars, one group per category.</summary>
    Bar,

    /// <summary>A line through the values of each series, with a dot at each category.</summary>
    Line,

    /// <summary>A pie of the first series, one slice per category; values of zero or less are left out.</summary>
    Pie,
}

/// <summary>
/// Describes a chart: its type, categories, series and labels. The chart is drawn as an SVG image, so it scales with
/// the slot like any other image, and its text is part of the drawing.
/// </summary>
public sealed class ChartDescriptor
{
    internal const int MaxCategories = 1_000, MaxSeries = 100;

    /// <summary>Characters a font family name may not hold, since it is written into an SVG attribute.</summary>
    private static readonly System.Buffers.SearchValues<char> NameStops = System.Buffers.SearchValues.Create("\"'\\<>&;");

    internal ChartType Kind { get; private set; }
    internal List<string> CategoryNames { get; } = [];
    internal List<ChartSeriesDescriptor> SeriesList { get; } = [];
    internal bool? ShowLegend { get; private set; }
    internal bool ShowDataLabels { get; private set; }
    internal string Format { get; private set; } = "0.##";
    internal float Width { get; private set; } = 480;
    internal float Height { get; private set; } = 270;
    internal float LabelSize { get; private set; } = 9;
    internal string? FontFamilyName { get; private set; }

    internal ChartDescriptor()
    {
    }

    /// <summary>The kind of chart: <see cref="ChartType.Column"/> by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is not a <see cref="ChartType"/>.</exception>
    public ChartDescriptor Type(ChartType type)
    {
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type), type, "Not a chart type.");
        Kind = type;
        return this;
    }

    /// <summary>The names of the categories, in order: the labels along the category axis, or of the pie's slices.</summary>
    /// <exception cref="ArgumentException">There are more than 1,000 categories.</exception>
    public ChartDescriptor Categories(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (CategoryNames.Count + names.Length > MaxCategories)
            throw new ArgumentException("A chart has at most 1,000 categories.", nameof(names));
        foreach (var name in names) CategoryNames.Add(name ?? "");
        return this;
    }

    /// <summary>
    /// A series of values, one per category in order, drawn in the next colour of the chart's palette unless given
    /// one. A series with more values than there are categories adds categories without a name.
    /// </summary>
    /// <exception cref="ArgumentException">A value is not finite, there are more than 1,000 values, or the chart already has 100 series.</exception>
    public ChartSeriesDescriptor Series(string name, IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (SeriesList.Count >= MaxSeries) throw new ArgumentException("A chart has at most 100 series.", nameof(values));
        var list = new List<double>();
        foreach (double value in values)
        {
            if (!double.IsFinite(value)) throw new ArgumentException("A chart value must be a finite number.", nameof(values));
            if (list.Count == MaxCategories) throw new ArgumentException("A series has at most 1,000 values.", nameof(values));
            list.Add(value);
        }
        var series = new ChartSeriesDescriptor(name ?? "", list);
        SeriesList.Add(series);
        return series;
    }

    /// <summary>A series of whole numbers; see <see cref="Series(string, IEnumerable{double})"/>.</summary>
    public ChartSeriesDescriptor Series(string name, IEnumerable<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Series(name, values.Select(v => (double)v));
    }

    /// <summary>A series of decimal amounts; see <see cref="Series(string, IEnumerable{double})"/>.</summary>
    public ChartSeriesDescriptor Series(string name, IEnumerable<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Series(name, values.Select(v => (double)v));
    }

    /// <summary>
    /// Shows the legend below the chart, or hides it with <c>false</c>. It is shown by default when there are several
    /// series, and for a pie.
    /// </summary>
    public ChartDescriptor Legend(bool show = true)
    {
        ShowLegend = show;
        return this;
    }

    /// <summary>Writes each value next to its bar, point or slice; a pie's slices show their percentage.</summary>
    public ChartDescriptor DataLabels(bool show = true)
    {
        ShowDataLabels = show;
        return this;
    }

    /// <summary>
    /// The .NET number format of the values on the axis and in the data labels, such as <c>"N0"</c> or
    /// <c>"0.0'%'"</c>, applied with the invariant culture. The default is <c>"0.##"</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The format is empty, longer than 64 characters or not a number format.</exception>
    public ChartDescriptor ValueFormat(string format)
    {
        ArgumentException.ThrowIfNullOrEmpty(format);
        if (format.Length > 64) throw new ArgumentException("A value format has at most 64 characters.", nameof(format));
        try
        {
            _ = 1234.5.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException e)
        {
            throw new ArgumentException("Not a number format.", nameof(format), e);
        }
        Format = format;
        return this;
    }

    /// <summary>
    /// The size the chart is drawn at, 480 × 270 points by default. Its text has the size <see cref="FontSize"/> at
    /// this size, and the image is then scaled to the slot like any image, so give the size it will be shown at.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A side is not between 50 and 14,400 points.</exception>
    public ChartDescriptor Size(float width, float height, Unit unit = Unit.Point)
    {
        Width = Side(Length.ToPoints(width, unit), nameof(width));
        Height = Side(Length.ToPoints(height, unit), nameof(height));
        return this;
    }

    /// <summary>The size of the chart's text in points, 9 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is not between 1 and 200 points.</exception>
    public ChartDescriptor FontSize(float size)
    {
        if (!float.IsFinite(size) || size < 1 || size > 200) throw new ArgumentOutOfRangeException(nameof(size), size, "A chart font size must be from 1 to 200 points.");
        LabelSize = size;
        return this;
    }

    /// <summary>The font of the chart's text, with fallbacks; sans-serif by default.</summary>
    /// <exception cref="ArgumentException">No family is given, or a name holds a quote, a backslash or a control character.</exception>
    public ChartDescriptor FontFamily(params string[] families)
    {
        ArgumentNullException.ThrowIfNull(families);
        if (families.Length == 0) throw new ArgumentException("Give at least one font family.", nameof(families));
        foreach (var family in families)
        {
            if (string.IsNullOrWhiteSpace(family) || family.Length > 100 || family.AsSpan().IndexOfAny(NameStops) >= 0 || family.Any(char.IsControl))
                throw new ArgumentException("Not a font family name.", nameof(families));
        }
        FontFamilyName = string.Join(",", families.Select(f => "'" + f.Trim() + "'"));
        return this;
    }

    private static float Side(float points, string paramName)
    {
        if (!float.IsFinite(points) || points < 50 || points > 14_400)
            throw new ArgumentOutOfRangeException(paramName, points, "A chart side must be from 50 to 14,400 points.");
        return points;
    }

    internal string ToSvg() => ChartSvg.Write(this);
}

/// <summary>A series of a chart.</summary>
public sealed class ChartSeriesDescriptor
{
    internal ChartSeriesDescriptor(string name, List<double> values)
    {
        Name = name;
        Values = values;
    }

    internal string Name { get; }
    internal List<double> Values { get; }
    internal Color? SeriesColor { get; private set; }

    /// <summary>The colour of the series' bars, line and legend entry.</summary>
    public ChartSeriesDescriptor Color(Color color)
    {
        SeriesColor = color;
        return this;
    }
}
