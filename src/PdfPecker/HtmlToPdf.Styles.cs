using System.Buffers;
using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker;

public static partial class HtmlToPdf
{
    // Either slash ends the directory part of a style sheet's path, so relative imports work with Windows paths too.
    private static readonly SearchValues<char> PathSeparators = SearchValues.Create("/\\");

    private static Stylesheet ReadStyles(Element doc, MediaContext media, PdfOptions options, string? baseDirectory, Func<string, byte[]?>? resolver)
    {
        var sheet = new Stylesheet { Media = media, ShareDeclarations = true };
        bool allowFiles = options.AllowFileAccess;
        sheet.ImportLoader = (url, importer) =>
        {
            // A URL written as a plain string is relative to the importing sheet; url() ones were rebased with it.
            var href = url.Trim();
            if (importer != null && !IsAbsoluteReference(href))
            {
                int slash = importer.AsSpan().LastIndexOfAny(PathSeparators);
                if (slash >= 0 && !IsAbsoluteReference(importer)) href = string.Concat(importer.AsSpan(0, slash + 1), href);
            }
            var css = LoadText(href, baseDirectory, resolver, allowFiles);
            return css == null ? null : (RebaseUrls(css, href), href);
        };
        CollectStyles(doc, sheet, baseDirectory, resolver, allowFiles);
        if (!string.IsNullOrEmpty(options.UserStyleSheet)) CssParser.ParseStylesheet(options.UserStyleSheet!, sheet);
        return sheet;
    }

    private static void CollectStyles(Element parent, Stylesheet sheet, string? baseDirectory, Func<string, byte[]?>? resolver, bool allowFiles)
    {
        foreach (var child in parent.Children)
        {
            if (child is not Element e) continue;
            if (e.Name == "style")
            {
                var media = e.GetAttribute("media");
                if (media == null || MediaQuery.Matches(media, sheet.Media)) CssParser.ParseStylesheet(e.InnerText(), sheet);
                continue;
            }
            if (e.Name == "link" && (e.GetAttribute("rel") ?? "").IndexOf("stylesheet", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var media = e.GetAttribute("media");
                var href = e.GetAttribute("href");
                var css = media == null || MediaQuery.Matches(media, sheet.Media) ? LoadText(href, baseDirectory, resolver, allowFiles) : null;
                if (css != null) CssParser.ParseStylesheet(RebaseUrls(css, href!), sheet, href);
                continue;
            }
            CollectStyles(e, sheet, baseDirectory, resolver, allowFiles);
        }
    }

    /// <summary>
    /// Makes the relative url() references of a linked stylesheet relative to the document, as they are relative to the
    /// stylesheet itself: url(img/bg.png) in css/site.css becomes url("css/img/bg.png").
    /// </summary>
    internal static string RebaseUrls(string css, string href)
    {
        int slash = href.AsSpan().LastIndexOfAny(PathSeparators);
        if (slash < 0 || css.IndexOf("url(", StringComparison.OrdinalIgnoreCase) < 0) return css;
        if (IsAbsoluteReference(href)) return css; // remote sheets only load through the resolver, which sees the URLs as written
        var folder = href.Substring(0, slash + 1);
        var sb = new System.Text.StringBuilder(css.Length + 64);
        int copied = 0, pos = 0;
        // Closing quote positions only move forward: caching them keeps the scan linear on hostile input like "url('url('".
        int nextDouble = -2, nextSingle = -2;
        int Next(ref int cache, char c, int from)
        {
            if (cache != -1 && cache < from) cache = css.IndexOf(c, from);
            return cache;
        }
        while ((pos = css.IndexOf("url(", pos, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int start = pos + 4, i = start;
            pos = start;
            while (i < css.Length && char.IsWhiteSpace(css[i])) i++;
            if (i >= css.Length) break;
            char q = css[i] is '"' or '\'' ? css[i] : '\0';
            int urlStart = q == '\0' ? i : i + 1, urlEnd, close;
            if (q == '\0')
            {
                // Unquoted: up to ")" with no whitespace, quotes or "(" inside; the scan stops at the first bad character.
                urlEnd = urlStart;
                while (urlEnd < css.Length && css[urlEnd] is not (')' or '(' or '"' or '\'') && !char.IsWhiteSpace(css[urlEnd])) urlEnd++;
                close = urlEnd;
                while (close < css.Length && char.IsWhiteSpace(css[close])) close++;
                if (close >= css.Length || css[close] != ')') continue;
            }
            else
            {
                urlEnd = q == '"' ? Next(ref nextDouble, '"', urlStart) : Next(ref nextSingle, '\'', urlStart);
                if (urlEnd < 0) continue;
                close = urlEnd + 1;
                while (close < css.Length && char.IsWhiteSpace(css[close])) close++;
                if (close >= css.Length || css[close] != ')') continue;
            }
            var url = css.Substring(urlStart, urlEnd - urlStart);
            if (url.Length == 0 || IsAbsoluteReference(url) || url.IndexOf('"') >= 0) continue;
            sb.Append(css, copied, pos - 4 - copied).Append("url(\"").Append(folder).Append(url).Append("\")");
            copied = pos = close + 1;
        }
        if (copied == 0) return css;
        return sb.Append(css, copied, css.Length - copied).ToString();
    }

    // A scheme (data:, http:, file:, C:), a rooted path or a fragment.
    private static bool IsAbsoluteReference(string url)
        => url.Length == 0 || url[0] is '/' or '\\' or '#' || HasScheme(url);

    /// <summary>
    /// Whether the reference starts with a URL scheme and its colon: a letter, then letters, digits, '+', '.' or '-'.
    /// Checked by hand rather than with a regular expression, whose engine takes milliseconds to start on the first
    /// conversion.
    /// </summary>
    private static bool HasScheme(string url)
    {
        if (url.Length == 0 || !char.IsAsciiLetter(url[0])) return false;
        for (int i = 1; i < url.Length; i++)
        {
            char c = url[i];
            if (c == ':') return true;
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '.' or '-')) return false;
        }
        return false;
    }

    private static string? LoadText(string? href, string? baseDirectory, Func<string, byte[]?>? resolver, bool allowFiles)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        try
        {
            // A data URI is the sheet itself, as browsers read it; it needs no resolver or file access.
            if (href!.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return Images.ImageLoader.DecodeDataUri(href) is { } data ? System.Text.Encoding.UTF8.GetString(data) : null;
            var bytes = resolver?.Invoke(href);
            if (bytes != null) return System.Text.Encoding.UTF8.GetString(bytes);
            if (href!.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return null;
            var path = LocalFiles.Resolve(href, baseDirectory, allowFiles);
            if (path == null || LocalFiles.Read(path) is not { } file) return null;
            // As File.ReadAllText reads it: UTF-8 unless a byte order mark says otherwise.
            using var reader = new StreamReader(new MemoryStream(file), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException)
        {
            return null;
        }
    }
}
