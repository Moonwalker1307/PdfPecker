using System.Runtime.CompilerServices;
using PdfPecker.Css;
using PdfPecker.Fonts;

namespace PdfPecker.Layout;

/// <summary>
/// Text into items: white space handling, the words with their break opportunities (hyphens, soft hyphens, East Asian
/// text, hyphenation), text-transform and synthesized small capitals.
/// </summary>
internal sealed partial class LayoutEngine
{
    // Synthesized small capitals are capitals at 70% of the font size, as Chrome makes them.
    private const float SmallCapsScale = 0.7f;

    // A tab is one space item covering up to this many spaces.
    private static readonly string TabSpaces = new(' ', 64);

    // Characters inside a word that give a line break opportunity (or, for U+200B and U+00AD, are not drawn).
    private static readonly System.Buffers.SearchValues<char> WordBreakChars = System.Buffers.SearchValues.Create("-?­​‐–—");

    private const char SoftHyphen = '­', ZeroWidthSpace = '​', Hyphen = '‐', EnDash = '–', EmDash = '—';

    /// <summary>What a white-space value does with the text's spaces and newlines, and whether lines may break at spaces.</summary>
    private readonly record struct WhiteSpaceRules(bool Collapse, bool KeepNewlines, bool Breakable)
    {
        public static WhiteSpaceRules For(WhiteSpace ws) => new(
            Collapse: ws is WhiteSpace.Normal or WhiteSpace.NoWrap or WhiteSpace.PreLine,
            KeepNewlines: ws is WhiteSpace.Pre or WhiteSpace.PreWrap or WhiteSpace.PreLine,
            Breakable: ws is WhiteSpace.Normal or WhiteSpace.PreWrap or WhiteSpace.PreLine);
    }

    private static bool IsCollapsible(ComputedStyle s) => s.WhiteSpace is WhiteSpace.Normal or WhiteSpace.NoWrap or WhiteSpace.PreLine;

    private static bool IsBreakable(ComputedStyle s) => s.WhiteSpace is WhiteSpace.Normal or WhiteSpace.PreWrap or WhiteSpace.PreLine;

    /// <summary>The characters that separate words: the CSS white space characters.</summary>
    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\f' or '\r';

    /// <summary>The width of <paramref name="length"/> characters of text in a font, with the letter spacing after each.</summary>
    private static float TextWidth(TextFont font, string text, int start, int length, float size, float letterSpacing) =>
        font.MeasureUnits(text, start, length) * size / 1000f + letterSpacing * length;

    /// <summary>A character's advance in a font, with the letter spacing after it.</summary>
    private static float CharAdvance(TextFont font, char c, float size, float letterSpacing) => font.CharWidth(c, size) + letterSpacing;

    /// <summary>The spaces a tab at <paramref name="column"/> covers: up to the next tab stop, none with tab-size: 0.</summary>
    private static int TabWidth(int column, int tabSize)
    {
        int tab = Math.Max(1, tabSize);
        return tabSize == 0 ? 0 : tab - column % tab;
    }

