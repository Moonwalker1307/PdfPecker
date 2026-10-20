using System.Globalization;
using PdfPecker.Fonts;
using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>Pseudo-elements (first letter, first line, markers, ::before and ::after) and counters.</summary>
internal sealed partial class StyleResolver
{
    /// <summary>The block whose content starts <paramref name="e"/>: its first child, while that is a block before any text.</summary>
    private static Element FirstLineHolder(Element e)
    {
        for (int depth = 0; depth < 32; depth++)
        {
            Element? next = null;
            foreach (var c in e.Children)
            {
                if (c is TextNode t && string.IsNullOrWhiteSpace(t.Text)) continue;
                if (c is Element { Generated: false, Detached: false, Style: { Display: Display.Block or Display.ListItem, Floated: false } } ce
                    && !ce.Style.OutOfFlow) next = ce;
                break;
            }
            if (next == null) return e;
            e = next;
        }
        return e;
    }

    /// <summary>The style rules give a pseudo-element of <paramref name="host"/>, or null when none matches.</summary>
    private ComputedStyle? PseudoStyle(Element host, PseudoElement which, string name)
    {
        _count = 0;
        if (UaIndex.Value.PseudoElements is { } ua) Collect(ua, host, TierUa, TierUaImportant, which);
        if (_author.PseudoElements is { } author) Collect(author, host, TierAuthor, TierAuthorImportant, which);
        if (_count == 0) return null;
        for (int i = 0; i < _registeredEach.Count; i++) Push(TierUa, 0, 0, i, _registeredEach[i]);
        if (_count > 1) Array.Sort(_keys, _decls, 0, _count);
        return Compute(name, host.Style);
    }

    /// <summary>The footnote call and marker are inline boxes in the text, whatever display they were given.</summary>
    private static ComputedStyle Inlined(ComputedStyle s)
    {
        var c = s.Clone();
        c.Display = Display.Inline;
        return c;
    }

    /// <summary>Elements that can hold ::before and ::after boxes: not replaced or void elements, SVG or table internals.</summary>
    private static bool CanHaveGenerated(Element e)
    {
        if (e.Name is "#document" or "img" or "br" or "wbr" or "svg" or "input" or "select" or "textarea" or "video" or "audio"
            or "iframe" or "canvas" or "object" or "embed" or "meta" or "link" or "style" or "script" or "head" or "title" or "html") return false;
        var d = e.Style.Display;
        if (d is Display.None or Display.Table or Display.TableRow or Display.TableRowGroup or Display.TableHeaderGroup
            or Display.TableFooterGroup or Display.TableColumn or Display.TableColumnGroup) return false;
        return !IsSvgElement(e);
    }

    /// <summary>Adds the ::before or ::after box of <paramref name="host"/> when a rule gives it content.</summary>
    private void Generate(Element host, PseudoElement which, ref List<string>? scope)
    {
        _count = 0;
        if (UaIndex.Value.PseudoElements is { } ua) Collect(ua, host, TierUa, TierUaImportant, which);
        if (_author.PseudoElements is { } author) Collect(author, host, TierAuthor, TierAuthorImportant, which);
        if (_count == 0) return;
        for (int i = 0; i < _registeredEach.Count; i++) Push(TierUa, 0, 0, i, _registeredEach[i]);
        if (_count > 1) Array.Sort(_keys, _decls, 0, _count);
        var s = Compute(host.Name, host.Style);
        if (s.Content == null || s.Display == Display.None) return;
        if (s.CounterReset != null || s.CounterSet != null || s.CounterIncrement != null) { _countersUsed = true; ApplyCounters(s, ref scope); }
        var text = GeneratedContent.Evaluate(s.Content, host, this, s);
        if (text == null) return;
        var box = new Element(which == PseudoElement.Before ? "::before" : "::after") { Generated = true, Style = s, Parent = host };
        if (text.Length > 0) box.AppendChild(new TextNode(text));
        if (which == PseudoElement.Before) host.Children.Insert(0, box);
        else host.Children.Add(box);
    }

    private const string FirstLetterName = "::first-letter";

