using System.Globalization;
using PdfPecker.Fonts;
using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>The cascade: matching rules, ordering their declarations and computing styles from them.</summary>
internal sealed partial class StyleResolver
{
    /// <summary>Stops resolving when cancelled.</summary>
    public CancellationToken Cancellation { get; init; }

    private void Resolve(Element e, ComputedStyle parent)
    {
        if ((++_resolved & 255) == 0) Cancellation.ThrowIfCancellationRequested();
        _count = 0;
        if (_registeredEach.Count > 0 || _registeredRoot.Count > 0) PushRegistered(e);
        Collect(UaIndex.Value, e, TierUa, TierUaImportant);
        AddHints(e);
        int authorStart = _count;
        Collect(_author, e, TierAuthor, TierAuthorImportant);
        // ::placeholder rules match the text field, and style the line that shows its placeholder.
        if (e.Name == "pdf-field-value" && e.Parent != null && e.HasAttribute("placeholder"))
        {
            if (UaIndex.Value.PseudoElements is { } uaPh) Collect(uaPh, e.Parent, TierUa, TierUaImportant, PseudoElement.Placeholder);
            if (_author.PseudoElements is { } authorPh) Collect(authorPh, e.Parent, TierAuthor, TierAuthorImportant, PseudoElement.Placeholder);
        }
        var inline = e.GetAttribute("style");
        if (!string.IsNullOrEmpty(inline))
        {
            if (!_inlineCache.TryGetValue(inline!, out var parsed))
            {
                parsed = new List<Declaration>();
                CssParser.ParseDeclarations(inline!, parsed);
                _inlineCache[inline!] = parsed;
                if (!_containerUnits && ContainerQuery.HasContainerUnit(inline!)) _containerUnits = true;
            }
            for (int i = 0; i < parsed.Count; i++)
                Push(parsed[i].Important ? TierInlineImportant : TierInline, 0, 0, i, parsed[i]);
        }
        StyledControlFallback(e, authorStart);

        SortDeclarations();
        e.Style = Compute(e.Name, parent);
        if (e.Style.Contents) e.Style = ContentsStyle(e.Name, e.Style, parent);
        if (e.AltText != null && e.Style.Display == Display.Inline) e.Style = AltTextBox(e.Style);
        if (e.Style.CounterReset != null || e.Style.CounterSet != null || e.Style.CounterIncrement != null) _countersUsed = true;
    }

    /// <summary>
    /// The style of an img that shows its alt text and is inline: as in Chrome, its alt text is one box that takes the room
    /// its text needs, whatever width and height the image was given. A block or inline-block img keeps its size.
    /// </summary>
    private static ComputedStyle AltTextBox(ComputedStyle s)
    {
        var c = s.Clone();
        c.Display = Display.InlineBlock;
        c.Width = c.Height = Length.Auto;
        c.WidthKeyword = SizeKeyword.None;
        return c;
    }

    private void PushRegistered(Element e)
    {
        for (int i = 0; i < _registeredEach.Count; i++) Push(TierUa, 0, 0, i, _registeredEach[i]);
        if (e.Name == "html" || e.Parent is { Name: "#document" })
            for (int i = 0; i < _registeredRoot.Count; i++) Push(TierUa, 0, 0, _registeredEach.Count + i, _registeredRoot[i]);
    }

