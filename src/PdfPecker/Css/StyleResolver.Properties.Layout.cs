using System.Globalization;

namespace PdfPecker.Css;

/// <summary>Flexbox, grid and multi-column properties.</summary>
internal sealed partial class StyleResolver
{
    private static void ApplyFlexDirection(ComputedStyle s, in DeclaredValue d)
        => s.FlexDirection = d.Lower switch
        {
            "row" => FlexDirection.Row,
            "row-reverse" => FlexDirection.RowReverse,
            "column" => FlexDirection.Column,
            "column-reverse" => FlexDirection.ColumnReverse,
            _ => s.FlexDirection,
        };

    private static void ApplyFlexWrap(ComputedStyle s, in DeclaredValue d)
        => s.FlexWrap = d.Lower switch { "nowrap" => FlexWrap.NoWrap, "wrap" => FlexWrap.Wrap, "wrap-reverse" => FlexWrap.WrapReverse, _ => s.FlexWrap };

    private static void ApplyFlexGrow(ComputedStyle s, in DeclaredValue d)
    {
        if (float.TryParse(d.Lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float fg) && fg >= 0) s.FlexGrow = fg;
    }

    private static void ApplyFlexShrink(ComputedStyle s, in DeclaredValue d)
    {
        if (float.TryParse(d.Lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float fsh) && fsh >= 0) s.FlexShrink = fsh;
    }

    private void ApplyFlexBasis(ComputedStyle s, in DeclaredValue d)
    {
        if (d.Lower is "content" or "auto" or "max-content" or "min-content" or "fit-content") s.FlexBasis = Length.Auto;
        else SetLength(ref s.FlexBasis, d.Lower, d.Em, null);
    }

    private static void ApplyOrder(ComputedStyle s, in DeclaredValue d)
    {
        if (Invariant.TryParseInt(d.Lower, out int order)) s.Order = order;
    }

    /// <summary>
    /// The -webkit-box- properties of the legacy flexible box. An invalid value is ignored, and so is a negative flex or
    /// an ordinal group below 1, as in Chrome.
    /// </summary>
    private static void ApplyLegacyBox(ComputedStyle s, ComputedStyle parent, string prop, in DeclaredValue d)
    {
        switch (prop)
        {
            case "-webkit-box-orient":
                if (d.Lower is "horizontal" or "inline-axis" or "vertical" or "block-axis") s.BoxVertical = d.Lower is "vertical" or "block-axis";
                break;
            case "-webkit-box-direction":
                if (d.Lower == "inherit") s.BoxReverse = parent.BoxReverse;
                else if (d.Lower is "normal" or "reverse" or "initial") s.BoxReverse = d.Lower == "reverse";
                break;
            case "-webkit-box-pack":
                s.BoxPack = d.Lower switch
                {
                    "start" => BoxAlign.Start,
                    "end" => BoxAlign.End,
                    "center" => BoxAlign.Center,
                    "justify" => BoxAlign.SpaceBetween,
                    _ => s.BoxPack,
                };
                break;
            case "-webkit-box-align":
                s.BoxAlignItems = d.Lower switch
                {
                    "start" => BoxAlign.Start,
                    "end" => BoxAlign.End,
                    "center" => BoxAlign.Center,
                    "baseline" => BoxAlign.Baseline,
                    "stretch" => BoxAlign.Stretch,
                    _ => s.BoxAlignItems,
                };
                break;
            case "-webkit-box-flex":
                if (float.TryParse(d.Lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float flex) && flex >= 0 && float.IsFinite(flex)) s.BoxFlex = flex;
                break;
            case "-webkit-box-ordinal-group":
                if (Invariant.TryParseInt(d.Lower, out int group) && group >= 1) s.BoxOrdinalGroup = group;
                break;
        }
    }

    /// <summary>
    /// Lays out a legacy flexible box as Chrome does: as a flex container that takes its direction, packing and
    /// alignment from the -webkit-box- properties, or, when it clamps lines in the vertical orientation, as a block
    /// (the display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp idiom). Its items grow and shrink by
    /// their -webkit-box-flex and are ordered by their -webkit-box-ordinal-group, whatever their flex properties say.
    /// </summary>
    private static void FinishLegacyBox(ComputedStyle s, ComputedStyle parent)
    {
        if (parent.LegacyBox && parent.IsFlex)
        {
            s.FlexGrow = s.FlexShrink = s.BoxFlex;
            s.Order = s.BoxOrdinalGroup;
            s.AlignSelf = BoxAlign.Auto;
        }
        if (!s.LegacyBox || !s.IsFlex) return;
        if (s.BoxVertical && s.LineClamp > 0)
        {
            s.Display = s.Display == Display.InlineFlex ? Display.InlineBlock : Display.Block;
            s.LegacyBox = false;
            return;
        }
        s.FlexDirection = (s.BoxVertical, s.BoxReverse) switch
        {
            (false, false) => FlexDirection.Row,
            (false, true) => FlexDirection.RowReverse,
            (true, false) => FlexDirection.Column,
            _ => FlexDirection.ColumnReverse,
        };
        s.FlexWrap = FlexWrap.NoWrap;
        s.JustifyContent = s.BoxPack;
        s.AlignItems = s.BoxAlignItems;
    }

    /// <summary>row-gap and column-gap: normal clears the gap, so the layout uses its own default.</summary>
    private void ApplyGap(ComputedStyle s, string prop, in DeclaredValue d)
    {
        Length? gap = null;
        if (d.Lower != "normal")
        {
            if (!TryParseLength(d.Lower, d.Em, _rootFontSize, out var gl, _vw, _vh) || gl.IsAuto) return;
            gap = ToLayoutUnits(gl);
        }
        if (prop == "row-gap") s.RowGap = gap;
        else s.ColumnGap = gap;
    }

    /// <summary>grid-auto-columns and grid-auto-rows: the tracks of a list before any auto-repeat, or none.</summary>
    private Track[]? ParseAutoTracks(in DeclaredValue d)
        => ParseTrackList(d.Lower, d.Em)?.Before is { Length: > 0 } tracks ? tracks : null;

    private static void ApplyGridAutoFlow(ComputedStyle s, in DeclaredValue d)
    {
        s.GridAutoFlowColumn = d.Lower.Contains("column");
        s.GridAutoFlowDense = d.Lower.Contains("dense");
    }

    private static void ApplyColumnCount(ComputedStyle s, in DeclaredValue d)
        => s.ColumnCount = Invariant.TryParseInt(d.Lower, out int cc) && cc > 0 ? Math.Min(cc, 100) : 0;

    private void ApplyColumnWidth(ComputedStyle s, in DeclaredValue d)
    {
        SetLength(ref s.ColumnWidth, d.Lower, d.Em, null);
        if (s.ColumnWidth.IsPercent) s.ColumnWidth = Length.Auto;
    }
}
