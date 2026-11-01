using System.Globalization;
using System.Text;

namespace PdfPecker.Fluent.Html;

/// <summary>
/// Draws a chart as SVG markup. Sizes are in points, the units of the viewBox; text widths are estimated from the font
/// size, since the fluent layer measures no text, and labels are cut with an ellipsis where they would collide.
/// </summary>
internal static class ChartSvg
{
    // A palette of distinct, print-friendly colours (Material 600), used in order for series or pie slices.
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x1E, 0x88, 0xE5), Color.FromRgb(0xFB, 0x8C, 0x00), Color.FromRgb(0x43, 0xA0, 0x47),
        Color.FromRgb(0xE5, 0x39, 0x35), Color.FromRgb(0x8E, 0x24, 0xAA), Color.FromRgb(0x00, 0x89, 0x7B),
        Color.FromRgb(0x6D, 0x4C, 0x41), Color.FromRgb(0x75, 0x75, 0x75),
    ];

    private const string Grid = "#e0e0e0", Axis = "#9e9e9e", Label = "#424242";

    /// <summary>The average advance of a character as a fraction of the font size, for a sans-serif font.</summary>
    private const float CharWidth = 0.55f;

    public static string Write(ChartDescriptor chart)
    {
        var svg = new StringBuilder(4096);
        float w = chart.Width, h = chart.Height, fs = chart.LabelSize;
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(N(w)).Append("\" height=\"").Append(N(h))
            .Append("\" viewBox=\"0 0 ").Append(N(w)).Append(' ').Append(N(h)).Append("\" font-family=\"")
            .Append(chart.FontFamilyName ?? "sans-serif").Append("\" font-size=\"").Append(N(fs)).Append("\" fill=\"").Append(Label).Append("\">");

        int categories = chart.CategoryNames.Count;
        foreach (var series in chart.SeriesList) categories = Math.Max(categories, series.Values.Count);
        bool pie = chart.Kind == ChartType.Pie;
        bool legend = chart.ShowLegend ?? (pie || chart.SeriesList.Count > 1);
        float bottom = h - fs * 0.5f;
        if (legend) bottom = DrawLegend(svg, chart, categories, w, bottom, fs);

        var plot = (X: fs * 0.5f, Y: fs * 0.5f, W: w - fs, H: bottom - fs * 0.5f);
        if (plot.W > fs && plot.H > fs)
        {
            if (pie) DrawPie(svg, chart, categories, plot, fs);
            else DrawAxes(svg, chart, categories, plot, fs);
        }
        return svg.Append("</svg>").ToString();
    }

    private static Color SeriesColor(ChartDescriptor chart, int index)
        => chart.SeriesList[index].SeriesColor ?? Palette[index % Palette.Length];

    private static string Category(ChartDescriptor chart, int index) => index < chart.CategoryNames.Count ? chart.CategoryNames[index] : "";

    /// <summary>Draws the legend in rows along the bottom, and returns the bottom of the space left above it.</summary>
    private static float DrawLegend(StringBuilder svg, ChartDescriptor chart, int categories, float w, float bottom, float fs)
    {
        bool pie = chart.Kind == ChartType.Pie;
        int count = pie ? categories : chart.SeriesList.Count;
        if (count == 0) return bottom;
        var entries = new List<(string Text, Color Color, float Width)>(count);
        float swatch = fs * 0.8f, gap = fs * 1.2f, maxWidth = w - fs;
        for (int i = 0; i < count; i++)
        {
            string text = pie ? Category(chart, i) : chart.SeriesList[i].Name;
            text = Fit(text, maxWidth - swatch - fs * 0.4f, fs);
            entries.Add((text, pie ? Palette[i % Palette.Length] : SeriesColor(chart, i), swatch + fs * 0.4f + TextWidth(text, fs)));
        }
        // Rows of entries, centred, the last row at the bottom.
        var rows = new List<List<int>> { new() };
        float rowWidth = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            float add = entries[i].Width + (rows[^1].Count > 0 ? gap : 0);
            if (rows[^1].Count > 0 && rowWidth + add > maxWidth)
            {
                rows.Add([]);
                rowWidth = 0;
                add = entries[i].Width;
            }
            rows[^1].Add(i);
            rowWidth += add;
        }
        float lineHeight = fs * 1.5f;
        // A legend never takes more than a third of the chart; the rows that do not fit are left out.
        int maxRows = Math.Max(1, (int)(bottom / 3 / lineHeight));
        if (rows.Count > maxRows) rows.RemoveRange(maxRows, rows.Count - maxRows);
        float top = bottom - rows.Count * lineHeight;
        for (int r = 0; r < rows.Count; r++)
        {
            float width = rows[r].Sum(i => entries[i].Width) + gap * (rows[r].Count - 1);
            float x = (w - width) / 2, baseline = top + r * lineHeight + fs;
            foreach (int i in rows[r])
            {
                Rect(svg, x, baseline - swatch, swatch, swatch, entries[i].Color);
                Text(svg, x + swatch + fs * 0.4f, baseline - fs * 0.05f, entries[i].Text, "start");
                x += entries[i].Width + gap;
            }
        }
        return top - fs * 0.5f;
    }

    private static void DrawAxes(StringBuilder svg, ChartDescriptor chart, int categories, (float X, float Y, float W, float H) plot, float fs)
    {
        bool bar = chart.Kind == ChartType.Bar, line = chart.Kind == ChartType.Line;
        double min = 0, max = 0;
        foreach (var series in chart.SeriesList)
            foreach (double v in series.Values)
            {
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
        var (lo, hi, step) = NiceScale(min, max);
        var ticks = new List<double>();
        for (int i = 0; ; i++)
        {
            double t = lo + i * step;
            if (t > hi + step * 1e-9 || i > 50) break;
            ticks.Add(Math.Abs(t) < step * 1e-9 ? 0 : t);
        }
        var tickLabels = ticks.Select(t => t.ToString(chart.Format, CultureInfo.InvariantCulture)).ToList();

        // The value axis labels sit left of the plot for columns and lines, below it for bars; category labels the other way.
        float valueLabelWidth = tickLabels.Max(t => TextWidth(t, fs));
        float x0, y0, pw, ph;
        if (bar)
        {
            float categoryWidth = 0;
            for (int i = 0; i < categories; i++) categoryWidth = Math.Max(categoryWidth, TextWidth(Category(chart, i), fs));
            categoryWidth = Math.Min(categoryWidth, plot.W * 0.35f);
            x0 = plot.X + categoryWidth + fs * 0.5f;
            pw = plot.X + plot.W - x0 - valueLabelWidth / 2;
            y0 = plot.Y;
            ph = plot.H - fs * 1.6f;
        }
        else
        {
            x0 = plot.X + valueLabelWidth + fs * 0.5f;
            pw = plot.X + plot.W - x0;
            y0 = plot.Y + (chart.ShowDataLabels ? fs : 0);
            ph = plot.Y + plot.H - fs * 1.6f - y0;
        }
        if (pw <= 1 || ph <= 1) return;

        // Where a value lands along the value axis.
        double span = hi - lo;
        float Pos(double v) => bar ? x0 + (float)((v - lo) / span * pw) : y0 + ph - (float)((v - lo) / span * ph);

        for (int i = 0; i < ticks.Count; i++)
        {
            float p = Pos(ticks[i]);
            if (bar)
            {
                Line(svg, p, y0, p, y0 + ph, ticks[i] == 0 ? Axis : Grid);
                Text(svg, p, y0 + ph + fs * 1.2f, tickLabels[i], "middle");
            }
            else
            {
                Line(svg, x0, p, x0 + pw, p, ticks[i] == 0 ? Axis : Grid);
                Text(svg, x0 - fs * 0.4f, p + fs * 0.35f, tickLabels[i], "end");
            }
        }
        if (categories == 0) return;

        float band = (bar ? ph : pw) / categories;
        float BandStart(int c) => bar ? y0 + c * band : x0 + c * band;
        for (int c = 0; c < categories; c++)
        {
            string name = Category(chart, c);
            if (bar) Text(svg, x0 - fs * 0.5f, BandStart(c) + band / 2 + fs * 0.35f, Fit(name, x0 - fs * 0.5f - plot.X, fs), "end");
            else Text(svg, BandStart(c) + band / 2, y0 + ph + fs * 1.2f, Fit(name, band - fs * 0.2f, fs), "middle");
        }

        int seriesCount = chart.SeriesList.Count;
        if (seriesCount == 0) return;
        float zero = Pos(Math.Clamp(0, lo, hi));
        if (line)
        {
            for (int s = 0; s < seriesCount; s++)
            {
                var values = chart.SeriesList[s].Values;
                string color = SeriesColor(chart, s).ToString();
                if (values.Count > 1)
                {
                    svg.Append("<polyline fill=\"none\" stroke=\"").Append(color).Append("\" stroke-width=\"").Append(N(Math.Max(1, fs / 5)))
                        .Append("\" stroke-linejoin=\"round\" points=\"");
                    for (int c = 0; c < values.Count; c++)
                        svg.Append(c > 0 ? " " : "").Append(N(BandStart(c) + band / 2)).Append(',').Append(N(Pos(values[c])));
                    svg.Append("\"/>");
                }
                for (int c = 0; c < values.Count; c++)
                {
                    float x = BandStart(c) + band / 2, y = Pos(values[c]);
                    svg.Append("<circle cx=\"").Append(N(x)).Append("\" cy=\"").Append(N(y)).Append("\" r=\"").Append(N(Math.Max(1.5f, fs / 3.5f)))
                        .Append("\" fill=\"").Append(color).Append("\"/>");
                    if (chart.ShowDataLabels) Text(svg, x, y - fs * 0.6f, Value(chart, values[c]), "middle");
                }
            }
            return;
        }

        // Bars: each category's band holds one bar per series, side by side, in 75% of the band.
        float group = band * 0.75f, thickness = group / seriesCount;
        for (int s = 0; s < seriesCount; s++)
        {
            var values = chart.SeriesList[s].Values;
            var color = SeriesColor(chart, s);
            for (int c = 0; c < values.Count; c++)
            {
                float start = BandStart(c) + (band - group) / 2 + s * thickness;
                float end = Pos(values[c]);
                float a = Math.Min(zero, end), length = Math.Abs(end - zero);
                if (bar) Rect(svg, a, start, length, thickness, color);
                else Rect(svg, start, a, thickness, length, color);
                if (!chart.ShowDataLabels) continue;
                string label = Value(chart, values[c]);
                bool negative = values[c] < 0;
                if (bar) Text(svg, end + (negative ? -fs * 0.3f : fs * 0.3f), start + thickness / 2 + fs * 0.35f, label, negative ? "end" : "start");
                else Text(svg, start + thickness / 2, negative ? end + fs * 1.1f : end - fs * 0.3f, label, "middle");
            }
        }
    }

    private static void DrawPie(StringBuilder svg, ChartDescriptor chart, int categories, (float X, float Y, float W, float H) plot, float fs)
    {
        if (chart.SeriesList.Count == 0) return;
        var values = chart.SeriesList[0].Values;
        double total = values.Where(v => v > 0).Sum();
        if (total <= 0) return;
        float r = Math.Min(plot.W, plot.H) / 2, cx = plot.X + plot.W / 2, cy = plot.Y + plot.H / 2;
        double angle = -Math.PI / 2;
        for (int c = 0; c < values.Count; c++)
        {
            if (values[c] <= 0) continue;
            double sweep = values[c] / total * 2 * Math.PI;
            string color = Palette[c % Palette.Length].ToString();
            if (sweep >= 2 * Math.PI - 1e-9)
                svg.Append("<circle cx=\"").Append(N(cx)).Append("\" cy=\"").Append(N(cy)).Append("\" r=\"").Append(N(r)).Append("\" fill=\"").Append(color).Append("\"/>");
            else
            {
                double end = angle + sweep;
                svg.Append("<path fill=\"").Append(color).Append("\" stroke=\"#fff\" stroke-width=\"").Append(N(Math.Max(0.5f, fs / 10)))
                    .Append("\" d=\"M").Append(N(cx)).Append(',').Append(N(cy))
                    .Append('L').Append(N(cx + r * (float)Math.Cos(angle))).Append(',').Append(N(cy + r * (float)Math.Sin(angle)))
                    .Append('A').Append(N(r)).Append(',').Append(N(r)).Append(" 0 ").Append(sweep > Math.PI ? '1' : '0').Append(",1 ")
                    .Append(N(cx + r * (float)Math.Cos(end))).Append(',').Append(N(cy + r * (float)Math.Sin(end))).Append("Z\"/>");
            }
            if (chart.ShowDataLabels && sweep > 0.15)
            {
                double mid = angle + sweep / 2;
                string percent = (values[c] / total * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
                svg.Append("<text fill=\"#fff\" font-weight=\"bold\" x=\"").Append(N(cx + r * 0.65f * (float)Math.Cos(mid)))
                    .Append("\" y=\"").Append(N(cy + r * 0.65f * (float)Math.Sin(mid) + fs * 0.35f)).Append("\" text-anchor=\"middle\">")
                    .Append(Escape(percent)).Append("</text>");
            }
            angle += sweep;
        }
    }

    /// <summary>
    /// A scale from at most <paramref name="min"/> to at least <paramref name="max"/> in round steps (1, 2 or 5 times a
    /// power of ten), about five of them.
    /// </summary>
    internal static (double Lo, double Hi, double Step) NiceScale(double min, double max)
    {
        if (max - min <= 0) max = min + 1;
        double raw = (max - min) / 5;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double fraction = raw / magnitude;
        double step = (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10) * magnitude;
        double lo = Math.Floor(min / step) * step, hi = Math.Ceiling(max / step) * step;
        if (!double.IsFinite(lo) || !double.IsFinite(hi) || !double.IsFinite(step) || step <= 0) return (min, max, max - min);
        return (lo, hi, step);
    }

    private static string Value(ChartDescriptor chart, double value) => value.ToString(chart.Format, CultureInfo.InvariantCulture);

    private static float TextWidth(string text, float fs) => text.Length * fs * CharWidth;

    /// <summary>The text, cut with an ellipsis to about <paramref name="width"/>.</summary>
    private static string Fit(string text, float width, float fs)
    {
        if (TextWidth(text, fs) <= width) return text;
        int keep = (int)(width / (fs * CharWidth)) - 1;
        if (keep <= 0) return "";
        if (char.IsHighSurrogate(text[keep - 1])) keep--;
        return text[..keep] + "…";
    }

    private static void Rect(StringBuilder svg, float x, float y, float w, float h, Color color)
        => svg.Append("<rect x=\"").Append(N(x)).Append("\" y=\"").Append(N(y)).Append("\" width=\"").Append(N(w)).Append("\" height=\"").Append(N(h))
            .Append("\" fill=\"").Append(color.ToString()).Append("\"/>");

    private static void Line(StringBuilder svg, float x1, float y1, float x2, float y2, string color)
        => svg.Append("<line x1=\"").Append(N(x1)).Append("\" y1=\"").Append(N(y1)).Append("\" x2=\"").Append(N(x2)).Append("\" y2=\"").Append(N(y2))
            .Append("\" stroke=\"").Append(color).Append("\" stroke-width=\"0.75\"/>");

    private static void Text(StringBuilder svg, float x, float y, string text, string anchor)
    {
        if (text.Length == 0) return;
        svg.Append("<text x=\"").Append(N(x)).Append("\" y=\"").Append(N(y)).Append('"');
        if (anchor != "start") svg.Append(" text-anchor=\"").Append(anchor).Append('"');
        svg.Append('>').Append(Escape(text)).Append("</text>");
    }

    /// <summary>
    /// Caller text as XML character data: markup characters escaped, and characters XML cannot hold (controls, lone
    /// surrogates) replaced, so a name can never break the drawing.
    /// </summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[++i]);
                continue;
            }
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\t' or '\n' or '\r': sb.Append(' '); break;
                default:
                    sb.Append(char.IsSurrogate(c) || c < ' ' || c is '￾' or '￿' ? '�' : c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string N(float value) => CssValue.Number(value);
}
