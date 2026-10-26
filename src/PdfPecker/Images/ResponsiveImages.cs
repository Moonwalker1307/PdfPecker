using System.Globalization;
using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Images;

/// <summary>
/// Chooses the picture of each img that has a srcset or sits in a picture element (HTML, "Images"), as Chrome does when
/// it prints: a picture's first source whose media and type match, else the img's own srcset and src, and from that set
/// the candidate nearest to one device pixel per CSS pixel. Chrome chooses as the page loads on screen and keeps the
/// choice when it prints, so media queries here see the screen media type (sized like the page area), not print. The choice and its pixel density are kept in the
/// <see cref="ImageLoader"/>, so an img keeps its src attribute for selectors and its size follows the density.
/// </summary>
internal static class ResponsiveImages
{
    /// <summary>
    /// The device pixels per CSS pixel the choice aims at: Chrome prints with the 1x candidate of a set, so the same
    /// picture lands in the PDF.
    /// </summary>
    private const float TargetDensity = 1;

    /// <summary>The range of densities a candidate may have; others are dropped.</summary>
    private const float MinDensity = 1e-6f, MaxDensity = 1e6f;

    /// <summary>Candidates read from one srcset, and descriptors read from one candidate, at most.</summary>
    private const int MaxCandidates = 64;

    /// <summary>
    /// Chooses the pictures of the img elements under <paramref name="root"/> for <paramref name="media"/>, with
    /// <paramref name="vw"/> and <paramref name="vh"/> (1vw and 1vh in points) for the lengths in sizes, and keeps
    /// them in <paramref name="images"/>.
    /// </summary>
    public static void Prepare(Element root, ImageLoader images, MediaContext media, float vw, float vh)
    {
        foreach (var c in root.Children)
        {
            if (c is not Element e) continue;
            if (e.Name == "img")
            {
                if (Choose(e, media, vw, vh) is { } chosen) images.Chosen[e] = chosen;
            }
            else Prepare(e, images, media, vw, vh);
        }
    }

    /// <summary>The source and density chosen for an img, or null when its src attribute stands.</summary>
    private static (string Url, float Density)? Choose(Element img, MediaContext media, float vw, float vh)
    {
        string? srcset = null, sizes = null;
        // In a picture element, the first source before the img whose media and type match supplies the set.
        if (img.Parent is { Name: "picture" } picture)
        {
            foreach (var n in picture.Children)
            {
                if (n == img) break;
                if (n is not Element { Name: "source" } source || source.GetAttribute("srcset") is not { } set) continue;
                if (source.GetAttribute("media") is { } m && !MediaQuery.Matches(m, media)) continue;
                if (source.GetAttribute("type") is { } type && !IsDecodedType(type)) continue;
                srcset = set;
                sizes = source.GetAttribute("sizes");
                break;
            }
        }
        string? src = null;
        if (srcset == null)
        {
            srcset = img.GetAttribute("srcset");
            if (srcset == null) return null;
            sizes = img.GetAttribute("sizes");
            src = img.GetAttribute("src");
        }

        var candidates = Parse(srcset);
        float sourceSize = float.NaN;
        bool widths = false;
        foreach (var c in candidates) widths |= c.Width > 0;
        if (widths) sourceSize = SourceSize(sizes, media, vw, vh);
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (c.Width > 0) candidates[i] = c with { Density = c.Width / sourceSize };
        }
        // The img's src stands for 1x, unless the set already has a 1x candidate or describes widths.
        if (!string.IsNullOrWhiteSpace(src) && !widths && !candidates.Exists(c => c.Density == 1))
            candidates.Add(new Candidate(src!.Trim(), 1, 0));
        // A density near zero would make the picture astronomically large, and one near infinity would make it vanish.
        candidates.RemoveAll(c => !(c.Density >= MinDensity && c.Density <= MaxDensity));
        if (candidates.Count == 0) return null;

