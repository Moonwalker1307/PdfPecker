using System.Buffers;
using System.Globalization;

namespace PdfPecker.Css;

/// <summary>SVG presentation properties: fill and stroke, markers, filter primitives and text alignment.</summary>
internal sealed partial class StyleResolver
{
    private static readonly SearchValues<char> DashSeparators = SearchValues.Create(" \t\r\n\f,");

    private static void ApplyFill(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        ParsePaint(d.Value, s, d.Inherit ? parent.SvgFill : null, d.Inherit ? parent.SvgFillUrl : null, d.Inherit,
            out var color, out var url, s.SvgFill, s.SvgFillUrl);
        s.SvgFill = color;
        s.SvgFillUrl = url;
    }

    private static void ApplyStroke(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        ParsePaint(d.Value, s, d.Inherit ? parent.SvgStroke : null, d.Inherit ? parent.SvgStrokeUrl : null, d.Inherit,
            out var color, out var url, s.SvgStroke, s.SvgStrokeUrl);
        s.SvgStroke = color;
        s.SvgStrokeUrl = url;
    }

    /// <summary>stroke-width: a plain number is in user units (CSS pixels); a length is converted to them.</summary>
    private void ApplyStrokeWidth(ComputedStyle s, in DeclaredValue d)
    {
        if (float.TryParse(d.Lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float sw)) s.SvgStrokeWidth = Math.Max(0, sw);
        else if (TryParseLength(d.Lower, d.Em, _rootFontSize, out var swl, _vw, _vh) && !swl.IsAuto && !swl.IsPercent) s.SvgStrokeWidth = Math.Max(0, swl.Value / 0.75f);
    }

    private static void ApplyFillOpacity(ComputedStyle s, in DeclaredValue d)
    {
        if (TryParseNumberOrPercent(d.Lower, out float fo)) s.SvgFillOpacity = Math.Clamp(fo, 0, 1);
    }

    private static void ApplyStrokeOpacity(ComputedStyle s, in DeclaredValue d)
    {
        if (TryParseNumberOrPercent(d.Lower, out float so)) s.SvgStrokeOpacity = Math.Clamp(so, 0, 1);
    }

    private static void ApplyStrokeLineCap(ComputedStyle s, in DeclaredValue d)
        => s.SvgLineCap = d.Lower switch { "round" => SvgLineCap.Round, "square" => SvgLineCap.Square, "butt" => SvgLineCap.Butt, _ => s.SvgLineCap };

    private static void ApplyStrokeLineJoin(ComputedStyle s, in DeclaredValue d)
        => s.SvgLineJoin = d.Lower switch { "round" => SvgLineJoin.Round, "bevel" => SvgLineJoin.Bevel, "miter" or "miter-clip" or "arcs" => SvgLineJoin.Miter, _ => s.SvgLineJoin };

    private static void ApplyStrokeMiterLimit(ComputedStyle s, in DeclaredValue d)
    {
        if (float.TryParse(d.Lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float ml) && ml >= 1) s.SvgMiterLimit = ml;
    }

    /// <summary>
    /// stroke-dasharray: none, or dash and gap lengths separated by commas or spaces. A negative or unreadable value
    /// makes the whole declaration invalid, so it is ignored, as is a list longer than <see cref="SvgDashArray.MaxValues"/>.
    /// </summary>
    private void ApplyStrokeDashArray(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.SvgDashArray = parent.SvgDashArray; return; }
        if (d.Lower == "none") { s.SvgDashArray = null; return; }
        var values = new List<float>();
        List<bool>? percent = null;
        var rest = d.Lower.AsSpan();
        while (true)
        {
            rest = rest.TrimStart(" \t\r\n\f");
            if (rest.IsEmpty) break;
            int end = rest.IndexOfAny(DashSeparators);
            if (end < 0) end = rest.Length;
            if (values.Count == SvgDashArray.MaxValues || !TryParseSvgLength(rest.Slice(0, end).ToString(), d.Em, out float v, out bool pct) || v < 0) return;
            if (pct) percent ??= new List<bool>(new bool[values.Count]);
            values.Add(v);
            percent?.Add(pct);
            rest = rest.Slice(end).TrimStart(" \t\r\n\f");
            // A comma separates two values; one at the start or end of the list, or two in a row, is an error.
            if (!rest.IsEmpty && rest[0] == ',')
            {
                rest = rest.Slice(1).TrimStart(" \t\r\n\f");
                if (rest.IsEmpty || rest[0] == ',') return;
            }
        }
        if (values.Count == 0) return;
        s.SvgDashArray = new SvgDashArray(values.ToArray(), percent?.ToArray());
    }

    /// <summary>stroke-dashoffset: a length, percentage or plain number of user units; it may be negative.</summary>
    private void ApplyStrokeDashOffset(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.SvgDashOffset = parent.SvgDashOffset; s.SvgDashOffsetPercent = parent.SvgDashOffsetPercent; }
        else if (TryParseSvgLength(d.Lower, d.Em, out float v, out bool pct)) { s.SvgDashOffset = v; s.SvgDashOffsetPercent = pct; }
    }

    /// <summary>
    /// An SVG length: a plain number of user units (CSS pixels), a CSS length converted to them, or a percentage, which
    /// is returned as written with <paramref name="percent"/> set.
    /// </summary>
    private bool TryParseSvgLength(string lower, float em, out float value, out bool percent)
    {
        percent = lower.EndsWith('%');
        if (percent ? float.TryParse(lower.AsSpan(0, lower.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            : float.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return float.IsFinite(value) && Math.Abs(value) <= Length.MaxMagnitude;
        if (TryParseLength(lower, em, _rootFontSize, out var len, _vw, _vh) && !len.IsAuto && !len.IsPercent)
        {
            value = len.Value / 0.75f;
            return true;
        }
        return false;
    }

    private static void ApplyFloodColor(ComputedStyle s, in DeclaredValue d)
    {
        if (CssColor.TryParse(d.Value, s.Color, out uint flood)) s.SvgFloodColor = flood;
    }

    private static void ApplyFloodOpacity(ComputedStyle s, in DeclaredValue d)
    {
        if (TryParseNumberOrPercent(d.Lower, out float fop)) s.SvgFloodOpacity = Math.Clamp(fop, 0, 1);
    }

    private static void ApplyLightingColor(ComputedStyle s, in DeclaredValue d)
    {
        if (CssColor.TryParse(d.Value, s.Color, out uint light)) s.SvgLightingColor = light;
    }

    private static void ApplyColorInterpolationFilters(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.SvgFilterSrgb = d.Lower switch { "srgb" => true, "linearrgb" or "auto" => false, "inherit" => parent.SvgFilterSrgb, _ => s.SvgFilterSrgb };

    private static void ApplyTextAnchor(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.SvgTextAnchor = d.Lower switch { "middle" => 1, "end" => 2, "start" => 0, "inherit" => parent.SvgTextAnchor, _ => s.SvgTextAnchor };

    /// <summary>dominant-baseline and alignment-baseline, which both choose the baseline SVG text sits on.</summary>
    private static void ApplySvgBaseline(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.SvgBaseline = d.Lower switch
        {
            "middle" or "central" => 1,
            "hanging" or "text-before-edge" or "before-edge" => 2,
            "text-after-edge" or "after-edge" or "ideographic" => 3,
            "auto" or "alphabetic" or "baseline" => 0,
            "inherit" => parent.SvgBaseline,
            _ => s.SvgBaseline,
        };
}
