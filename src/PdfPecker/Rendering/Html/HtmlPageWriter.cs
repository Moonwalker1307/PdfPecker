using System.Globalization;
using System.Text;

namespace PdfPecker.Rendering.Html;

/// <summary>
/// Writes the HTML of PDF pages: each page is a box with the page's image, an invisible text layer over it in which
/// every word covers its glyphs in the image, and its links.
/// </summary>
/// <remarks>
/// Positions are percentages of the page and font sizes are container query units (<c>cqw</c>), so a page scales to
/// any width, such as a phone screen, and its text layer scales with it.
/// </remarks>
internal static class HtmlPageWriter
{
    /// <summary>The class names and style sheet the pages rely on, without the page size rules.</summary>
    public static void WriteStyles(StringBuilder sb)
    {
        sb.Append("@font-face{font-family:\"PdfPecker Text Layer\";src:url(").Append(TextLayerFont.DataUri).Append(") format(\"truetype\")}\n");
        sb.Append("html{background:#e8e8e8}\n");
        sb.Append("body{margin:0;padding:16px 8px}\n");
        sb.Append(".pdf-page{position:relative;max-width:100%;margin:0 auto 16px;background:#fff;box-shadow:0 1px 4px rgba(0,0,0,.3);overflow:hidden;container-type:inline-size}\n");
        sb.Append(".pdf-page>img{position:absolute;left:0;top:0;width:100%;height:100%;user-select:none;-webkit-user-select:none}\n");
        // The line breaks between lines are in the layer's own flow; at size 0 they take no room and show no selection.
        sb.Append(".pdf-text{position:absolute;inset:0;overflow:hidden;font-size:0;line-height:1;font-family:\"PdfPecker Text Layer\";color:transparent;forced-color-adjust:none}\n");
        // Each word is stretched to its glyphs in the image; kerning, ligatures and spacing would change its width.
        sb.Append(".pdf-text>span{position:absolute;white-space:pre;transform-origin:0 ")
            .Append(Number(TextLayerFont.Ascent)).Append("em;font-kerning:none;font-variant-ligatures:none;letter-spacing:0;word-spacing:0}\n");
        // Word selections touch and can overlap a little, as browsers widen a selection at the end of each span. An
        // opaque colour multiplied onto the page looks the same where they overlap, which a translucent one would not.
        sb.Append(".pdf-text{mix-blend-mode:multiply}\n");
        sb.Append(".pdf-text ::selection{background:#b3d4fc;color:transparent}\n");
        sb.Append(".pdf-link{position:absolute}\n");
        sb.Append("@page{margin:0}\n");
        sb.Append("@media print{html{background:none}body{padding:0}.pdf-page{margin:0;max-width:none;box-shadow:none;break-after:page}}\n");
    }

    /// <summary>A page's box with its image, text and links.</summary>
    /// <param name="sb">Receives the HTML.</param>
    /// <param name="pageNumber">The page number, from 1, which gives the box its id; 0 for a box without an id.</param>
    /// <param name="width">The page width in points, as displayed.</param>
    /// <param name="height">The page height in points, as displayed.</param>
    /// <param name="pageName">The CSS named page that sets the paper size when printing.</param>
    /// <param name="mediaType">The image's media type.</param>
    /// <param name="image">The encoded page image.</param>
    /// <param name="imageWidth">The image width in pixels.</param>
    /// <param name="imageHeight">The image height in pixels.</param>
    /// <param name="text">The page's text, or null for no text layer.</param>
    /// <param name="links">The page's links.</param>
    /// <param name="pageIds">Maps a page index to its page number in the output, or 0 when the page is left out.</param>
    public static void WritePage(StringBuilder sb, int pageNumber, double width, double height, string pageName, string mediaType,
        byte[] image, int imageWidth, int imageHeight, PdfTextPage? text, List<PageLink> links, Func<int, int> pageIds)
    {
        sb.Append("<div class=\"pdf-page\"");
        if (pageNumber > 0) sb.Append(" id=\"page-").Append(pageNumber.ToString(CultureInfo.InvariantCulture)).Append('"');
        sb.Append(" style=\"width:").Append(Number(width)).Append("pt;aspect-ratio:").Append(Number(width)).Append('/').Append(Number(height))
            .Append(";page:").Append(pageName).Append("\">\n");
        sb.Append("<img src=\"data:").Append(mediaType).Append(";base64,").Append(Convert.ToBase64String(image))
            .Append("\" width=\"").Append(imageWidth.ToString(CultureInfo.InvariantCulture))
            .Append("\" height=\"").Append(imageHeight.ToString(CultureInfo.InvariantCulture)).Append("\" alt=\"\" draggable=\"false\">\n");
        if (text != null && text.Lines.Count > 0)
        {
            sb.Append("<div class=\"pdf-text\">");
            foreach (var line in text.Lines) WriteLine(sb, line, width, height);
            sb.Append("</div>\n");
        }
        foreach (var link in links)
        {
            string? href = link.Uri;
            if (href == null)
            {
                int target = pageIds(link.Page);
                if (target == 0) continue;
                href = "#page-" + target.ToString(CultureInfo.InvariantCulture);
            }
            sb.Append("<a class=\"pdf-link\" href=\"");
            AppendEscaped(sb, href, attribute: true);
            sb.Append('"');
            if (link.Uri != null) sb.Append(" rel=\"noopener noreferrer\"");
            sb.Append(" style=\"left:").Append(Percent(link.X, width)).Append(";top:").Append(Percent(link.Y, height))
                .Append(";width:").Append(Percent(link.Width, width)).Append(";height:").Append(Percent(link.Height, height)).Append("\"></a>\n");
        }
        sb.Append("</div>\n");
    }

