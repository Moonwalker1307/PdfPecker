using System.Globalization;
using System.Text;

namespace PdfPecker.Cli;

/// <summary>
/// The options of <c>convert</c> and <c>watch</c>. They follow wkhtmltopdf's names where it has the same setting, so
/// scripts written for it keep working; wkhtmltopdf options that PdfPecker has no use for (it runs no scripts and fetches
/// nothing) are accepted and ignored with a warning, so a script does not break on them.
/// </summary>
internal static class ConvertOptions
{
    // Settings wkhtmltopdf has that depend on a browser engine, JavaScript or the network. name, arity.
    private static readonly (string Names, int Arity)[] Ignored =
    {
        ("grayscale|g", 0), ("lowquality|l", 0), ("collate", 0), ("no-collate", 0), ("print-media-type", 0),
        ("no-print-media-type", 0), ("background", 0), ("no-background", 0), ("images", 0), ("no-images", 0),
        ("disable-javascript", 0), ("enable-javascript", 0), ("stop-slow-scripts", 0), ("no-stop-slow-scripts", 0),
        ("enable-plugins", 0), ("disable-plugins", 0), ("enable-external-links", 0), ("disable-external-links", 0),
        ("enable-internal-links", 0), ("disable-internal-links", 0), ("enable-toc-back-links", 0),
        ("disable-toc-back-links", 0), ("exclude-from-outline", 0), ("include-in-outline", 0),
        ("debug-javascript", 0), ("no-debug-javascript", 0), ("enable-forms", 0), ("disable-forms", 0),
        ("custom-header-propagation", 0), ("no-custom-header-propagation", 0), ("footer-line", 0),
        ("no-footer-line", 0), ("header-line", 0), ("no-header-line", 0), ("toc", 0), ("keep-relative-links", 0),
        ("dpi", 1), ("image-dpi", 1), ("image-quality", 1), ("copies", 1), ("encoding", 1), ("minimum-font-size", 1),
        ("javascript-delay", 1), ("window-status", 1), ("username", 1), ("password", 1), ("proxy|p", 1),
        ("load-error-handling", 1), ("load-media-error-handling", 1), ("viewport-size", 1), ("page-offset", 1),
        ("header-html", 1), ("footer-html", 1), ("header-spacing", 1), ("footer-spacing", 1), ("cache-dir", 1),
        ("checkbox-svg", 1), ("checkbox-checked-svg", 1), ("radiobutton-svg", 1), ("radiobutton-checked-svg", 1),
        ("allow", 1), ("run-script", 1), ("bypass-proxy-for", 1), ("cover", 1), ("toc-header-text", 1),
        ("cookie", 2), ("custom-header", 2), ("post", 2), ("post-file", 2), ("replace", 2),
    };

    private static readonly HashSet<string> IgnoredNames = new(Ignored.Select(i => i.Names.Split('|')[0]), StringComparer.Ordinal);

    public static OptionSet Create()
    {
        var set = new OptionSet()
            .Value("output|o")
            .Value("page-size|s").Value("page-width").Value("page-height")
            .Value("orientation|O").Flag("landscape").Flag("portrait")
            .Value("margin").Value("margin-top|T").Value("margin-right|R").Value("margin-bottom|B").Value("margin-left|L")
            .Value("title").Value("author").Value("subject").Value("keywords")
            .Value("zoom|scale").Flag("no-shrink-to-fit|disable-smart-shrinking").Flag("enable-smart-shrinking")
            .Flag("no-pdf-compression").Flag("outline").Flag("no-outline|no-bookmarks")
            .Value("user-style-sheet|css").Value("base-dir")
            .Flag("allow-file-access|enable-local-file-access").Flag("disable-local-file-access")
            .Flag("markdown").Flag("html")
            .Flag("quiet|q")
            .Value("header-left").Value("header-center").Value("header-right")
            .Value("footer-left").Value("footer-center").Value("footer-right")
            .Value("header-font-size").Value("footer-font-size").Value("header-font-name").Value("footer-font-name")
            .Value("font").Value("font-dir").Flag("system-fonts")
            .Value("pdfa").Value("accessibility").Value("lang")
            .Value("user-password").Value("owner-password").Value("user-password-env").Value("owner-password-env").Flag("linearize")
            .Value("creation-date");
        foreach (var (names, arity) in Ignored) set.Add(names, arity);
        return set;
    }

