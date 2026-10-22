namespace PdfPecker.Fluent;

/// <summary>
/// Common paper sizes, in portrait. Use <see cref="PageSize.Landscape"/> for landscape:
/// <c>page.Size(PageSizes.A4.Landscape)</c>.
/// </summary>
public static class PageSizes
{
    /// <summary>A0, 841 × 1189 mm.</summary>
    public static PageSize A0 { get; } = PageSize.FromMillimeters(841, 1189);

    /// <summary>A1, 594 × 841 mm.</summary>
    public static PageSize A1 { get; } = PageSize.FromMillimeters(594, 841);

    /// <summary>A2, 420 × 594 mm.</summary>
    public static PageSize A2 { get; } = PageSize.FromMillimeters(420, 594);

    /// <summary>A3, 297 × 420 mm.</summary>
    public static PageSize A3 { get; } = PageSize.A3;

    /// <summary>A4, 210 × 297 mm.</summary>
    public static PageSize A4 { get; } = PageSize.A4;

    /// <summary>A5, 148 × 210 mm.</summary>
    public static PageSize A5 { get; } = PageSize.A5;

    /// <summary>A6, 105 × 148 mm.</summary>
    public static PageSize A6 { get; } = PageSize.FromMillimeters(105, 148);

    /// <summary>B4, 250 × 353 mm.</summary>
    public static PageSize B4 { get; } = PageSize.FromMillimeters(250, 353);

    /// <summary>B5, 176 × 250 mm.</summary>
    public static PageSize B5 { get; } = PageSize.FromMillimeters(176, 250);

    /// <summary>US Letter, 8.5 × 11 inches.</summary>
    public static PageSize Letter { get; } = PageSize.Letter;

    /// <summary>US Legal, 8.5 × 14 inches.</summary>
    public static PageSize Legal { get; } = PageSize.Legal;

    /// <summary>Tabloid, 11 × 17 inches.</summary>
    public static PageSize Tabloid { get; } = new(792, 1224);

    /// <summary>Executive, 7.25 × 10.5 inches.</summary>
    public static PageSize Executive { get; } = new(522, 756);
}
