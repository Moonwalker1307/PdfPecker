using System.Globalization;

namespace PdfPecker.Css;

/// <summary>SVG presentation properties: fill and stroke, markers, filter primitives and text alignment.</summary>
internal sealed partial class StyleResolver
{
    private static void ApplyFill(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => ParsePaint(d.Value, s, d.Inherit ? parent.SvgFill : null, d.Inherit ? parent.SvgFillUrl : null, d.Inherit,
            out s.SvgFill, out s.SvgFillUrl, s.SvgFill, s.SvgFillUrl);

    private static void ApplyStroke(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => ParsePaint(d.Value, s, d.Inherit ? parent.SvgStroke : null, d.Inherit ? parent.SvgStrokeUrl : null, d.Inherit,
            out s.SvgStroke, out s.SvgStrokeUrl, s.SvgStroke, s.SvgStrokeUrl);

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