    /// <summary>Adds a text's words and spaces as items, following the style's white-space.</summary>
    private void AddText(CollectState st, string text, ComputedStyle style, string? href, ComputedStyle? deco)
    {
        var rules = WhiteSpaceRules.For(style.WhiteSpace);
        var font = style.Font;
        float size = style.FontSize;
        float spaceW = font.CharWidth(' ', size) + style.LetterSpacing + style.WordSpacing;

        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (c == '\r' || c == '\n')
            {
                if (rules.KeepNewlines)
                {
                    if (c == '\r' && i + 1 < n && text[i + 1] == '\n') i++;
                    i++;
                    AddBreak(st, style, href, deco);
                    continue;
                }
                c = ' ';
            }
            if (IsSpace(c))
            {
                if (rules.Collapse)
                {
                    if (!st.LastWasSpace)
                    {
                        AddSpace(st, style, " ", 1, spaceW, rules.Breakable, href, deco);
                        st.LastWasSpace = true;
                    }
                    i++;
                    continue;
                }
                // A tab is one item covering its spaces up to the next tab stop, not one item per space.
                int count = c == '\t' ? TabWidth(st.Column, style.TabSize) : 1;
                AddSpace(st, style, TabSpaces, count, spaceW * count, rules.Breakable, href, deco);
                st.Column += count;
                st.LastWasSpace = true;
                i++;
                continue;
            }

            int start = i;
            while (i < n && !IsSpace(text[i])) i++;
            if (rules.Breakable && Text.LineBreaker.HasEastAsian(text.AsSpan(start, i - start))) AddEastAsianWord(st, text, start, i - start, style, href, deco);
            else AddBreakableWord(st, text, start, i - start, style, href, deco, rules.Breakable);
            st.Column += i - start;
        }
    }

    /// <summary>Adds a run of <paramref name="count"/> spaces (a slice of <paramref name="source"/>) of <paramref name="width"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] // once per space of the document
    private void AddSpace(CollectState st, ComputedStyle style, string source, int count, float width, bool breakable, string? href, ComputedStyle? deco)
    {
        var space = Rent(ItemKind.Space, style);
        space.Source = source;
        space.Length = count;
        space.Width = width;
        space.Breakable = breakable;
        space.Href = href;
        space.Deco = deco;
        st.Items.Add(space);
    }

    private void AddBreakableWord(CollectState st, string text, int start, int length, ComputedStyle style, string? href, ComputedStyle? deco, bool breakable)
    {
        var span = text.AsSpan(start, length);
        // hyphens: auto, except in words that have soft hyphens: those break only there, as CSS Text specifies.
        // Nor in URLs and e-mail addresses.
        bool hyphenate = breakable && style.HyphensAuto && span.IndexOf(SoftHyphen) < 0 && !LooksLikeAddress(span);
        if (span.IndexOfAny(WordBreakChars) >= 0 && breakable) AddWordSegments(st, text, start, length, style, href, deco, hyphenate);
        else AddWord(st, text, start, length, style, href, deco, BreakAfter.None, hyphenate);
    }

    private static bool LooksLikeAddress(ReadOnlySpan<char> word) =>
        word.IndexOf('@') >= 0 || word.IndexOf("://", StringComparison.Ordinal) >= 0 || word.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ East Asian text

    /// <summary>
    /// A word with Chinese, Japanese or Korean text, which breaks between its characters without spaces: each piece
    /// between two opportunities becomes a word of its own that may end a line, and nothing is drawn between them.
    /// </summary>
    private void AddEastAsianWord(CollectState st, string text, int start, int length, ComputedStyle style, string? href, ComputedStyle? deco)
    {
        bool keepAll = style.WordBreak == WordBreak.KeepAll;
        var strictness = LineBreakStrictness(style);
        // Text that continues from the previous inline element, as in 漢<b>字</b>, may break at the element boundary.
        if (!st.LastWasSpace && st.Last is { Kind: ItemKind.Text, BreakAfter: BreakAfter.None, Length: > 0 } prev
            && IsBreakable(prev.Style)
            && Text.LineBreaker.BreakBetween(LastCodePoint(prev.Source, prev.Start + prev.Length), FirstCodePoint(text, start), keepAll, strictness))
            prev.BreakAfter = BreakAfter.Break;
        var span = text.AsSpan(start, length);
        int seg = 0;
        while (seg < length)
        {
            int next = Text.LineBreaker.NextBreak(span, seg, keepAll, strictness);
            int before = st.Items.Count;
            AddBreakableWord(st, text, start + seg, next - seg, style, href, deco, breakable: true);
            if (next < length && st.Items.Count > before && st.Last is { Kind: ItemKind.Text, BreakAfter: BreakAfter.None } last)
                last.BreakAfter = BreakAfter.Break;
            seg = next;
        }
    }

    /// <summary>The strictness East Asian line breaking uses for a line-break value.</summary>
    private static Text.LineBreakStrictness LineBreakStrictness(ComputedStyle style) => style.LineBreak switch
    {
        LineBreak.Loose => Text.LineBreakStrictness.Loose,
        LineBreak.Normal or LineBreak.Anywhere => Text.LineBreakStrictness.Normal,
        // auto is strict, as in Chrome: no line starts with a small kana or a prolonged sound mark.
        _ => Text.LineBreakStrictness.Strict,
    };

    private static int FirstCodePoint(string s, int at) =>
        char.IsHighSurrogate(s[at]) && at + 1 < s.Length && char.IsLowSurrogate(s[at + 1]) ? char.ConvertToUtf32(s[at], s[at + 1]) : s[at];

    private static int LastCodePoint(string s, int end) =>
        end >= 2 && char.IsLowSurrogate(s[end - 1]) && char.IsHighSurrogate(s[end - 2]) ? char.ConvertToUtf32(s[end - 2], s[end - 1]) : s[end - 1];

    // ------------------------------------------------------------------ break opportunities inside a word

    /// <summary>
    /// Splits a word at its break opportunities as Chrome does: after a hyphen followed by a letter, digit or hyphen,
    /// after U+2010 and en dashes, around em dashes, after "?" before a letter, at zero-width spaces and at soft hyphens
    /// (which show a hyphen only when the line breaks there).
    /// </summary>
    private void AddWordSegments(CollectState st, string text, int start, int length, ComputedStyle style, string? href, ComputedStyle? deco, bool hyphenate)
    {
        int end = start + length, seg = start;
        for (int k = start; k < end; k++)
        {
            char c = text[k];
            char next = k + 1 < end ? text[k + 1] : '\0';
            switch (c)
            {
                case SoftHyphen:
                case ZeroWidthSpace:
                    // The character itself is not drawn: the segment ends before it.
                    AddWord(st, text, seg, k - seg, style, href, deco, c == ZeroWidthSpace ? BreakAfter.Break : style.NoHyphens ? BreakAfter.None : BreakAfter.SoftHyphen, hyphenate);
                    seg = k + 1;
                    break;
                case '-':
                    if (char.IsLetterOrDigit(next) || next == '-') seg = EndSegment(st, text, seg, k + 1, style, href, deco, hyphenate);
                    break;
                case '?':
                    if (char.IsLetter(next)) seg = EndSegment(st, text, seg, k + 1, style, href, deco, hyphenate);
                    break;
                case Hyphen:
                case EnDash:
                    if (next != '\0') seg = EndSegment(st, text, seg, k + 1, style, href, deco, hyphenate);
                    break;
                case EmDash:
                    // The dash is a segment of its own, breakable on both sides.
                    if (k > seg) AddWord(st, text, seg, k - seg, style, href, deco, BreakAfter.Break, hyphenate);
                    seg = k;
                    if (next != '\0') seg = EndSegment(st, text, seg, k + 1, style, href, deco, hyphenate);
                    break;
            }
        }
        if (seg < end) AddWord(st, text, seg, end - seg, style, href, deco, BreakAfter.None, hyphenate);
        else if (st.Last is { Kind: ItemKind.Text, BreakAfter: BreakAfter.SoftHyphen } last)
            last.BreakAfter = BreakAfter.None; // a soft hyphen at the end of a word is no break opportunity of its own
    }

    /// <summary>Adds the segment from <paramref name="seg"/> up to <paramref name="end"/> with a break after it; returns the next segment's start.</summary>
    private int EndSegment(CollectState st, string text, int seg, int end, ComputedStyle style, string? href, ComputedStyle? deco, bool hyphenate)
    {
        AddWord(st, text, seg, end - seg, style, href, deco, BreakAfter.Break, hyphenate);
        return end;
    }

    // ------------------------------------------------------------------ one word

    /// <summary>
    /// Adds one word (a segment between break opportunities) as text items: with its soft hyphens and zero-width spaces
    /// removed and its text-transform applied, as small capitals when the style asks for them, and in pieces at its
    /// hyphenation points with hyphens: auto.
    /// </summary>
    private void AddWord(CollectState st, string text, int start, int length, ComputedStyle style, string? href, ComputedStyle? deco, BreakAfter breakAfter, bool hyphenate = false)
    {
        string src = text;
        int wStart = start, wLen = length;
        var span = text.AsSpan(start, length);
        if (NeedsNewText(span, style))
        {
            string word = TransformedWord(span, style, st.LastWasSpace);
            src = word;
            wStart = 0;
            wLen = word.Length;
        }
        if (wLen == 0)
        {
            // An empty segment (two break characters in a row) still marks the opportunity on the text before it.
            if (breakAfter != BreakAfter.None && st.Last is { Kind: ItemKind.Text, BreakAfter: BreakAfter.None } prev) prev.BreakAfter = breakAfter;
            return;
        }
        if (style.SmallCaps != 0 && NeedsSmallCaps(src, wStart, wLen, style.SmallCaps == 2))
        {
            AddSmallCaps(st, src.Substring(wStart, wLen), style, href, deco, breakAfter);
            return;
        }
        if (hyphenate && HyphenatorFor(style) is { } hyphenator
            && hyphenator.Hyphenate(src.AsSpan(wStart, wLen), _hyphenBreaks, skipCapitalized: IsEnglish(style.Lang!),
                style.HyphenateLimitChars.Word, style.HyphenateLimitChars.Before, style.HyphenateLimitChars.After) > 0)
        {
            // The pieces between the break points are words of their own that break like soft hyphens: only where the
            // word does not fit, showing a hyphen there.
            int from = 0;
            foreach (int at in _hyphenBreaks)
            {
                AddTextItem(st, src, wStart + from, at - from, style, href, deco, BreakAfter.SoftHyphen);
                from = at;
            }
            AddTextItem(st, src, wStart + from, wLen - from, style, href, deco, breakAfter);
            return;
        }
        AddTextItem(st, src, wStart, wLen, style, href, deco, breakAfter);
    }

    /// <summary>Whether a word needs a string of its own: it has soft hyphens or zero-width spaces to drop, or a text-transform.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] // once per word of the document
    private static bool NeedsNewText(ReadOnlySpan<char> span, ComputedStyle style) =>
        span.IndexOf(SoftHyphen) >= 0 || span.IndexOf(ZeroWidthSpace) >= 0 || style.TextTransform != TextTransform.None;

    /// <summary>The word without its soft hyphens and zero-width spaces and with its text-transform applied.</summary>
    private static string TransformedWord(ReadOnlySpan<char> span, ComputedStyle style, bool afterSpace)
    {
        string word = span.ToString().Replace("­", "").Replace("​", "");
        switch (style.TextTransform)
        {
            case TextTransform.Uppercase: return Upper(word, style.Lang);
            case TextTransform.Lowercase: return Lower(word, style.Lang);
            case TextTransform.Capitalize:
                if (word.Length > 0 && afterSpace && char.IsLower(word[0])) word = string.Concat(Upper(word.Substring(0, 1), style.Lang), word.AsSpan(1));
                return word;
            default: return word;
        }
    }

    /// <summary>
    /// Upper case as CSS defines it, by Unicode's full case mapping: ß becomes SS, which the simple mapping of
    /// <see cref="string.ToUpperInvariant"/> leaves alone, and in Turkish and Azerbaijani i becomes İ. Done by hand so
    /// that it does not depend on the server's culture data.
    /// </summary>
    private static string Upper(string word, string? lang)
    {
        if (IsTurkic(lang)) word = word.Replace('i', '\u0130');
        word = word.ToUpperInvariant();
        return word.Contains('ß') ? word.Replace("ß", "SS") : word;
    }

    /// <summary>Lower case, with the Turkish and Azerbaijani dotless ı for I and i for İ.</summary>
    private static string Lower(string word, string? lang)
    {
        if (IsTurkic(lang)) word = word.Replace('I', '\u0131').Replace('\u0130', 'i');
        return word.ToLowerInvariant();
    }

    private static bool IsTurkic(string? lang)
        => lang is { Length: >= 2 } l && (l.StartsWith("tr", StringComparison.OrdinalIgnoreCase) || l.StartsWith("az", StringComparison.OrdinalIgnoreCase))
           && (l.Length == 2 || l[2] == '-' || l[2] == '_');

    /// <summary>Adds one text item: <paramref name="length"/> characters of <paramref name="src"/> from <paramref name="start"/>, measured.</summary>
    private void AddTextItem(CollectState st, string src, int start, int length, ComputedStyle style, string? href, ComputedStyle? deco, BreakAfter breakAfter)
    {
        var word = Rent(ItemKind.Text, style);
        word.Source = src;
        word.Start = start;
        word.Length = length;
        word.Width = TextWidth(style.Font, src, start, length, style.FontSize, style.LetterSpacing);
        word.Href = href;
        word.Deco = deco;
        word.BreakAfter = breakAfter;
        st.Items.Add(word);
        st.LastWasSpace = false;
    }

    // ------------------------------------------------------------------ hyphenation

    private readonly List<int> _hyphenBreaks = new();
    private readonly Dictionary<string, Text.Hyphenator?> _hyphenators = new(StringComparer.Ordinal);

    /// <summary>The hyphenation patterns of the style's language, or null without a language or patterns for it.</summary>
    private Text.Hyphenator? HyphenatorFor(ComputedStyle style)
    {
        if (style.Lang is not { } lang) return null;
        if (!_hyphenators.TryGetValue(lang, out var h)) _hyphenators[lang] = h = Text.Hyphenator.For(lang, style.Hyphenation);
        return h;
    }

    // Chrome does not hyphenate capitalized English words.
    private static bool IsEnglish(string lang) =>
        lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) && (lang.Length == 2 || lang[2] is '-' or '_');

    // ------------------------------------------------------------------ small capitals

    private readonly Dictionary<ComputedStyle, ComputedStyle> _smallCapsStyles = new(ReferenceEqualityComparer.Instance);

    private static bool NeedsSmallCaps(string s, int start, int length, bool all)
    {
        for (int k = start; k < start + length; k++)
            if (char.IsLower(s[k]) || all && char.IsUpper(s[k])) return true;
        return false;
    }

    /// <summary>
    /// font-variant-caps: small-caps (and all-small-caps) without a font that has them: lower-case letters (and, for
    /// all-small-caps, capitals too) become smaller capitals. The pieces of a word keep it unbreakable.
    /// </summary>
    private void AddSmallCaps(CollectState st, string word, ComputedStyle style, string? href, ComputedStyle? deco, BreakAfter breakAfter)
    {
        var small = SmallCapsStyle(style);
        bool all = style.SmallCaps == 2;
        int k = 0;
        while (k < word.Length)
        {
            bool lower = IsSmall(word[k]);
            int end = k + 1;
            while (end < word.Length && IsSmall(word[end]) == lower) end++;
            var piece = lower ? word.Substring(k, end - k).ToUpperInvariant() : word.Substring(k, end - k);
            AddTextItem(st, piece, 0, piece.Length, lower ? small : style, href, deco, end == word.Length ? breakAfter : BreakAfter.None);
            k = end;
        }

        bool IsSmall(char c) => char.IsLower(c) || all && char.IsUpper(c);
    }

    /// <summary>The style of the small capitals of <paramref name="style"/>: the same at the smaller size, made once per style.</summary>
    private ComputedStyle SmallCapsStyle(ComputedStyle style)
    {
        if (_smallCapsStyles.TryGetValue(style, out var small)) return small;
        small = style.Clone();
        small.FontSize = style.FontSize * SmallCapsScale;
        // Decorations keep the full size's thickness and position, so a line under mixed capitals stays straight.
        small.DecorationThickness ??= Length.Pt(Math.Max(style.FontSize * DecorationThicknessRatio, MinDecorationThickness));
        small.UnderlineOffset ??= Length.Pt(style.FontSize * UnderlineOffsetRatio);
        small.InvalidateFont();
        _smallCapsStyles[style] = small;
        return small;
    }
}
