using System.Globalization;

namespace PdfPecker.Css;

/// <summary>
/// The zoom property, as Chrome computes it since it follows the CSS specification: an element's effective zoom is its
/// own zoom times its parent's, and every absolute length on the element (px, pt, mm, viewport units, ...) is multiplied
/// by it, so the box and its text grow or shrink and take that much room in the layout. Percentages are left alone,
/// and em-based lengths follow the zoomed font size by themselves.
/// </summary>
internal sealed partial class StyleResolver
{
    private const float MinZoom = 1f / 64, MaxZoom = 64;
    private float _rootZoom = 1; // the html element's effective zoom, which the root font size of rem already has
    private float _zoom = 1;     // the effective zoom of the element being computed

    /// <summary>Sets the element's effective zoom from its zoom declaration, scaling the inherited lengths by its own zoom.</summary>
    private void ApplyZoom(ComputedStyle s, ComputedStyle parent)
    {
        s.Zoom = parent.Zoom;
        float own = 1;
        for (int i = _count - 1; i >= 0; i--)
        {
            if (_decls[i].Property != "zoom") continue;
            if (Substituted(_decls[i], s) is { } value && ParseZoom(value.Trim().ToLowerInvariant()) is { } z) own = z;
            break;
        }
        // Zooms multiply, but stay within bounds, so lengths stay finite however deeply zoomed boxes nest.
        if (own != 1) s.Zoom = Math.Clamp(parent.Zoom * own, MinZoom, MaxZoom);
        _zoom = s.Zoom;
        if (s.Zoom != 1)
        {
            // The medium border, outline and column rule widths a box starts with are lengths too.
            float medium = SnapBorderWidth(1.5f * s.Zoom);
            if (s.BorderTopWidth == 1.5f) s.BorderTopWidth = medium;
            if (s.BorderRightWidth == 1.5f) s.BorderRightWidth = medium;
            if (s.BorderBottomWidth == 1.5f) s.BorderBottomWidth = medium;
            if (s.BorderLeftWidth == 1.5f) s.BorderLeftWidth = medium;
            if (s.OutlineWidth == 1.5f) s.OutlineWidth = medium;
            if (s.ColumnRuleWidth == 1.5f) s.ColumnRuleWidth = medium;
        }
        if (s.Zoom == parent.Zoom) return;
        float r = s.Zoom / parent.Zoom;
        // The inherited lengths are the parent's at the parent's zoom; this element shows them at its own.
        s.FontSize *= r;
        if (s.LineHeightKind == LineHeightKind.Absolute) s.LineHeightValue *= r;
        s.LetterSpacing *= r;
        s.WordSpacing *= r;
        if (s.TextIndent.Unit == LengthUnit.Pt) s.TextIndent = Length.Pt(s.TextIndent.Value * r);
        s.BorderSpacingH *= r;
        s.BorderSpacingV *= r;
        if (s.TextStrokeWidth != 0) s.TextStrokeWidth *= r;
    }

    /// <summary>
    /// A zoom value: a number or percentage of at least 0, where 0 means 1, or normal (and the old reset). Null when
    /// it is not valid.
    /// </summary>
    internal static float? ParseZoom(string lower)
    {
        if (lower is "normal" or "reset" or "initial" or "unset") return 1;
        bool percent = lower.EndsWith('%');
        var number = percent ? lower.AsSpan(0, lower.Length - 1) : lower.AsSpan();
        if (!float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out float n) || !float.IsFinite(n) || n < 0) return null;
        if (percent) n /= 100;
        return n == 0 ? 1 : n;
    }

    /// <summary>
    /// Multiplies the absolute lengths of a declared value by the element's effective zoom. Root-relative lengths (rem,
    /// rex, ...) are scaled from the html element's zoom, which the root font size already has. In font-size, the
    /// lengths relative to the parent's font (em, ex, ch, %, ...) are scaled by the element's own zoom only, because the
    /// parent's size already has the parent's.
    /// </summary>
    private string Zoomed(string property, string value, ComputedStyle s, ComputedStyle parent)
    {
        if (s.Zoom == 1 && _rootZoom == 1 || property == "zoom" || property.StartsWith("--", StringComparison.Ordinal)) return value;
        bool fontSize = property == "font-size";
        float own = s.Zoom / parent.Zoom, root = s.Zoom / _rootZoom;
        return CssUnits.Scale(value, unit => ZoomOfUnit(unit, s.Zoom, root, fontSize ? own : 1));
    }

    private static float ZoomOfUnit(string unit, float zoom, float root, float relative)
    {
        switch (unit)
        {
            case "px" or "pt" or "pc" or "in" or "cm" or "mm" or "q":
            case "vw" or "vh" or "vi" or "vb" or "vmin" or "vmax":
            case "svw" or "svh" or "svi" or "svb" or "svmin" or "svmax":
            case "lvw" or "lvh" or "lvi" or "lvb" or "lvmin" or "lvmax":
            case "dvw" or "dvh" or "dvi" or "dvb" or "dvmin" or "dvmax":
                return zoom;
            case "rem" or "rex" or "rch" or "rcap" or "ric" or "rlh":
                return root;
            case "em" or "ex" or "ch" or "cap" or "ic" or "lh" or "%":
                return relative;
            default:
                return 1;
        }
    }

    /// <summary>The font size of a keyword (medium, larger, ...) at the element's zoom.</summary>
    private static void ZoomFontSizeKeyword(ComputedStyle s, ComputedStyle parent, string value)
    {
        if (s.Zoom == parent.Zoom && s.Zoom == 1) return;
        var v = value.Trim().ToLowerInvariant();
        if (Array.IndexOf(FontSizeKeywords, v) >= 0 || v == "initial") s.FontSize *= s.Zoom;
        else if (v is "larger" or "smaller" or "inherit" or "unset") s.FontSize *= s.Zoom / parent.Zoom;
    }
}