    /// <summary>Computes a style from the sorted declarations in the working buffer, sharing it when it was computed before.</summary>
    private ComputedStyle Compute(string name, ComputedStyle parent)
    {
        // A style with cq units depends on the containers around the element, which the cache key does not include.
        bool cacheable = !_containerUnits || !UsesContainerUnits();
        if (cacheable) _probe.Set(parent, _decls, _count);
        if (cacheable && _styleCache.TryGetValue(_probe, out var shared))
        {
            if (name == "html") { _rootFontSize = shared.FontSize; _htmlStyle = shared; }
            Array.Clear(_decls, 0, _count);
            return shared;
        }

        var allDecl = FindAll();
        var s = StartingStyle(parent, allDecl.Keyword);

        // Custom properties first: any other declaration may reference them.
        _custom.Clear();
        for (int i = 0; i < _count; i++)
            if (_decls[i].Property.StartsWith("--", StringComparison.Ordinal)) _custom.Add(new(_decls[i].Property, _decls[i].Value));
        if (_custom.Count > 0) s.CustomProperties = CssVariables.Compute(parent.CustomProperties, _custom);

        // direction before the logical properties (padding-inline-start, ...) that depend on it.
        for (int i = _count - 1; i >= 0; i--)
        {
            if (_decls[i].Property == "direction")
            {
                if (Substituted(_decls[i].Value, s) is { } dir) Apply(s, parent, "direction", dir);
                break;
            }
        }

        // font-size next: em-based lengths in every other property depend on it.
        for (int i = _count - 1; i >= 0; i--)
        {
            if (_decls[i].Property == "font-size" && !ResetByAll(i, allDecl))
            {
                var fs = Substituted(_decls[i].Value, s);
                if (fs != null && CssUnits.HasMetricUnit(fs)) fs = RewriteMetricUnits(fs, parent, parent);
                if (fs != null) ApplyFontSize(s, parent, fs);
                break;
            }
        }
        if (name == "html") _rootFontSize = s.FontSize;

        // color next: currentColor in any other property (background-color: currentColor under an icon's mask) is the
        // element's own colour, whichever of the rules setting them comes first.
        for (int i = 0; i < _count; i++)
            if (_decls[i].Property == "color" && !ResetByAll(i, allDecl) && Substituted(_decls[i].Value, s) is { } color) Apply(s, parent, "color", color);

        bool fontFirst = HasMetricUnit();
        if (fontFirst) ApplyFontPropertiesFirst(s, parent, allDecl);

        for (int i = 0; i < _count; i++)
        {
            var d = _decls[i];
            if (!UsedViewportHeight && d.HasViewportHeightUnit) UsedViewportHeight = true;
            if (d.Property is "font-size" or "color" or "all" || d.Property.StartsWith("--", StringComparison.Ordinal) || ResetByAll(i, allDecl)) continue;
            if (fontFirst && _applied[i]) continue;
            if (!d.HasVar)
            {
                Apply(s, parent, d.Property, d.MetricUnits ? RewriteMetricUnits(d.Value, s, d.Property == "line-height" ? parent : s) : d.Value);
                continue;
            }
            // var(): substitute, then expand shorthands now that the value is known.
            var value = CssVariables.Substitute(d.Value, s.CustomProperties);
            if (value == null) continue; // invalid at computed-value time
            _expanded.Clear();
            CssParser.Expand(d.Property, value, d.Important, _expanded);
            foreach (var x in _expanded)
            {
                var xv = CssUnits.HasMetricUnit(x.Value) ? RewriteMetricUnits(x.Value, x.Property == "font-size" ? parent : s, x.Property is "font-size" or "line-height" ? parent : s) : x.Value;
                if (x.Property == "font-size") ApplyFontSize(s, parent, xv);
                else Apply(s, parent, x.Property, xv);
            }
        }
        if (fontFirst) Array.Clear(_applied, 0, _count);

        // A box that sets its text decoration fixes the decoration's colour to its own colour (currentColor here), so
        // descendants with another colour keep it.
        if (_decorationLineSet)
        {
            _decorationLineSet = false;
            if (s.DecorationColor == null && s.TextDecoration != TextDecoration.None) s.DecorationColor = s.Color;
        }

        // translate, rotate and scale apply before the transform property, in that order.
        if (s.Translate != null || s.Rotate != null || s.Scale != null)
        {
            var all = new List<TransformFn>();
            if (s.Translate != null) all.AddRange(s.Translate);
            if (s.Rotate != null) all.AddRange(s.Rotate);
            if (s.Scale != null) all.AddRange(s.Scale);
            if (s.Transform != null) all.AddRange(s.Transform);
            s.Transform = all.ToArray();
        }

        // Floats and absolutely positioned boxes are block-level (CSS 2.1 §9.7).
        if (s.Floated || s.OutOfFlow)
        {
            if (s.Display is Display.Inline or Display.InlineBlock) s.Display = Display.Block;
            else if (s.Display == Display.InlineFlex) s.Display = Display.Flex;
            else if (s.Display == Display.InlineGrid) s.Display = Display.Grid;
            s.InlineTable = false;
            if (s.OutOfFlow) { s.Floated = false; s.Float = FloatSide.None; }
        }
        if (s.Footnote)
        {
            // A footnote is block-level unless footnote-display makes it inline; an absolutely positioned box is no footnote.
            if (s.OutOfFlow || s.Display == Display.None) s.Footnote = false;
            else if (s.FootnoteDisplay != FootnoteDisplay.Block) s.Display = Display.Inline;
            else if (s.Display is Display.Inline or Display.InlineBlock) s.Display = Display.Block;
            else if (s.Display == Display.InlineFlex) s.Display = Display.Flex;
            else if (s.Display == Display.InlineGrid) s.Display = Display.Grid;
        }
        // A table never inherits the legacy -webkit- alignments of an align attribute around it: Chrome starts its
        // text at the start again.
        if (s.Display == Display.Table && s.BlockAlign != null) { s.TextAlign = TextAlign.Start; s.BlockAlign = null; }
        if (!s.ControlWidth.IsAuto && s.Width.IsAuto && s.WidthKeyword == SizeKeyword.None && !StretchedAcross(s, parent)) s.Width = s.ControlWidth;
        // Flex and grid items are laid out as blocks, which carry their own opacity layer.
        bool inlineBox = s.Display == Display.Inline && !parent.IsFlex && !parent.IsGrid;
        s.InlineFade = inlineBox ? s.Opacity * (parent.Display == Display.Inline ? parent.InlineFade : 1) : 1;
        s.InvalidateFont();
        if (name == "html") _htmlStyle = s;
        if (cacheable) _styleCache[_probe.Freeze()] = s;
        Array.Clear(_decls, 0, _count);
        return s;
    }

