using System.Globalization;
using PdfPecker.Fonts;
using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>Parsing property values: grid templates, tracks, counters, lengths, borders and font families.</summary>
internal sealed partial class StyleResolver
{
    /// <summary>Parses aspect-ratio: auto, a ratio ("16 / 9" or "1.5"), or both. A ratio of zero is treated as auto.</summary>
    private static bool ParseAspectRatio(string lower, out float ratio, out bool auto)
    {
        ratio = 0;
        auto = false;
        string rest = lower;
        if (rest.StartsWith("auto", StringComparison.Ordinal)) { auto = true; rest = rest.Substring(4); }
        else if (rest.EndsWith("auto", StringComparison.Ordinal)) { auto = true; rest = rest.Substring(0, rest.Length - 4); }
        rest = rest.Trim();
        if (rest.Length == 0) return auto;
        int slash = rest.IndexOf('/');
        string a = slash < 0 ? rest : rest.Substring(0, slash), b = slash < 0 ? "1" : rest.Substring(slash + 1);
        if (!float.TryParse(a.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float w) || w < 0
            || !float.TryParse(b.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float h) || h < 0) return false;
        if (w > 0 && h > 0 && float.IsFinite(w / h)) ratio = w / h;
        else auto = true; // a degenerate ratio such as 0 / 1 behaves as auto
        return true;
    }

    private static bool TryParseNumberOrPercent(string lower, out float value)
    {
        if (lower.EndsWith('%'))
        {
            bool ok = float.TryParse(lower.AsSpan(0, lower.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            value /= 100f;
            return ok;
        }
        return float.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static BoxAlign ParseAlign(string lower, BoxAlign current)
    {
        // Drop overflow and baseline-position modifiers: "safe center", "first baseline".
        var t = lower;
        foreach (var prefix in new[] { "safe ", "unsafe ", "first ", "last ", "legacy " })
            if (t.StartsWith(prefix, StringComparison.Ordinal)) t = t.Substring(prefix.Length).Trim();
        return t switch
        {
            "normal" => BoxAlign.Normal,
            "auto" => BoxAlign.Auto,
            "start" or "flex-start" or "self-start" or "left" => BoxAlign.Start,
            "end" or "flex-end" or "self-end" or "right" => BoxAlign.End,
            "center" => BoxAlign.Center,
            "stretch" => BoxAlign.Stretch,
            "baseline" => BoxAlign.Baseline,
            "space-between" => BoxAlign.SpaceBetween,
            "space-around" => BoxAlign.SpaceAround,
            "space-evenly" => BoxAlign.SpaceEvenly,
            _ => current,
        };
    }

    private BackgroundImage?[]? ParseBackgroundImage(string v, ComputedStyle s)
    {
        var layers = CssParser.SplitTopLevel(v, ',');
        var result = new BackgroundImage?[layers.Count];
        bool any = false;
        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i].Trim();
            if (CssBackground.Url(layer) is { } url) result[i] = new BackgroundImage { Url = url };
            else if (layer.IndexOf("gradient(", StringComparison.OrdinalIgnoreCase) >= 0
                     && CssGradient.Parse(layer, s.Color, s.FontSize, _rootFontSize, _vw, _vh) is { } g) result[i] = new BackgroundImage { Gradient = g };
            any |= result[i] != null;
        }
        return any ? result : null;
    }

    /// <summary>A comma-separated background longhand, one entry per layer; null when any layer is invalid.</summary>
    private static T[]? BackgroundList<T>(string v, Func<string, T?> parse) where T : struct
    {
        var layers = CssParser.SplitTopLevel(v, ',');
        if (layers.Count == 0) return null;
        var result = new T[layers.Count];
        for (int i = 0; i < layers.Count; i++)
        {
            if (parse(layers[i]) is not { } item) return null;
            result[i] = item;
        }
        return result;
    }

    /// <summary>Parses grid-template-areas: one string per row, "." for unnamed cells. Invalid when an area is not a rectangle.</summary>
    internal static GridAreas? ParseGridAreas(string v)
    {
        var rows = new List<string[]>();
        int i = 0;
        while (i < v.Length)
        {
            char q = v[i];
            if (q != '"' && q != '\'') { i++; continue; }
            int close = v.IndexOf(q, i + 1);
            if (close < 0) return null;
            rows.Add(SplitAreaRow(v.Substring(i + 1, close - i - 1)));
            i = close + 1;
        }
        if (rows.Count == 0 || rows[0].Length == 0 || rows.Any(r => r.Length != rows[0].Length)) return null;
        var areas = new GridAreas { Rows = rows.Count, Columns = rows[0].Length };
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < rows[r].Length; c++)
            {
                var name = rows[r][c];
                if (name == ".") continue;
                if (areas.Areas.TryGetValue(name, out var a))
                    areas.Areas[name] = (a.RowStart, a.ColStart, Math.Max(a.RowEnd, r + 1), Math.Max(a.ColEnd, c + 1));
                else areas.Areas[name] = (r, c, r + 1, c + 1);
            }
        foreach (var (name, a) in areas.Areas)
            for (int r = a.RowStart; r < a.RowEnd; r++)
                for (int c = a.ColStart; c < a.ColEnd; c++)
                    if (rows[r][c] != name) return null;
        return areas;
    }

