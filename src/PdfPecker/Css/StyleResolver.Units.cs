using PdfPecker.Fonts;

namespace PdfPecker.Css;

/// <summary>Font-relative units that need the font's metrics: ex, ch, cap, ic, lh and their root versions.</summary>
internal sealed partial class StyleResolver
{
    private ComputedStyle? _htmlStyle; // the html element's style once it is computed, for rex, rch, rcap, ric and rlh
    private bool[] _applied = new bool[64]; // declarations ApplyFontPropertiesFirst has applied

    private bool HasMetricUnit()
    {
        for (int i = 0; i < _count; i++)
            if (_decls[i].MetricUnits) return true;
        return false;
    }

    /// <summary>
    /// Applies the font properties before the rest, in their own order: a length in ex or ch measures the font the element
    /// ends up with, whichever order the declarations are written in. Only the declarations without var() are moved.
    /// </summary>
    private void ApplyFontPropertiesFirst(ComputedStyle s, ComputedStyle parent, (int Index, AllKeyword Keyword) all)
    {
        if (_applied.Length < _count) _applied = new bool[Math.Max(_count, _applied.Length * 2)];
        // line-height goes last: its own ex and ch measure the font, and the lh unit of other properties measures it.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < _count; i++)
            {
                var d = _decls[i];
                bool lineHeight = d.Property == "line-height";
                if (lineHeight != (pass == 1) || !IsFontProperty(d.Property) || d.HasVar || ResetByAll(i, all)) continue;
                _applied[i] = true;
                Apply(s, parent, d.Property, d.MetricUnits ? RewriteMetricUnits(d.Value, s, lineHeight ? parent : s) : d.Value);
            }
        }
    }

    private static bool IsFontProperty(string property)
        => property is "font-family" or "font-weight" or "font-style" or "font-stretch" or "font-variation-settings"
            or "font-optical-sizing" or "line-height";

    /// <summary>Rewrites the ex, ch, cap, ic and lh lengths of a value to points; lh measures <paramref name="lineHeightOf"/>.</summary>
    private string RewriteMetricUnits(string value, ComputedStyle fontOf, ComputedStyle lineHeightOf)
        => CssUnits.Rewrite(value, unit => MetricUnit(unit, fontOf, lineHeightOf));

    private float? MetricUnit(string unit, ComputedStyle fontOf, ComputedStyle lineHeightOf)
    {
        bool root = unit[0] == 'r';
        if (root) unit = unit.Substring(1);
        var style = root ? _htmlStyle ?? fontOf : fontOf;
        if (unit == "lh") return LineHeightPoints(root ? _htmlStyle ?? lineHeightOf : lineHeightOf);
        var font = style.UnitFont();
        float size = style.FontSize;
        // A font without the metric gives 0, and the unit falls back as the CSS specification says.
        return unit switch
        {
            "ex" => size * Or(font.Metric(FontMetric.XHeight), 0.5f),
            "ch" => size * Or(font.Metric(FontMetric.ZeroWidth), 0.5f),
            "cap" => size * Or(font.Metric(FontMetric.CapHeight), font.Ascent),
            "ic" => size * Or(font.Metric(FontMetric.IdeographWidth), 1f),
            _ => null,
        };
    }

    private static float Or(float metric, float fallback) => metric > 0 ? metric : fallback;

    /// <summary>The used line height of a style in points, which is what the lh unit is one of.</summary>
    private static float LineHeightPoints(ComputedStyle style)
        => style.LineHeightKind == LineHeightKind.Normal ? style.UnitFont().NormalLineHeight(style.FontSize) : style.LineHeight;
}
