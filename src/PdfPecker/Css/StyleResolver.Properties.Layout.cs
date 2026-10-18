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
