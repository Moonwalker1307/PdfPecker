namespace PdfPecker;

/// <summary>
/// A text or image stamped on every page, for example a diagonal "DRAFT" or "PRØVEBEREGNING".
/// Set <see cref="Text"/>, <see cref="Image"/> or both (the text is drawn over the image).
/// </summary>
public sealed class PdfWatermark
{
    /// <summary>The watermark text. Use "\n" for more than one line.</summary>
    public string? Text { get; set; }

    /// <summary>A PNG or JPEG image to stamp, centred on the page.</summary>
    public byte[]? Image { get; set; }

    /// <summary>Width of the image in points. Default 0: half the page width, keeping the aspect ratio.</summary>
    public float ImageWidth { get; set; }

    /// <summary>Font size in points. Default 0: as large as fits across the page at the chosen angle.</summary>
    public float FontSize { get; set; }

    /// <summary>
    /// CSS font-family list for the text, e.g. "'Open Sans', sans-serif". Registered and @font-face fonts are embedded.
    /// Default: the document body's font family when it names an embedded font, otherwise Helvetica.
    /// </summary>
    public string? FontFamily { get; set; }

    /// <summary>
    /// Extra space after each character in points, like CSS letter-spacing, to spread the letters out. Default: 0.
    /// </summary>
    public float LetterSpacing { get; set; }

    /// <summary>Use the bold font. Default: true.</summary>
    public bool Bold { get; set; } = true;

    /// <summary>Text colour as a CSS colour (e.g. "#c00", "rgb(200,0,0)", "gray"). Default: "#808080".</summary>
    public string Color { get; set; } = "#808080";

    /// <summary>Opacity from 0 (invisible) to 1 (solid). Default: 0.3.</summary>
    public float Opacity { get; set; } = 0.3f;

    /// <summary>
    /// Counter-clockwise rotation in degrees. Default null: along the page diagonal from bottom left to top right for text,
    /// and no rotation for an image on its own.
    /// </summary>
    public float? Angle { get; set; }

    /// <summary>Draw the watermark behind the page content instead of over it. Default: false (over the content).</summary>
    public bool Behind { get; set; }
}
