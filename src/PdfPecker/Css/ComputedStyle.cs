using PdfPecker.Fonts;

namespace PdfPecker.Css;

internal enum Display : byte
{
    Inline, Block, ListItem, InlineBlock, Table, TableRow, TableCell, TableRowGroup, TableHeaderGroup,
    TableFooterGroup, TableCaption, TableColumn, TableColumnGroup, None,
}

internal enum TextAlign : byte { Left, Right, Center, Justify }
internal enum WhiteSpace : byte { Normal, NoWrap, Pre, PreWrap, PreLine }
internal enum VerticalAlign : byte { Baseline, Sub, Super, Top, Middle, Bottom, TextTop, TextBottom }
internal enum TextTransform : byte { None, Uppercase, Lowercase, Capitalize }
internal enum BorderStyle : byte { None, Solid, Dashed, Dotted, Double }
internal enum LineHeightKind : byte { Normal, Factor, Absolute }

internal enum ListStyleType : byte
{
    Disc, Circle, Square, Decimal, DecimalLeadingZero, LowerAlpha, UpperAlpha, LowerRoman, UpperRoman, None,
}

[Flags]
internal enum TextDecoration : byte { None = 0, Underline = 1, LineThrough = 2, Overline = 4 }

internal enum LengthUnit : byte { Pt, Percent, Auto }

/// <summary>A computed length: absolute points, a percentage of the containing block, or auto.</summary>
internal readonly struct Length
{
    public readonly float Value;
    public readonly LengthUnit Unit;

    public Length(float value, LengthUnit unit) { Value = value; Unit = unit; }

    public static readonly Length Auto = new(0, LengthUnit.Auto);
    public static readonly Length Zero = new(0, LengthUnit.Pt);
    public static Length Pt(float v) => new(v, LengthUnit.Pt);

    public bool IsAuto => Unit == LengthUnit.Auto;
    public bool IsPercent => Unit == LengthUnit.Percent;

    public float Resolve(float basis, float autoValue = 0)
        => Unit switch
        {
            LengthUnit.Pt => Value,
            LengthUnit.Percent => Value * basis / 100f,
            _ => autoValue,
        };

    public override string ToString() => Unit switch { LengthUnit.Pt => Value + "pt", LengthUnit.Percent => Value + "%", _ => "auto" };
}

/// <summary>The computed values of the supported CSS properties for one element. All lengths are in points.</summary>
internal sealed class ComputedStyle
{
    // ---- Inherited ----
    public uint Color = CssColor.Black;
    public FontFamily FontFamily = FontFamily.Times;
    public bool Bold;
    public bool Italic;
    public float FontSize = 12f; // 16px
    public LineHeightKind LineHeightKind = LineHeightKind.Normal;
    public float LineHeightValue;
    public TextAlign TextAlign;
    public WhiteSpace WhiteSpace;
    public ListStyleType ListStyleType;
    public bool ListStylePositionInside;
    public TextTransform TextTransform;
    public TextDecoration TextDecoration; // propagated to descendants, approximated as inherited
    public float TextIndent;
    public float LetterSpacing;
    public float WordSpacing;
    public bool Visible = true;
    public bool BorderCollapse;
    public float BorderSpacingH = 1.5f, BorderSpacingV = 1.5f; // 2px
    public string? FontFamilyName;

    // ---- Not inherited ----
    public Display Display = Display.Inline;
    public uint BackgroundColor;
    public Length MarginTop = Length.Zero, MarginRight = Length.Zero, MarginBottom = Length.Zero, MarginLeft = Length.Zero;
    public Length PaddingTop = Length.Zero, PaddingRight = Length.Zero, PaddingBottom = Length.Zero, PaddingLeft = Length.Zero;
    public float BorderTopWidth = 1.5f, BorderRightWidth = 1.5f, BorderBottomWidth = 1.5f, BorderLeftWidth = 1.5f; // medium = 3px
    public BorderStyle BorderTopStyle, BorderRightStyle, BorderBottomStyle, BorderLeftStyle;
    public uint? BorderTopColor, BorderRightColor, BorderBottomColor, BorderLeftColor; // null = currentColor
    public Length Width = Length.Auto, Height = Length.Auto;
    public Length MinWidth = Length.Zero, MaxWidth = Length.Auto, MinHeight = Length.Zero, MaxHeight = Length.Auto;
    public bool BorderBox;
    public VerticalAlign VerticalAlign;
    public bool PageBreakBefore, PageBreakAfter, AvoidBreakInside;
    public bool Floated;
    public bool ClearBoth;

    private StandardFont? _font;

    public StandardFont Font => _font ??= StandardFont.Get(FontFamily, Bold, Italic);

    public void InvalidateFont() => _font = null;

    public float LineHeight
        => LineHeightKind switch
        {
            LineHeightKind.Factor => LineHeightValue * FontSize,
            LineHeightKind.Absolute => LineHeightValue,
            _ => FontSize * 1.2f,
        };

    // Effective border widths: zero when the style is none.
    public float BorderTop => BorderTopStyle == BorderStyle.None ? 0 : BorderTopWidth;
    public float BorderRight => BorderRightStyle == BorderStyle.None ? 0 : BorderRightWidth;
    public float BorderBottom => BorderBottomStyle == BorderStyle.None ? 0 : BorderBottomWidth;
    public float BorderLeft => BorderLeftStyle == BorderStyle.None ? 0 : BorderLeftWidth;

    public bool HasBorder => BorderTopStyle != BorderStyle.None || BorderRightStyle != BorderStyle.None
                             || BorderBottomStyle != BorderStyle.None || BorderLeftStyle != BorderStyle.None;

    public bool IsBlockLevel => Display is not (Display.Inline or Display.InlineBlock);

    /// <summary>Creates the starting style for a child: inherited properties copied, the rest reset.</summary>
    public ComputedStyle CreateChild()
    {
        var c = (ComputedStyle)MemberwiseClone();
        c.Display = Display.Inline;
        c.BackgroundColor = 0;
        c.MarginTop = c.MarginRight = c.MarginBottom = c.MarginLeft = Length.Zero;
        c.PaddingTop = c.PaddingRight = c.PaddingBottom = c.PaddingLeft = Length.Zero;
        c.BorderTopWidth = c.BorderRightWidth = c.BorderBottomWidth = c.BorderLeftWidth = 1.5f;
        c.BorderTopStyle = c.BorderRightStyle = c.BorderBottomStyle = c.BorderLeftStyle = BorderStyle.None;
        c.BorderTopColor = c.BorderRightColor = c.BorderBottomColor = c.BorderLeftColor = null;
        c.Width = c.Height = Length.Auto;
        c.MinWidth = c.MinHeight = Length.Zero;
        c.MaxWidth = c.MaxHeight = Length.Auto;
        c.BorderBox = false;
        c.VerticalAlign = VerticalAlign.Baseline;
        c.PageBreakBefore = c.PageBreakAfter = c.AvoidBreakInside = false;
        c.Floated = false;
        c.ClearBoth = false;
        return c;
    }

    public ComputedStyle Clone() => (ComputedStyle)MemberwiseClone();
}