    /// <summary>
    /// Wraps the first letter of a block's first line in a ::first-letter box when rules style it, together with the
    /// punctuation around it (CSS Pseudo-Elements 4 §2.2). The letter may sit in an inline descendant, a ::before box
    /// or the first block inside this one; an image or other atomic box before any text means there is no first letter.
    /// </summary>
    private void GenerateFirstLetter(Element host)
    {
        _count = 0;
        if (UaIndex.Value.PseudoElements is { } ua) Collect(ua, host, TierUa, TierUaImportant, PseudoElement.FirstLetter);
        if (_author.PseudoElements is { } author) Collect(author, host, TierAuthor, TierAuthorImportant, PseudoElement.FirstLetter);
        if (_count == 0) return;
        if (FindFirstText(host) is not { } found)
        {
            Array.Clear(_decls, 0, _count);
            return;
        }
        var (owner, index, start, length) = found;
        var node = (TextNode)owner.Children[index];
        if (_count > 1) Array.Sort(_keys, _decls, 0, _count);
        // The first letter is inside the first line, so it inherits the ::first-line style.
        var parentStyle = owner.Style;
        for (var a = owner; a != null; a = a == host ? null : a.Parent)
            if (a.FirstLine is { } fl) { parentStyle = fl.Map(owner.Style); break; }
        var s = Compute(FirstLetterName, parentStyle);
        // A first letter that floats or is absolutely positioned becomes a block box; otherwise it stays inline.
        if (s.Float != FloatSide.None || s.Position is Position.Absolute or Position.Fixed) s.Display = Display.Block;
        else if (s.Display != Display.Inline) s.Display = Display.Inline;
        var letter = new Element(FirstLetterName) { Generated = true, Style = s, Parent = owner };
        letter.AppendChild(new TextNode(node.Text.Substring(start, length)));
        var rest = node.Text.Substring(start + length);
        node.Text = node.Text.Substring(0, start);
        owner.Children.Insert(index + 1, letter);
        if (rest.Length > 0) owner.Children.Insert(index + 2, new TextNode(rest) { Parent = owner });
    }