    /// <summary>Warns about each wkhtmltopdf option that was given and has no effect here.</summary>
    public static void WarnAboutIgnored(CliIo io, ParsedArgs a)
    {
        foreach (var name in a.Seen.Distinct(StringComparer.Ordinal))
        {
            if (!IgnoredNames.Contains(name)) continue;
            io.Warn($"--{name} is not supported by PdfPecker and was ignored.");
        }
    }

    /// <summary>Builds the library options from the command line. <paramref name="wkDefaults"/> gives wkhtmltopdf's 10 mm margins.</summary>
    public static PdfOptions Build(ParsedArgs a, bool wkDefaults)
    {
        var options = new PdfOptions();

        PageSize? size = null;
        var sizeName = a.Get("page-size");
        if (sizeName != null) size = Units.ParsePageSize(sizeName);
        var pw = a.Get("page-width");
        var ph = a.Get("page-height");
        if (pw != null || ph != null)
        {
            if (pw == null || ph == null) throw new UsageException("--page-width and --page-height go together.");
            size = new PageSize(Units.ParseLength(pw, "page-width"), Units.ParseLength(ph, "page-height"));
        }

        var orientation = a.Get("orientation");
        bool landscape = a.Has("landscape");
        if (orientation != null)
        {
            if (orientation.Equals("Landscape", StringComparison.OrdinalIgnoreCase)) landscape = true;
            else if (orientation.Equals("Portrait", StringComparison.OrdinalIgnoreCase)) landscape = false;
            else throw new UsageException($"--orientation must be Landscape or Portrait, not '{orientation}'.");
        }

        var pageSize = size ?? PageSize.A4;
        options.PageSize = landscape ? pageSize.Landscape : pageSize;

        float all = wkDefaults ? 10f * 72f / 25.4f : 36f;
        var margin = a.Get("margin");
        if (margin != null) all = Units.ParseLength(margin, "margin");
        float Side(string name) => a.Get(name) is { } v ? Units.ParseLength(v, name) : all;
        options.Margins = new PageMargins(Side("margin-top"), Side("margin-right"), Side("margin-bottom"), Side("margin-left"));

        options.Title = a.Get("title");
        options.Author = a.Get("author");
        options.Subject = a.Get("subject");
        options.Keywords = a.Get("keywords");
        options.Language = a.Get("lang");
        if (a.GetFloat("zoom") is { } zoom) options.Scale = zoom;
        if (a.Has("no-shrink-to-fit") && !a.Has("enable-smart-shrinking")) options.ShrinkToFit = false;
        if (a.Has("no-pdf-compression")) options.Compress = false;
        if (a.Has("no-outline")) options.Bookmarks = false;
        if (a.Has("outline")) options.Bookmarks = true;
        options.AllowFileAccess = a.Has("allow-file-access") && !a.Has("disable-local-file-access");
        options.UseSystemFonts = a.Has("system-fonts");
        options.Linearize = a.Has("linearize");
        options.BaseDirectory = a.Get("base-dir");

        foreach (var font in a.GetAll("font")) options.Fonts.AddFile(font);
        foreach (var dir in a.GetAll("font-dir")) options.Fonts.AddDirectory(dir);

        if (a.Get("pdfa") is { } pdfa) options.Conformance = ParseConformance(pdfa);
        if (a.Get("accessibility") is { } acc) options.Accessibility = ParseAccessibility(acc);

        var user = Common.Password(a, "user-password", "user-password-env");
        var owner = Common.Password(a, "owner-password", "owner-password-env");
        if (user != null || owner != null)
            options.Security = new PdfSecurity { UserPassword = user, OwnerPassword = owner };

        if (a.Get("creation-date") is { } date)
        {
            if (!DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                throw new UsageException($"'{date}' is not a date (option --creation-date); write it like 2026-01-31T12:00:00Z.");
            options.CreationDate = parsed;
        }

        return options;
    }

    private static PdfConformance ParseConformance(string s) => s.ToLowerInvariant().Replace("pdf/a-", "").Replace("pdfa-", "") switch
    {
        "none" => PdfConformance.None,
        "2b" => PdfConformance.PdfA2b,
        "2u" => PdfConformance.PdfA2u,
        "3b" => PdfConformance.PdfA3b,
        "3u" => PdfConformance.PdfA3u,
        "2a" => PdfConformance.PdfA2a,
        "3a" => PdfConformance.PdfA3a,
        "4" => PdfConformance.PdfA4,
        "4f" => PdfConformance.PdfA4f,
        _ => throw new UsageException($"--pdfa must be 2b, 2u, 2a, 3b, 3u, 3a, 4 or 4f, not '{s}'."),
    };

    private static PdfAccessibility ParseAccessibility(string s) => s.ToLowerInvariant() switch
    {
        "none" => PdfAccessibility.None,
        "tagged" => PdfAccessibility.Tagged,
        "ua1" or "pdfua1" or "pdf/ua-1" => PdfAccessibility.PdfUA1,
        "ua2" or "pdfua2" or "pdf/ua-2" => PdfAccessibility.PdfUA2,
        _ => throw new UsageException($"--accessibility must be tagged, ua1 or ua2, not '{s}'."),
    };

    private static readonly string[] BandTexts =
        { "header-left", "header-center", "header-right", "footer-left", "footer-center", "footer-right" };

    /// <summary>Whether a header or footer text holds <paramref name="token"/>, such as [page].</summary>
    public static bool BandsUse(ParsedArgs a, string token)
        => BandTexts.Any(name => a.Get(name) is { } text && text.Contains(token, StringComparison.Ordinal));

    /// <summary>
    /// With several inputs, refuses what the merged file cannot have: the merge keeps PDF/UA-1 but not PDF/UA-2, and
    /// existing PDFs convert to PDF/A-2 and PDF/A-3 levels B and U but not to level A or PDF/A-4. Saying so is better than a file that silently
    /// lacks what was asked for.
    /// </summary>
    public static void CheckSeveralInputs(ParsedArgs a)
    {
        if (a.Get("pdfa") is { } pdfa && ParseConformance(pdfa) is PdfConformance.PdfA4 or PdfConformance.PdfA4f)
            throw new UsageException($"--pdfa {pdfa} needs a single input: the joined file can be made PDF/A-2 or PDF/A-3 (2b, 2u, 3b or 3u), not PDF/A-4. Convert the inputs one at a time, or choose another level.");
        if (a.Get("pdfa") is { } levelA && ParseConformance(levelA) is PdfConformance.PdfA2a or PdfConformance.PdfA3a)
            throw new UsageException($"--pdfa {levelA} needs a single input: the joined file can be made PDF/A-2 or PDF/A-3 level B or U (2b, 2u, 3b or 3u), not level A, which needs a structure tree the merge does not keep. Convert the inputs one at a time, or choose another level.");
        if (a.Get("accessibility") is { } acc && ParseAccessibility(acc) == PdfAccessibility.PdfUA2)
            throw new UsageException($"--accessibility {acc} needs a single input: the joined file can claim PDF/UA-1 (ua1) but not PDF/UA-2. Convert the inputs one at a time, or choose ua1.");
    }

    /// <summary>
    /// wkhtmltopdf's <c>--header-left</c>, <c>--footer-center</c> and the rest become CSS page margin boxes, which the
    /// library draws in the page margins. The tokens <c>[page]</c>, <c>[topage]</c>, <c>[date]</c>, <c>[title]</c> and
    /// the other wkhtmltopdf tokens are replaced, either by a page counter or by their value now. <paramref name="pages"/>
    /// places one of several inputs in the whole output, where [page] and [topage] count every input's pages, as in
    /// wkhtmltopdf, while [sitepage] and [sitepages] count the input's own.
    /// </summary>
    public static string? HeaderFooterStyleSheet(ParsedArgs a, string? title, string? documentTitle, string? source, DateTimeOffset now, BandPages pages)
    {
        var sb = new StringBuilder();
        bool offsetUsed = false;
        foreach (var band in new[] { "header", "footer" })
        {
            float size = a.GetFloat($"{band}-font-size") ?? 12f;
            var name = a.Get($"{band}-font-name");
            var font = name == null ? "" : $" font-family: \"{CssString(name, false)}\";";
            var position = band == "header" ? "top" : "bottom";
            foreach (var side in new[] { "left", "center", "right" })
            {
                var text = a.Get($"{band}-{side}");
                if (string.IsNullOrEmpty(text)) continue;
                sb.Append($"@page {{ @{position}-{side} {{ content: {ContentValue(text, title, documentTitle, source, now, pages, ref offsetUsed)}; ")
                  .Append($"font-size: {size.ToString("0.##", CultureInfo.InvariantCulture)}pt;{font} }} }}\n");
            }
        }

        if (offsetUsed)
        {
            // CSS has no offset for the page counter, so a counter style names the input's pages by their numbers in
            // the whole output.
            sb.Append("@counter-style ").Append(PageStyle).Append(" { system: fixed; symbols:");
            for (int p = 1; p <= pages.Count; p++)
                sb.Append(" \"").Append((pages.Offset + p).ToString(CultureInfo.InvariantCulture)).Append('"');
            sb.Append("; }\n");
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private const string PageStyle = "pdfpecker-output-page";

    private static string ContentValue(string text, string? title, string? documentTitle, string? source, DateTimeOffset now, BandPages pages, ref bool offsetUsed)
    {
        var parts = new List<string>();
        var literal = new StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length == 0) return;
            parts.Add("\"" + CssString(literal.ToString(), true) + "\"");
            literal.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '[')
            {
                int end = text.IndexOf(']', i + 1);
                if (end > 0)
                {
                    var token = text.Substring(i + 1, end - i - 1);
                    string? counter = token switch
                    {
                        "page" when pages.Offset > 0 => $"counter(page, {PageStyle})",
                        "page" or "sitepage" => "counter(page)",
                        "topage" when pages.Total is not null => null,
                        "topage" or "sitepages" => "counter(pages)",
                        _ => null,
                    };
                    string? value = token switch
                    {
                        "topage" when pages.Total is int total => total.ToString(CultureInfo.InvariantCulture),
                        "frompage" => "1",
                        "date" or "isodate" => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        "time" => now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                        "title" => title ?? "",
                        "doctitle" => documentTitle ?? "",
                        "webpage" => source ?? "",
                        "section" or "subsection" => "",
                        _ => null,
                    };
                    if (counter != null)
                    {
                        FlushLiteral();
                        parts.Add(counter);
                        offsetUsed |= token == "page" && pages.Offset > 0;
                        i = end;
                        continue;
                    }

                    if (value != null)
                    {
                        literal.Append(value);
                        i = end;
                        continue;
                    }
                }
            }

            literal.Append(text[i]);
        }

        FlushLiteral();
        return parts.Count == 0 ? "\"\"" : string.Join(" ", parts);
    }

    private static string CssString(string s, bool newlines)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n' when newlines: sb.Append("\\A "); break;
                case '\r': break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Where one input's pages lie in the whole output: after <see cref="Offset"/> pages of earlier inputs, with
/// <see cref="Count"/> pages of its own, out of <see cref="Total"/> (null when not known, or for a single input).
/// </summary>
internal readonly record struct BandPages(int Offset, int Count, int? Total)
{
    /// <summary>A single input, or a first conversion before the page counts are known: the counters count the input.</summary>
    public static BandPages Alone => default;
}
