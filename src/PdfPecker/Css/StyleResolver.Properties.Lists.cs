namespace PdfPecker.Css;

/// <summary>List markers: list-style-type and list-style-image.</summary>
internal sealed partial class StyleResolver
{
    private void ApplyListStyleType(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (GeneratedContent.UnquoteMark(d.Value) is { } markerText)
        {
            // A string is the marker text itself.
            s.ListStyleString = markerText;
            s.ListStyleType = ListStyleType.Disc;
            s.ListCounterStyle = null;
            return;
        }
        string lower = d.Lower;
        if (lower is "inherit" or "unset")
        {
            s.ListStyleString = parent.ListStyleString;
            s.ListStyleType = parent.ListStyleType;
            s.ListCounterStyle = parent.ListCounterStyle;
            return;
        }
        if (lower is "initial") lower = "disc";
        var listBullet = lower switch
        {
            "disc" => ListStyleType.Disc,
            "circle" => ListStyleType.Circle,
            "square" => ListStyleType.Square,
            "disclosure-open" => ListStyleType.DisclosureOpen,
            "disclosure-closed" => ListStyleType.DisclosureClosed,
            "none" => ListStyleType.None,
            _ => (ListStyleType?)null,
        };
        if (listBullet is { } listBulletType)
        {
            // The bullets are drawn as shapes, as Chrome draws them, and cannot be redefined by @counter-style.
            s.ListStyleType = listBulletType;
            s.ListStyleString = null;
            s.ListCounterStyle = null;
        }
        else if (CounterStyles.TryGet(d.Value, out var listCounter))
        {
            s.ListStyleString = null;
            s.ListCounterStyle = listCounter;
            s.ListStyleType = listCounter.Name switch
            {
                "decimal" => ListStyleType.Decimal,
                "decimal-leading-zero" => ListStyleType.DecimalLeadingZero,
                "lower-alpha" or "lower-latin" => ListStyleType.LowerAlpha,
                "upper-alpha" or "upper-latin" => ListStyleType.UpperAlpha,
                "lower-roman" => ListStyleType.LowerRoman,
                "upper-roman" => ListStyleType.UpperRoman,
                _ => ListStyleType.Other,
            };
        }
    }

    private void ApplyListStyleImage(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        string lower = d.Lower;
        string value = d.Value;
        float resolution = 1;
        if (lower is "inherit" or "unset")
        {
            (s.ListStyleImage, s.ListStyleGradient, s.ListStyleImageResolution) = (parent.ListStyleImage, parent.ListStyleGradient, parent.ListStyleImageResolution);
            return;
        }
        if (CssImageSet.IsImageSet(value))
        {
            // An image-set without an option to show leaves the type to show, like an image that fails to load.
            if (CssImageSet.Choose(value) is not { } chosen)
            {
                (s.ListStyleImage, s.ListStyleGradient) = (null, null);
                return;
            }
            (value, resolution) = chosen;
            lower = value.ToLowerInvariant();
        }
        s.ListStyleImageResolution = resolution;
        if (lower is "none" or "initial") (s.ListStyleImage, s.ListStyleGradient) = (null, null);
        else if (CssBackground.Url(value) is { } listImage) (s.ListStyleImage, s.ListStyleGradient) = (listImage, null);
        else if (lower.Contains("gradient(", StringComparison.Ordinal))
            // A gradient has no size of its own: it is drawn as a small square, as in Chrome; one that does not parse
            // leaves the type to show, like an image that fails to load.
            (s.ListStyleImage, s.ListStyleGradient) = (null, CssGradient.Parse(value, s.Color, s.FontSize, _rootFontSize, _vw, _vh));
    }
}