    /// <summary>Cell tokens of one grid-template-areas row; a run of dots is one null cell token.</summary>
    private static string[] SplitAreaRow(string row)
    {
        var cells = new List<string>();
        int i = 0;
        while (i < row.Length)
        {
            if (char.IsWhiteSpace(row[i])) { i++; continue; }
            int start = i;
            if (row[i] == '.') { while (i < row.Length && row[i] == '.') i++; cells.Add("."); continue; }
            while (i < row.Length && !char.IsWhiteSpace(row[i]) && row[i] != '.') i++;
            cells.Add(row.Substring(start, i - start).ToLowerInvariant());
        }
        return cells.ToArray();
    }

    private static GridLine ParseGridLine(string lower)
    {
        var t = CssParser.Tokens(lower);
        if (t.Count == 0 || t[0] == "auto") return default;
        bool span = false;
        int number = 0;
        string? name = null;
        foreach (var tok in t)
        {
            if (tok == "span") span = true;
            else if (Invariant.TryParseInt(tok, out int n)) number = n;
            else if (IsIdent(tok)) name = tok;
            else return default;
        }
        if (span)
        {
            // "span 2" spans two tracks; "span name" (or "span 2 name") spans to the next (second) line of that name.
            if (number < 0) return default;
            return new GridLine(0, number > 0 ? Math.Min(number, 1000) : 1, null, name);
        }
        if (name != null) return new GridLine(number == 0 ? 1 : Math.Clamp(number, -1000, 1000), 0, name);
        return number != 0 ? new GridLine(Math.Clamp(number, -1000, 1000), 0) : default;
    }

