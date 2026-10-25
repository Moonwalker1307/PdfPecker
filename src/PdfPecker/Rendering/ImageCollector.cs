using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering;

/// <summary>
/// One image a page draws, for image extraction: the image and the transform of its unit square to points on the
/// displayed page, origin top left.
/// </summary>
internal readonly struct ImageDraw
{
    public ImageDraw(PdfStream? xobject, PdfDict dict, byte[]? inlineData, PdfDict? resources, in Matrix ctm,
        (double X0, double Y0, double X1, double Y1)? clip = null)
    {
        Clip = clip;
        XObject = xobject;
        Dict = dict;
        InlineData = inlineData;
        Resources = resources;
        Ctm = ctm;
    }

    /// <summary>The image XObject, which other draws of the same image share; null for an inline image.</summary>
    public PdfStream? XObject { get; }

    /// <summary>The image dictionary: the XObject's, or the inline image's.</summary>
    public PdfDict Dict { get; }

    /// <summary>The data of an inline image; null for an XObject.</summary>
    public byte[]? InlineData { get; }

    /// <summary>The resources the image was drawn with, where named colour spaces are looked up.</summary>
    public PdfDict? Resources { get; }

    public Matrix Ctm { get; }

    /// <summary>The box the clips in force leave visible, when the collector keeps clips; null for none.</summary>
    public (double X0, double Y0, double X1, double Y1)? Clip { get; }
}

/// <summary>Receives the images <see cref="PageRenderer"/> draws when it runs a page for its images instead of pixels.</summary>
internal sealed class ImageCollector
{
    /// <summary>
    /// Draws kept per page. A small content stream can draw one image through forms millions of times, so this bounds
    /// the memory one page can take; the draws past it are not reported.
    /// </summary>
    public const int MaxDraws = 100_000;

    public readonly List<ImageDraw> Draws = new();

    /// <summary>Whether each draw keeps the box its clips leave visible, as a picture cropped to a card is drawn.</summary>
    public readonly bool Clips;

    public ImageCollector(bool clips = false) => Clips = clips;

    public void Add(PdfStream? xobject, PdfDict dict, byte[]? inlineData, PdfDict? resources, in Matrix ctm,
        (double X0, double Y0, double X1, double Y1)? clip = null)
    {
        if (Draws.Count < MaxDraws) Draws.Add(new ImageDraw(xobject, dict, inlineData, resources, ctm, clip));
    }
}
