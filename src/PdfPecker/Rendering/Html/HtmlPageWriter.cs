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
    /// <summary>How many times larger than on the page the text layer lays out its lines before scaling them down.</summary>
    private const double LineScale = 10;

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
        // Each line is a box whose words are stretched to their glyphs in the image; kerning, ligatures and spacing other
        // than the layer's own would change their widths. Every word is isolated, so the browser lays out the words of
        // right-to-left text in the order they are written in, where the layer has placed them.
        sb.Append(".pdf-text>span{position:absolute;white-space:pre;transform-origin:0 ")
            .Append(Number(TextLayerFont.Ascent)).Append("em;font-kerning:none;font-variant-ligatures:none;letter-spacing:0;word-spacing:0}\n");
        sb.Append(".pdf-text>span>span{unicode-bidi:isolate;transform-origin:0 0}\n");
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
    /// A line of the text layer: a box at the start of the line's baseline, turned with the line, holding a span per word,
    /// each followed by the space that separates it from the next, so copied text keeps its spaces, and a line break
    /// after the box.
    /// </summary>
    /// <remarks>
    /// Browsers search for a phrase only within one block of text, and every absolutely positioned box is a block of
    /// its own, so the words of a line flow inline in one box for a search to find words that follow each other. A word
    /// is stretched with letter spacing to reach the next word, as an inline box cannot be scaled, and a margin moves it
    /// to its place where the gap before it is wider than a space or its words go right to left.
    /// <para>
    /// Browsers put each glyph on a 64th of a pixel, and with letter spacing those steps add up along a line to a pixel
    /// or two. A line is laid out <see cref="LineScale"/> times larger and scaled down, which shrinks them as much.
    /// </para>
    /// </remarks>
    private static void WriteLine(StringBuilder sb, PdfTextLine line, double width, double height)
    {
        var words = line.Words;
        int first = -1;
        double lineSize = 0;
        for (int i = 0; i < words.Count; i++)
        {
            if (TextLayerFont.Ems(words[i].Text) == 0 || !(words[i].FontSize > 0)) continue;
            if (first < 0) first = i;
            lineSize = Math.Max(lineSize, words[i].FontSize);
        }
        if (first < 0)
        {
            sb.Append("<br>\n");
            return;
        }
        bool rotated = Math.Abs(line.Angle) > 1e-6;
        double rad = line.Angle * Math.PI / 180, dx = Math.Cos(rad), dy = -Math.Sin(rad); // along the baseline, y down
        var start = words[first];
        // The box's line height is its font size, the largest of the line, which puts the baseline of its words an
        // ascent below its top whatever their sizes.
        sb.Append("<span style=\"left:").Append(Percent(start.OriginX, width))
            .Append(";top:").Append(Percent(start.OriginY - TextLayerFont.Ascent * lineSize * LineScale, height))
            .Append(";font-size:").Append(Cqw(lineSize, width)).Append(";transform:");
        if (rotated) sb.Append("rotate(").Append(Number(-line.Angle)).Append("deg) ");
        sb.Append("scale(").Append(Number(1 / LineScale)).Append(")\">");
        double pen = 0; // where the previous word ends, along the baseline from the start of the box
        for (int i = first; i < words.Count; i++)
        {
            var word = words[i];
            int ems = TextLayerFont.Ems(word.Text);
            if (ems == 0 || !(word.FontSize > 0)) continue;
            double size = word.FontSize, advance = word.Advance;
            bool last = i == words.Count - 1;
            if (!last)
            {
                // The word and its space reach to where the next word starts, so their selections meet without
                // overlapping. A next word that is not ahead on the baseline, as in right-to-left lines, or far away
                // leaves the space squeezed into the word.
                var next = words[i + 1];
                double reach = (next.OriginX - word.OriginX) * dx + (next.OriginY - word.OriginY) * dy;
                if (reach > advance && reach - advance < 3 * size) advance = reach;
            }
            int characters = last ? ems : ems + 1;
            double natural = characters * size;
            double ox = word.OriginX - start.OriginX, oy = word.OriginY - start.OriginY;
            double along = ox * dx + oy * dy, across = oy * dx - ox * dy;
            double margin = along - pen;
            // Letter spacing follows every character, which a word of single characters in a script whose letters do
            // not join stretches evenly. Browsers leave it out where letters join, as in Arabic, and between a letter
            // and its marks, so other words are scaled instead, as boxes of their own that a phrase search stops at.
            bool spaced = LetterSpaced(word.Text);
            pen = along + (spaced ? advance : natural);
            sb.Append("<span style=\"");
            int styleStart = sb.Length;
            if (Math.Abs(size - lineSize) > 1e-6) sb.Append("font-size:").Append(Cqw(size, width)).Append(';');
            if (Math.Abs(margin) > 1e-3) sb.Append("margin-left:").Append(Cqw(margin, width)).Append(';');
            if (Math.Abs(across) > 1e-3) sb.Append("position:relative;top:").Append(Cqw(across, width)).Append(';');
            if (spaced)
            {
                double spacing = (advance - natural) / characters / size;
                if (Math.Abs(spacing) > 1e-6) sb.Append("letter-spacing:").Append(Number(spacing)).Append("em;");
            }
            else if (Math.Abs(advance - natural) > 1e-6 * natural)
                sb.Append("display:inline-block;transform:scaleX(").Append(Number(advance / natural)).Append(");");
            // A word that needs no style has no style attribute.
            if (sb.Length == styleStart) sb.Length -= " style=\"".Length;
            else
            {
                sb.Length--;
                sb.Append('"');
            }
            sb.Append('>');
            AppendEscaped(sb, word.Text, attribute: false);
            if (!last) sb.Append(' ');
            sb.Append("</span>");
        }
        sb.Append("</span><br>\n");
    }

    /// <summary>
    /// Whether every character of <paramref name="text"/> takes letter spacing of its own in browsers: letters, digits,
    /// punctuation and symbols of the Basic Multilingual Plane that are neither marks nor parts of a cluster, in scripts
    /// whose letters do not join.
    /// </summary>
    private static bool LetterSpaced(string text)
    {
        foreach (char c in text)
        {
            // Arabic, Syriac, Thaana, N'Ko, Mandaic and their extensions and presentation forms; Mongolian; Hangul jamo,
            // which join into syllables; and Thai and Lao sara am, which belong to the letter before them.
            if (c is (>= '\u0600' and <= '\u08FF') or (>= '\u1800' and <= '\u18AF') or (>= '\u1100' and <= '\u11FF')
                or (>= '\uA960' and <= '\uA97F') or (>= '\uD7B0' and <= '\uD7FF') or (>= '\uFB50' and <= '\uFDFF')
                or (>= '\uFE70' and <= '\uFEFF') or '\u0E33' or '\u0EB3')
                return false;
            switch (CharUnicodeInfo.GetUnicodeCategory(c))
            {
                case UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                    or UnicodeCategory.Format or UnicodeCategory.Control or UnicodeCategory.Surrogate
                    or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned:
                    return false;
            }
        }
        return true;
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

    /// <summary>
    /// A length in points as container query units of a page <paramref name="width"/> points wide, in a line laid out
    /// <see cref="LineScale"/> times larger.
    /// </summary>
    private static string Cqw(double value, double width) => Number(value * LineScale / width * 100) + "cqw";

    /// <summary>A number for CSS, with at most four decimals and no exponent.</summary>
    public static string Number(double value)
    {
        if (!double.IsFinite(value)) return "0";
        string s = Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}