    private static bool IsIdent(string tok)
        => tok.Length > 0 && (char.IsLetter(tok[0]) || tok[0] is '_' or '-') && tok.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Parses a grid track list: lengths, percentages, fr, auto, min-content, max-content, minmax(), fit-content(),
    /// repeat() and [line names].
    /// </summary>
    private TrackList? ParseTrackList(string lower, float em)
    {
        var list = new TrackList();
        var before = new List<Track>();
        var after = new List<Track>();
        var current = before;
        List<(string, int, bool)>? names = null;
        void Name(string tok, int line)
        {
            foreach (var n in tok.Trim('[', ']').Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries))
                (names ??= new()).Add((n, line, current == after));
        }
        var tokens = CssParser.Tokens(lower);
        if (tokens.Count > 0 && tokens[0] == "subgrid") return ParseSubgrid(tokens);
        foreach (var tok in tokens)
        {
            if (tok.StartsWith('[')) { Name(tok, current.Count); continue; }
            if (tok.StartsWith("repeat(", StringComparison.Ordinal) && tok.EndsWith(')'))
            {
                var inner = tok.Substring(7, tok.Length - 8);
                int comma = inner.IndexOf(',');
                if (comma < 0) return null;
                var countText = inner.Substring(0, comma).Trim();
                var tracks = new List<Track>();
                var repeatNames = new List<(string Tok, int Index)>();
                foreach (var rt in CssParser.Tokens(inner.Substring(comma + 1).Trim()))
                {
                    if (rt.StartsWith('[')) { repeatNames.Add((rt, tracks.Count)); continue; }
                    if (!TryParseTrack(rt, em, out var tr)) return null;
                    tracks.Add(tr);
                }
                if (tracks.Count == 0) return null;
                if (countText is "auto-fill" or "auto-fit")
                {
                    // Names inside an automatic repetition are not tracked.
                    if (list.AutoRepeat != null) return null;
                    list.AutoRepeat = tracks.ToArray();
                    list.AutoFit = countText == "auto-fit";
                    current = after;
                    continue;
                }
                if (!Invariant.TryParseInt(countText, out int count) || count <= 0) return null;
                for (int k = 0; k < Math.Min(count, 1000); k++)
                {
                    foreach (var (nameTok, index) in repeatNames) Name(nameTok, current.Count + index);
                    current.AddRange(tracks);
                }
                continue;
            }
            if (!TryParseTrack(tok, em, out var t)) return null;
            current.Add(t);
        }
        list.Before = before.ToArray();
        list.After = after.ToArray();
        list.LineNames = names;
        return list.Before.Length + list.After.Length > 0 || list.AutoRepeat != null ? list : null;
    }

    /// <summary>
    /// <c>subgrid</c> with its line-name lists, as in <c>subgrid [a] [b c] repeat(2, [d]) repeat(auto-fill, [e])</c>.
    /// The tokenizer splits a bracket that holds several names at its spaces, so a list is read on to its closing bracket.
    /// </summary>
    private static TrackList? ParseSubgrid(List<string> tokens)
    {
        var list = new TrackList { Subgrid = true };
        var target = list.SubgridNames;
        for (int i = 1; i < tokens.Count; i++)
        {
            var tok = tokens[i];
            if (tok.StartsWith("repeat(", StringComparison.Ordinal) && tok.EndsWith(')'))
            {
                var inner = tok.Substring(7, tok.Length - 8);
                int comma = inner.IndexOf(',');
                if (comma < 0) return null;
                var countText = inner.Substring(0, comma).Trim();
                var repeated = new List<string[]>();
                var inside = CssParser.Tokens(inner.Substring(comma + 1));
                for (int k = 0; k < inside.Count; k++)
                    if (!ReadLineNames(inside, ref k, repeated)) return null;
                if (repeated.Count == 0) return null;
                if (countText == "auto-fill")
                {
                    // Only one auto-fill per list; the lists after it fill the lines from the end.
                    if (list.SubgridAutoFill != null) return null;
                    list.SubgridAutoFill = repeated.ToArray();
                    target = list.SubgridNamesAfter;
                }
                else if (Invariant.TryParseInt(countText, out int count) && count > 0)
                {
                    for (int k = 0; k < Math.Min(count, 1000); k++) target.AddRange(repeated);
                }
                else return null;
                continue;
            }
            if (!ReadLineNames(tokens, ref i, target)) return null;
        }
        return list;
    }

    /// <summary>Reads one bracketed name list that starts at <paramref name="i"/>, leaving <paramref name="i"/> on its last token.</summary>
    private static bool ReadLineNames(List<string> tokens, ref int i, List<string[]> into)
    {
        if (!tokens[i].StartsWith('[')) return false;
        var names = new List<string>();
        for (; i < tokens.Count; i++)
        {
            var tok = tokens[i];
            bool last = tok.EndsWith(']');
            names.AddRange(tok.Trim('[', ']').Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (!last) continue;
            into.Add(names.ToArray());
            return true;
        }
        return false;
    }

    private bool TryParseTrack(string tok, float em, out Track track)
    {
        track = default;
        if (tok.StartsWith("minmax(", StringComparison.Ordinal) && tok.EndsWith(')'))
        {
            var parts = CssParser.SplitTopLevel(tok.Substring(7, tok.Length - 8), ',');
            if (parts.Count != 2 || !TryParseTrackSize(parts[0].Trim(), em, out var min) || !TryParseTrackSize(parts[1].Trim(), em, out var max)) return false;
            if (min.Kind == TrackSizeKind.Fr) min = new TrackSize(TrackSizeKind.Auto);
            track = new Track(min, max);
            return true;
        }
        if (tok.StartsWith("fit-content(", StringComparison.Ordinal) && tok.EndsWith(')'))
        {
            // minmax(auto, max-content) with the max-content size capped at the length; a percentage cap is
            // approximated by the track's available space.
            float cap = TryParseTrackSize(tok.Substring(12, tok.Length - 13).Trim(), em, out var limit) && limit.Kind == TrackSizeKind.Fixed ? limit.Value : 0;
            track = new Track(new TrackSize(TrackSizeKind.Auto), new TrackSize(TrackSizeKind.MaxContent, cap));
            return true;
        }
        if (!TryParseTrackSize(tok, em, out var size)) return false;
        track = size.Kind == TrackSizeKind.Fr ? new Track(new TrackSize(TrackSizeKind.Auto), size) : new Track(size, size);
        return true;
    }

    private bool TryParseTrackSize(string tok, float em, out TrackSize size)
    {
        size = default;
        switch (tok)
        {
            case "auto": size = new TrackSize(TrackSizeKind.Auto); return true;
            case "min-content": size = new TrackSize(TrackSizeKind.MinContent); return true;
            case "max-content": size = new TrackSize(TrackSizeKind.MaxContent); return true;
        }
        if (tok.EndsWith("fr", StringComparison.Ordinal))
        {
            if (!float.TryParse(tok.AsSpan(0, tok.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out float fr) || fr < 0) return false;
            size = new TrackSize(TrackSizeKind.Fr, fr);
            return true;
        }
        if (!TryParseLength(tok, em, _rootFontSize, out var len, _vw, _vh) || len.IsAuto) return false;
        size = len.Unit == LengthUnit.Calc ? new TrackSize(len) : len.IsPercent ? new TrackSize(TrackSizeKind.Percent, len.Value) : new TrackSize(TrackSizeKind.Fixed, Math.Max(0, ToLayoutUnits(len.Value)));
        return true;
    }

    private static void ParsePaint(string v, ComputedStyle s, uint? inheritedColor, string? inheritedUrl, bool inherit,
        out uint? color, out string? url, uint? currentColor, string? currentUrl)
    {
        if (inherit) { color = inheritedColor; url = inheritedUrl; return; }
        var t = v.Trim();
        if (t.Equals("none", StringComparison.OrdinalIgnoreCase) || t.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            color = null;
            url = null;
            return;
        }
        if (t.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            int close = t.IndexOf(')');
            url = close > 4 ? t.Substring(4, close - 4).Trim().Trim('"', '\'') : null;
            var fallback = close >= 0 ? t.Substring(close + 1).Trim() : "";
            color = fallback.Length > 0 && CssColor.TryParse(fallback, s.Color, out var fc) ? fc : null;
            return;
        }
        if (CssColor.TryParse(t, s.Color, out var c))
        {
            color = CssColor.IsVisible(c) ? c : null;
            url = null;
            return;
        }
        color = currentColor;
        url = currentUrl;
    }

    /// <summary>Parses a counter-reset, counter-set or counter-increment list: names, each optionally followed by an integer.</summary>
    private static CounterOp[]? ParseCounters(string v, int defaultValue)
    {
        var t = v.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (t.Length == 0 || t.Length == 1 && t[0].Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        var list = new List<CounterOp>();
        for (int i = 0; i < t.Length; i++)
        {
            if (int.TryParse(t[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return null; // a number without a name
            string name = t[i];
            int value = defaultValue;
            if (i + 1 < t.Length && int.TryParse(t[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) { value = n; i++; }
            list.Add(new CounterOp(name, value));
        }
        return list.ToArray();
    }

    private void SetLength(ref Length target, string lower, float em, Length? inherited, bool noAuto = false)
    {
        if (inherited.HasValue) { target = inherited.Value; return; }
        if (TryParseLength(lower, em, _rootFontSize, out var len, _vw, _vh) && !(noAuto && len.IsAuto)) target = ToLayoutUnits(len);
    }

    /// <summary>
    /// Chrome stores box lengths in units of 1/64 CSS pixel, cutting off the rest (toward zero). Doing the same keeps
    /// long stacks of boxes sized in mm or pt from drifting away from Chrome's positions.
    /// </summary>
    internal static float ToLayoutUnits(float pt) => MathF.Truncate(pt / 0.75f * 64f) / 64f * 0.75f;

    private static Length ToLayoutUnits(Length len) => len.Unit == LengthUnit.Pt ? Length.Pt(ToLayoutUnits(len.Value)) : len;

    private float BorderWidth(string lower, float em, float current)
    {
        switch (lower)
        {
            case "thin": return 0.75f;
            case "medium": return 1.5f;
            case "thick": return 3.75f;
        }
        return TryParseLength(lower, em, _rootFontSize, out var len, _vw, _vh) && !len.IsAuto && !len.IsPercent ? SnapBorderWidth(len.Value) : current;
    }

    /// <summary>
    /// Border widths are rounded down to whole CSS pixels, and a width under one pixel up to one pixel, as browsers do
    /// (CSS Values 4 "snap as a border width"): a 1pt border is 1px wide, not 1.33px.
    /// </summary>
    internal static float SnapBorderWidth(float pt)
    {
        if (pt <= 0) return 0;
        float px = pt / 0.75f;
        return (px < 1 ? 1 : MathF.Floor(px + 0.001f)) * 0.75f;
    }

    private static BorderStyle ParseBorderStyle(string lower, BorderStyle current)
        => lower switch
        {
            "none" or "hidden" => BorderStyle.None,
            "solid" => BorderStyle.Solid,
            "groove" => BorderStyle.Groove,
            "ridge" => BorderStyle.Ridge,
            "inset" => BorderStyle.Inset,
            "outset" => BorderStyle.Outset,
            "dashed" => BorderStyle.Dashed,
            "dotted" => BorderStyle.Dotted,
            "double" => BorderStyle.Double,
            _ => current,
        };

    /// <summary>Sets one corner's border radius: one length for both axes, or horizontal then vertical.</summary>
    private void SetRadius(ComputedStyle s, int corner, string lower, float em, ComputedStyle? inherited)
    {
        Length h, v;
        if (inherited != null)
        {
            if (inherited.BorderRadii == null) { h = v = Length.Zero; }
            else { h = inherited.BorderRadii[corner * 2]; v = inherited.BorderRadii[corner * 2 + 1]; }
        }
        else
        {
            var t = CssParser.Tokens(lower);
            if (t.Count is 0 or > 2) return;
            if (!TryParseLength(t[0], em, _rootFontSize, out h, _vw, _vh) || h.IsAuto || h.Value < 0) return;
            v = h;
            if (t.Count == 2 && (!TryParseLength(t[1], em, _rootFontSize, out v, _vw, _vh) || v.IsAuto || v.Value < 0)) return;
        }
        if (s.BorderRadii == null)
        {
            if (h.Value == 0 && v.Value == 0) return;
            s.BorderRadii = new Length[8];
            Array.Fill(s.BorderRadii, Length.Zero);
        }
        else s.BorderRadii = (Length[])s.BorderRadii.Clone(); // the array may be shared with the style it was copied from
        s.BorderRadii[corner * 2] = h;
        s.BorderRadii[corner * 2 + 1] = v;
    }

    private static uint? BorderColor(string v, ComputedStyle s)
    {
        if (v.Equals("currentcolor", StringComparison.OrdinalIgnoreCase)) return null;
        return CssColor.TryParse(v, s.Color, out var c) ? c : null;
    }

    private static FontFamily MapFontFamily(string value, FontFamily current)
    {
        foreach (var part in value.Split(','))
        {
            var name = part.Trim().Trim('"', '\'').Trim().ToLowerInvariant();
            if (name.Length == 0) continue;
            if (name.Contains("mono") || name.Contains("courier") || name.Contains("consolas") || name.Contains("menlo")
                || name.Contains("monaco") || name.Contains("console") || name.Contains("code"))
                return FontFamily.Courier;
            if (name == "serif" || name.Contains("times") || name.Contains("georgia") || name.Contains("garamond")
                || name.Contains("cambria") || name.Contains("palatino") || name.Contains("baskerville")
                || name.Contains("book antiqua") || name.Contains("didot") || name.Contains("bodoni") || name.Contains("merriweather")
                || name.Contains("charter") || name.Contains("constantia") || name.EndsWith(" serif", StringComparison.Ordinal))
                return FontFamily.Times;
            if (name == "sans-serif" || name.Contains("arial") || name.Contains("helvetica") || name.Contains("verdana")
                || name.Contains("tahoma") || name.Contains("segoe") || name.Contains("calibri") || name.Contains("roboto")
                || name.Contains("sans") || name.Contains("system-ui") || name.Contains("apple-system") || name.Contains("inter")
                || name.Contains("lato") || name.Contains("trebuchet") || name.Contains("lucida") || name.Contains("ubuntu")
                || name.Contains("montserrat") || name.Contains("poppins") || name.Contains("nunito") || name.Contains("geneva")
                || name == "cursive" || name == "fantasy")
                return FontFamily.Helvetica;
        }
        return FontFamily.Helvetica;
    }

    /// <summary>Parses a CSS length. Font-relative units are resolved here; percentages are kept for layout.</summary>
    public static bool TryParseLength(string v, float em, float rem, out Length len, float vw = 5.9528f, float vh = 8.4189f)
    {
        len = default;
        if (v.Length == 0) return false;
        if (v == "auto") { len = Length.Auto; return true; }
        if (v == "0") { len = Length.Zero; return true; }
        if (v[^1] == ')') return CssMath.IsMathFunction(v) && CssMath.TryParseLength(v, em, rem, vw, vh, out len);
        int end = v.Length;
        int unitStart = end;
        while (unitStart > 0 && (char.IsLetter(v[unitStart - 1]) || v[unitStart - 1] == '%')) unitStart--;
        if (unitStart == 0) return false;
        if (!float.TryParse(v.AsSpan(0, unitStart), NumberStyles.Float, CultureInfo.InvariantCulture, out float n)) return false;
        var unit = v.AsSpan(unitStart);
        float pt;
        switch (unit.ToString())
        {
            case "px": pt = n * 0.75f; break;
            case "pt": pt = n; break;
            case "": pt = n * 0.75f; break; // unitless (quirks): treat as px
            case "em": pt = n * em; break;
            case "rem": pt = n * rem; break;
            case "ex": pt = n * em * 0.5f; break;
            case "ch": pt = n * em * 0.5f; break;
            case "%": len = new Length(n, LengthUnit.Percent); return true;
            case "in": pt = n * 72f; break;
            case "cm": pt = n * 72f / 2.54f; break;
            case "mm": pt = n * 72f / 25.4f; break;
            case "q": pt = n * 72f / 101.6f; break;
            case "pc": pt = n * 12f; break;
            case "vw" or "vi": pt = n * vw; break; // 1% of the page's content width
            case "vh" or "vb": pt = n * vh; break; // 1% of the page's content height
            case "vmin": pt = n * Math.Min(vw, vh); break;
            case "vmax": pt = n * Math.Max(vw, vh); break;
            // Without the font's metrics: the style resolver rewrites these in a declaration to points beforehand.
            case "cap": pt = n * em * 0.7f; break;
            case "ic": pt = n * em; break;
            case "lh": pt = n * em * 1.15f; break;
            case "rex" or "rch": pt = n * rem * 0.5f; break;
            case "rcap": pt = n * rem * 0.7f; break;
            case "ric": pt = n * rem; break;
            case "rlh": pt = n * rem * 1.15f; break;
            default:
                if (ViewportUnit(unit.ToString(), vw, vh) is float vp) { pt = n * vp; break; }
                if (ContainerQuery.UnitValue(unit.ToString(), vw, vh) is not float cq) return false;
                pt = n * cq;
                break;
        }
        len = Length.Pt(pt);
        return true;
    }

    /// <summary>
    /// The points one viewport unit is worth. The page has no browser chrome to show or hide, so the small, large and
    /// dynamic viewports (svh, lvh, dvh and the like) are all the page. vi and vb are the inline and block axis, which are
    /// the width and the height in the horizontal writing mode every box has here.
    /// </summary>
    private static float? ViewportUnit(string unit, float vw, float vh)
    {
        if (unit.Length is >= 3 and <= 5 && unit[0] is 's' or 'l' or 'd') unit = unit.Substring(1);
        return unit switch
        {
            "vw" or "vi" => vw, // 1% of the page's content width
            "vh" or "vb" => vh, // 1% of the page's content height
            "vmin" => Math.Min(vw, vh),
            "vmax" => Math.Max(vw, vh),
            _ => null,
        };
    }
}
