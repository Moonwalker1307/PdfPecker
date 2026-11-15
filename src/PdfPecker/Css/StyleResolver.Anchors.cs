namespace PdfPecker.Css;

/// <summary>Anchor positioning properties: anchor-name, position-anchor, position-area, fallbacks and anchor().</summary>
internal sealed partial class StyleResolver
{
    /// <summary>
    /// A declaration of a property that takes anchor() or anchor-size(): one that calls them is kept as written for the
    /// layout and leaves the property initial; any other drops what an earlier one kept. Returns whether it was handled.
    /// </summary>
    private bool ApplyAnchorFunction(ComputedStyle s, ComputedStyle parent, string prop, in DeclaredValue d)
    {
        if (!AnchorStyle.UsesFunctions(d.Lower))
        {
            if (s.Anchor?.Functions?.ContainsKey(prop) == true)
            {
                var changed = s.ChangeAnchor();
                changed.Functions!.Remove(prop);
                if (changed.Functions.Count == 0) changed.Functions = null;
            }
            return false;
        }
        var a = s.ChangeAnchor();
        (a.Functions ??= new Dictionary<string, AnchorFunctionValue>(StringComparer.Ordinal))[prop] = new AnchorFunctionValue(d.Lower, d.Em, _rootFontSize, _vw, _vh);
        ApplyCssWideKeyword(s, parent, prop, "initial");
        return true;
    }

    private static void ApplyAnchorName(ComputedStyle s, in DeclaredValue d)
    {
        if (!AnchorStyle.TryParseNames(d.Lower, out var names) || names == null && s.Anchor == null) return;
        s.ChangeAnchor().Names = names;
    }

    private static void ApplyPositionAnchor(ComputedStyle s, in DeclaredValue d)
    {
        string? name = d.Lower is "auto" or "none" ? null : AnchorStyle.IsDashedIdent(d.Lower) ? d.Lower : "";
        if (name == "" || name == null && s.Anchor == null) return;
        s.ChangeAnchor().Default = name;
    }

    private static void ApplyPositionArea(ComputedStyle s, in DeclaredValue d)
    {
        if (!PositionArea.TryParse(d.Lower, s.Rtl, out var area) || area == null && s.Anchor == null) return;
        s.ChangeAnchor().Area = area;
    }

    private static void ApplyPositionTryFallbacks(ComputedStyle s, in DeclaredValue d)
    {
        if (!TryOption.TryParse(d.Lower, s.Rtl, out var options) || options == null && s.Anchor == null) return;
        s.ChangeAnchor().Fallbacks = options;
    }

    /// <summary>position-try: an optional position-try-order (which this library does not apply) and the fallbacks.</summary>
    private static void ApplyPositionTry(ComputedStyle s, in DeclaredValue d)
    {
        var v = d.Lower;
        foreach (var order in new[] { "normal ", "most-width ", "most-height ", "most-block-size ", "most-inline-size " })
            if (v.StartsWith(order, StringComparison.Ordinal)) { v = v.Substring(order.Length).Trim(); break; }
        ApplyPositionTryFallbacks(s, new DeclaredValue(v, d.Em));
    }

    /// <summary>
    /// align-self and justify-self, with anchor-center, which centres an absolutely positioned box on its anchor and
    /// is centre for other boxes.
    /// </summary>
    private static BoxAlign ParseSelfAlign(ComputedStyle s, string lower, BoxAlign current, bool vertical)
    {
        bool center = lower == "anchor-center";
        if (center || s.Anchor != null && (vertical ? s.Anchor.CenterY : s.Anchor.CenterX))
        {
            var a = s.ChangeAnchor();
            if (vertical) a.CenterY = center;
            else a.CenterX = center;
        }
        return center ? BoxAlign.Center : ParseAlign(lower, current);
    }
}