    /// <summary>
    /// Whether a flex column or a grid stretches the box over its width. Chrome sizes a text field with width auto to
    /// its size attribute, except where stretching gives it the width of its line or grid area.
    /// </summary>
    private static bool StretchedAcross(ComputedStyle s, ComputedStyle parent)
    {
        if (s.OutOfFlow || s.MarginLeft.IsAuto || s.MarginRight.IsAuto) return false;
        if (parent.IsFlex && !parent.IsRow)
        {
            var align = s.AlignSelf == BoxAlign.Auto ? parent.AlignItems : s.AlignSelf;
            return align is BoxAlign.Stretch or BoxAlign.Normal;
        }
        if (parent.IsGrid)
        {
            var justify = s.JustifySelf == BoxAlign.Auto ? parent.JustifyItems : s.JustifySelf;
            return justify is BoxAlign.Stretch or BoxAlign.Normal or BoxAlign.Auto;
        }
        return false;
    }

    /// <summary>Whether a declaration in the working buffer uses cq units, or may through var().</summary>
    private bool UsesContainerUnits()
    {
        for (int i = 0; i < _count; i++)
            if (ContainerQuery.HasContainerUnit(_decls[i].Value) || _decls[i].HasVar) return true;
        return false;
    }

    private static string? Substituted(string value, ComputedStyle s)
        => CssParser.HasVar(value) ? CssVariables.Substitute(value, s.CustomProperties) : value;

    private void Collect(RuleIndex index, Element e, long tier, long importantTier, PseudoElement which = PseudoElement.None)
    {
        if (e.Id != null && index.ById.TryGetValue(e.Id, out var byId)) Match(byId, e, tier, importantTier, which);
        foreach (var c in e.Classes)
            if (index.ByClass.TryGetValue(c, out var byClass)) Match(byClass, e, tier, importantTier, which);
        if (index.ByTag.TryGetValue(e.Name, out var byTag)) Match(byTag, e, tier, importantTier, which);
        if (index.Universal.Count > 0) Match(index.Universal, e, tier, importantTier, which);
    }

    private void Match(List<StyleRule> rules, Element e, long tier, long importantTier, PseudoElement which)
    {
        foreach (var r in rules)
        {
            if (r.Selector.PseudoElement != which || r.Containers != null && !ContainersMatch(r.Containers, e, which) || !r.Selector.Matches(e)) continue;
            var decls = r.Declarations;
            // Cascade layers: later layers win over earlier ones and unlayered rules over all of them; for !important
            // declarations the order is reversed.
            int layer = r.Layer is { } l ? l.Rank : UnlayeredRank;
            int importantLayer = r.Layer is { } il ? UnlayeredRank - il.Rank : 0;
            for (int i = 0; i < decls.Length; i++)
            {
                bool important = decls[i].Important;
                Push(important ? importantTier : tier, r.Selector.Specificity, r.Order, i, decls[i], important ? importantLayer : layer);
            }
        }
    }

    private const int UnlayeredRank = Stylesheet.MaxLayerRank + 1;

    /// <summary>
    /// Puts the declarations in the working buffer into cascade order. They are usually collected in that order already,
    /// and sorting them anyway cost a style sheet of many rules that all match an element most of the conversion. A
    /// buffer whose keys already rise strictly is the order a sort would give, so it is left as it is.
    /// </summary>
    private void SortDeclarations()
    {
        for (int i = 1; i < _count; i++)
            if (_keys[i - 1] >= _keys[i])
            {
                Array.Sort(_keys, _decls, 0, _count);
                return;
            }
    }

    private void Push(long tier, int specificity, int order, int declIndex, Declaration d, int layer = 0)
    {
        if (_count == _keys.Length)
        {
            Array.Resize(ref _keys, _count * 2);
            Array.Resize(ref _decls, _count * 2);
        }
        long key = tier << 60
                   | (long)layer << 52
                   | (long)Math.Min(specificity, 0xFFFFF) << 32
                   | (long)Math.Min(order, 0xFFFFF) << 12
                   | (long)Math.Min(declIndex, 0xFFF);
        _keys[_count] = key;
        _decls[_count++] = d;
    }
}