        // Stable, so the first of two candidates with the same density wins.
        var sorted = candidates.OrderBy(c => c.Density).ToList();
        int pick = sorted.FindIndex(c => c.Density >= TargetDensity);
        if (pick < 0) pick = sorted.Count - 1;
        // Chrome takes the smaller of two neighbours while the target lies below their geometric mean.
        else if (pick > 0 && MathF.Sqrt(sorted[pick - 1].Density * sorted[pick].Density) > TargetDensity) pick--;
        return (sorted[pick].Url, sorted[pick].Density);
    }

    /// <summary>Whether a source's type attribute names a format PdfPecker decodes; an empty type matches any.</summary>
    private static bool IsDecodedType(string type)
    {
        var t = type.AsSpan().Trim();
        int semicolon = t.IndexOf(';');
        if (semicolon >= 0) t = t[..semicolon].TrimEnd();
        if (t.Length == 0) return true;
        foreach (var known in DecodedTypes)
            if (t.Equals(known, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly string[] DecodedTypes =
    [
        "image/jpeg", "image/jpg", "image/pjpeg", "image/png", "image/apng", "image/gif", "image/webp", "image/bmp",
        "image/x-ms-bmp", "image/tiff", "image/svg+xml",
    ];

    /// <summary>One image candidate: its URL and a density (an x descriptor) or a width (a w descriptor).</summary>
    private readonly record struct Candidate(string Url, float Density, float Width);

    /// <summary>
    /// Parses a srcset attribute as the HTML parsing algorithm does: a URL runs to the next white space, so a URL may
    /// contain commas (data URIs do), and a candidate with a descriptor it cannot read is dropped.
    /// </summary>
    private static List<Candidate> Parse(string srcset)
    {
        var list = new List<Candidate>();
        int i = 0, n = srcset.Length;
        while (i < n && list.Count < MaxCandidates)
        {
            while (i < n && (IsSpace(srcset[i]) || srcset[i] == ',')) i++;
            if (i >= n) break;
            int start = i;
            while (i < n && !IsSpace(srcset[i])) i++;
            var url = srcset.AsSpan(start, i - start);
            bool ended = false;
            if (url[^1] == ',')
            {
                url = url.TrimEnd(',');
                ended = true;
            }
            var descriptors = new List<string>();
            if (!ended)
            {
                // Descriptors run to the next comma outside parentheses.
                while (i < n)
                {
                    while (i < n && IsSpace(srcset[i])) i++;
                    if (i >= n) break;
                    if (srcset[i] == ',') { i++; break; }
                    int d = i, depth = 0;
                    while (i < n && (depth > 0 || !IsSpace(srcset[i]) && srcset[i] != ','))
                    {
                        if (srcset[i] == '(') depth++;
                        else if (srcset[i] == ')' && depth > 0) depth--;
                        i++;
                    }
                    if (descriptors.Count < MaxCandidates) descriptors.Add(srcset.Substring(d, i - d));
                }
            }
            if (url.Length > 0 && Descriptors(descriptors) is { } c) list.Add(new Candidate(url.ToString(), c.Density, c.Width));
        }
        return list;
    }

    /// <summary>The density or width the descriptors give (1x without any), or null when they are invalid.</summary>
    private static (float Density, float Width)? Descriptors(List<string> descriptors)
    {
        float density = float.NaN, width = float.NaN;
        bool height = false;
        foreach (var d in descriptors)
        {
            if (d.Length < 2) return null;
            var number = d.AsSpan(0, d.Length - 1);
            switch (d[^1])
            {
                case 'x' or 'X':
                    if (!float.IsNaN(density) || !float.IsNaN(width)
                        || !float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out density) || !(density >= 0)) return null;
                    break;
                case 'w' or 'W':
                    if (!float.IsNaN(density) || !float.IsNaN(width) || !Invariant.TryParseInt(number, out int w) || w <= 0) return null;
                    width = w;
                    break;
                case 'h' or 'H':
                    // A height alone says nothing the choice uses; it is allowed only beside a width.
                    if (height || !Invariant.TryParseInt(number, out int h) || h <= 0) return null;
                    height = true;
                    break;
                default: return null;
            }
        }
        if (height && float.IsNaN(width)) return null;
        return float.IsNaN(width) ? (float.IsNaN(density) ? 1 : density, 0) : (0, width);
    }

    /// <summary>
    /// The width in CSS pixels that a sizes attribute gives the image: the length of the first entry whose media
    /// condition matches, else 100vw.
    /// </summary>
    private static float SourceSize(string? sizes, MediaContext media, float vw, float vh)
    {
        if (sizes != null)
        {
            foreach (var part in CssParser.SplitTopLevel(sizes.ToLowerInvariant(), ','))
            {
                var entry = part.Trim();
                if (entry.Length == 0) continue;
                // The size is the entry's last component: a function such as calc(), or the last word.
                int split;
                if (entry[^1] == ')')
                {
                    int open = OpeningParen(entry);
                    if (open < 0) continue;
                    split = open;
                    while (split > 0 && (char.IsLetterOrDigit(entry[split - 1]) || entry[split - 1] == '-')) split--;
                    // A media condition alone, such as "(max-width: 600px)", has no size.
                    if (split == open) continue;
                }
                else split = entry.LastIndexOfAny([' ', '\t', '\n', '\r', '\f']) + 1;
                var value = entry[split..];
                var condition = entry[..split].Trim();
                if (value == "auto") return 100 * vw / 0.75f;
                if (!StyleResolver.TryParseLength(value, 12, 12, out var len, vw, vh) || len.Unit != LengthUnit.Pt || len.Value < 0) continue;
                if (condition.Length > 0 && !MediaQuery.Matches(condition, media)) continue;
                return len.Value / 0.75f;
            }
        }
        return 100 * vw / 0.75f;
    }

    /// <summary>The index of the parenthesis that the closing one at the end of <paramref name="s"/> matches, or -1.</summary>
    private static int OpeningParen(string s)
    {
        int depth = 0;
        for (int i = s.Length - 1; i >= 0; i--)
        {
            if (s[i] == ')') depth++;
            else if (s[i] == '(' && --depth == 0) return i;
        }
        return -1;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';
}