    /// <summary>
    /// The text node holding the first letter of <paramref name="e"/>'s first line, and where in it the letter and the
    /// punctuation around it start and how long they are; null when the first line starts with something else.
    /// </summary>
    private static (Element Owner, int Index, int Start, int Length)? FindFirstText(Element e)
    {
        for (int i = 0; i < e.Children.Count; i++)
        {
            switch (e.Children[i])
            {
                case TextNode t:
                {
                    var text = t.Text;
                    int k = 0;
                    while (k < text.Length && char.IsWhiteSpace(text[k])) k++;
                    if (k == text.Length) continue;
                    int start = k;
                    while (k < text.Length && IsFirstLetterPunctuation(text, k)) k++;
                    if (k == text.Length || char.IsWhiteSpace(text[k])) return null; // only punctuation: no letter
                    k += char.IsSurrogatePair(text, k) ? 2 : 1;
                    while (k < text.Length && CharUnicodeInfo.GetUnicodeCategory(text[k]) is UnicodeCategory.NonSpacingMark
                               or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark) k++;
                    while (k < text.Length && IsFirstLetterPunctuation(text, k)) k++;
                    return (e, i, start, k - start);
                }
                case Element c:
                {
                    var s = c.Style;
                    if (s == null || s.Display == Display.None) continue;
                    // Out-of-flow boxes are not on the first line.
                    if (s.Float != FloatSide.None || s.Position is Position.Absolute or Position.Fixed) continue;
                    if (c.Name is "br" or "img" or "svg" or "input" or "select" or "textarea" or "button" or "video" or "audio" or "iframe"
                        or "canvas" or "object" or "embed") return null;
                    if (c.Name == FirstLetterName) return null; // a nested block already took it
                    if (s.Display is Display.Inline or Display.Block or Display.ListItem) return FindFirstText(c);
                    return null; // an inline-block, table, flex or grid box starts the line
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Works out a block's ::first-line style, when rules give it one, and hands it to the element whose inline content
    /// makes the first line: the block itself, or the first block nested at its start. Layout applies it to the text
    /// of that line.
    /// </summary>
    private void ResolveFirstLine(Element host)
    {
        _count = 0;
        if (UaIndex.Value.PseudoElements is { } ua) Collect(ua, host, TierUa, TierUaImportant, PseudoElement.FirstLine);
        if (_author.PseudoElements is { } author) Collect(author, host, TierAuthor, TierAuthorImportant, PseudoElement.FirstLine);
        if (_count == 0) return;
        if (_count > 1) Array.Sort(_keys, _decls, 0, _count);
        var line = Compute("::first-line", host.Style);
        var target = host;
        while (FirstBlockChild(target) is { } inner) target = inner;
        // A ::first-line of a nested block was resolved first and stays: it is the more specific of the two.
        target.FirstLine ??= new FirstLineStyle(host.Style, line);
    }

    /// <summary>The block that starts <paramref name="e"/>'s content, when no text or inline box comes before it.</summary>
    private static Element? FirstBlockChild(Element e)
    {
        foreach (var c in e.Children)
        {
            if (c is TextNode t)
            {
                if (string.IsNullOrWhiteSpace(t.Text)) continue;
                return null;
            }
            if (c is not Element child || child.Style is not { } s || s.Display == Display.None) continue;
            if (s.Float != FloatSide.None || s.Position is Position.Absolute or Position.Fixed) continue;
            return s.Display is Display.Block or Display.ListItem ? child : null;
        }
        return null;
    }

    // Punctuation that goes with the first letter: the Ps, Pe, Pi, Pf and Po classes, as CSS Pseudo-Elements says.
    private static bool IsFirstLetterPunctuation(string text, int k)
        => CharUnicodeInfo.GetUnicodeCategory(text[k]) is UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;

    /// <summary>The style and content of a list item's ::marker, when rules give it one.</summary>
    private void ResolveMarker(Element e)
    {
        _count = 0;
        if (UaIndex.Value.PseudoElements is { } ua) Collect(ua, e, TierUa, TierUaImportant, PseudoElement.Marker);
        if (_author.PseudoElements is { } author) Collect(author, e, TierAuthor, TierAuthorImportant, PseudoElement.Marker);
        if (_count == 0) { e.MarkerStyle = null; e.MarkerContent = null; return; }
        if (_count > 1) Array.Sort(_keys, _decls, 0, _count);
        var s = Compute("::marker", e.Style);
        e.MarkerStyle = s;
        e.MarkerContent = s.Content != null ? GeneratedContent.Evaluate(s.Content, e, this) : null;
    }


    private readonly Dictionary<string, List<int>> _counters = new(StringComparer.Ordinal);
    private bool _countersUsed;
    private readonly bool _firstLetterRules, _firstLineRules;
    internal int QuoteDepth;

    /// <summary>Applies counter-reset, counter-increment and counter-set, in that order (CSS Lists 3).</summary>
    private void ApplyCounters(ComputedStyle s, ref List<string>? scope)
    {
        if (s.CounterReset != null)
            foreach (var c in s.CounterReset)
            {
                if (scope != null && scope.Contains(c.Name)) _counters[c.Name][^1] = c.Value; // a sibling's counter: reset it
                else
                {
                    if (!_counters.TryGetValue(c.Name, out var stack)) _counters[c.Name] = stack = new List<int>();
                    if (stack.Count >= 64) continue; // runaway nesting
                    stack.Add(c.Value);
                    (scope ??= new List<string>()).Add(c.Name);
                }
            }
        if (s.CounterIncrement != null)
            foreach (var c in s.CounterIncrement)
            {
                var stack = Instance(c.Name, ref scope);
                stack[^1] = unchecked(stack[^1] + c.Value);
            }
        if (s.CounterSet != null)
            foreach (var c in s.CounterSet)
                Instance(c.Name, ref scope)[^1] = c.Value;
    }

    /// <summary>The counter's instances, creating one (at 0, scoped to the current parent) when none is in scope.</summary>
    private List<int> Instance(string name, ref List<string>? scope)
    {
        if (!_counters.TryGetValue(name, out var stack)) _counters[name] = stack = new List<int>();
        if (stack.Count == 0)
        {
            stack.Add(0);
            (scope ??= new List<string>()).Add(name);
        }
        return stack;
    }

    /// <summary>Gives a counter a single instance with this value (the page and pages counters of margin boxes).</summary>
    internal void SetCounter(string name, int value) => _counters[name] = new List<int> { value };

    /// <summary>The values of a counter's instances, outermost first; empty when it has none.</summary>
    internal IReadOnlyList<int> CounterValues(string name) => _counters.TryGetValue(name, out var stack) ? stack : Array.Empty<int>();
}