    /// <summary>
    /// A line of the text layer: a span per word, each followed by the space that separates it from the next, so copied
    /// text keeps its spaces, and a line break after the last.
    /// </summary>
    private static void WriteLine(StringBuilder sb, PdfTextLine line, double width, double height)
    {
        var words = line.Words;
        bool rotated = Math.Abs(line.Angle) > 1e-6;
        double rad = line.Angle * Math.PI / 180, dx = Math.Cos(rad), dy = -Math.Sin(rad); // along the baseline, y down
        for (int i = 0; i < words.Count; i++)
        {
            var word = words[i];
            int ems = TextLayerFont.Ems(word.Text);
            if (ems == 0 || !(word.FontSize > 0)) continue;
            double size = word.FontSize, advance = word.Advance;
            bool space = i < words.Count - 1;
            if (space)
            {
                // The word and its space reach to where the next word starts, so their selections meet without
                // overlapping. A next word that is not ahead on the baseline, as in right-to-left lines, or far away
                // leaves the space with no width.
                var next = words[i + 1];
                double reach = (next.OriginX - word.OriginX) * dx + (next.OriginY - word.OriginY) * dy;
                if (reach > advance && reach - advance < 3 * size) { advance = reach; ems++; }
                else space = false;
            }
            double scale = advance > 0 ? advance / (ems * size) : 1;
            sb.Append("<span style=\"left:").Append(Percent(word.OriginX, width))
                .Append(";top:").Append(Percent(word.OriginY - TextLayerFont.Ascent * size, height))
                .Append(";font-size:").Append(Number(size / width * 100)).Append("cqw");
            bool scaled = Math.Abs(scale - 1) > 1e-4;
            if (rotated || scaled)
            {
                sb.Append(";transform:");
                if (rotated) sb.Append("rotate(").Append(Number(-line.Angle)).Append("deg)");
                if (rotated && scaled) sb.Append(' ');
                if (scaled) sb.Append("scaleX(").Append(Number(scale)).Append(')');
            }
            if (!space && i < words.Count - 1) sb.Append(";word-spacing:-1em");
            sb.Append("\">");
            AppendEscaped(sb, word.Text, attribute: false);
            if (i < words.Count - 1) sb.Append(' ');
            sb.Append("</span>");
        }
        sb.Append("<br>\n");
    }

    /// <summary>
    /// Appends text for HTML content or a quoted attribute. Control characters, unpaired surrogates and noncharacters,
    /// which a damaged PDF can put in its text, are left out.
    /// </summary>
    public static void AppendEscaped(StringBuilder sb, string s, bool attribute)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    int cp = char.ConvertToUtf32(c, s[i + 1]);
                    if ((cp & 0xFFFE) != 0xFFFE) sb.Append(c).Append(s[i + 1]);
                    i++;
                }
                continue;
            }
            if (char.IsLowSurrogate(c) || c < ' ' || c is >= '\u007F' and <= '\u009F' or '￾' or '￿') continue;
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"' when attribute: sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    private static string Percent(double value, double of) => Number(value / of * 100) + "%";

    /// <summary>A number for CSS, with at most four decimals and no exponent.</summary>
    public static string Number(double value)
    {
        if (!double.IsFinite(value)) return "0";
        string s = Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}
