using System.Globalization;
using PdfPecker.Fonts;
using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>Applying a declared value to a computed style.</summary>
internal sealed partial class StyleResolver
{
    private void ApplyFontSize(ComputedStyle s, ComputedStyle parent, string value)
    {
        var v = value.Trim().ToLowerInvariant();
        float px;
        switch (v)
        {
            case "xx-small": px = 9; break;
            case "x-small": px = 10; break;
            case "small": px = 13; break;
            case "medium": px = 16; break;
            case "large": px = 18; break;
            case "x-large": px = 24; break;
            case "xx-large": px = 32; break;
            case "xxx-large": px = 48; break;
            case "smaller": s.FontSize = parent.FontSize / 1.2f; return;
            case "larger": s.FontSize = parent.FontSize * 1.2f; return;
            case "inherit": s.FontSize = parent.FontSize; return;
            case "initial": s.FontSize = 12; return;
            default:
                if (TryParseLength(v, parent.FontSize, _rootFontSize, out var len, _vw, _vh) && !len.IsAuto)
                    s.FontSize = Math.Max(0, len.Resolve(parent.FontSize));
                return;
        }
        s.FontSize = px * 0.75f;
    }

    private void Apply(ComputedStyle s, ComputedStyle parent, string prop, string raw)
    {
        string v = raw.Trim();
        // Math functions that produce plain numbers (opacity: calc(1 / 2)) become the number; lengths are parsed later.
        if (CssMath.HasMath(v)) v = CssMath.FoldNumbers(v);
        string lower = v.ToLowerInvariant();
        float em = s.FontSize;
        bool inherit = lower == "inherit";

        switch (prop)
        {
            case "display":
                s.Display = lower switch
                {
                    "block" => Display.Block,
                    "flow-root" => Display.Block,
                    "flex" or "block flex" => Display.Flex,
                    "grid" or "block grid" => Display.Grid,
                    "inline-flex" or "inline flex" => Display.InlineFlex,
                    "inline-grid" or "inline grid" => Display.InlineGrid,
                    "inline" or "contents" => Display.Inline,
                    "inline-block" => Display.InlineBlock,
                    // The legacy box of line clamping (display: -webkit-box; -webkit-box-orient: vertical) lays out as a block.
                    "-webkit-box" => Display.Block,
                    "-webkit-inline-box" => Display.InlineBlock,
                    "list-item" => Display.ListItem,
                    "table" or "inline-table" => Display.Table,
                    "table-row" => Display.TableRow,
                    "table-cell" => Display.TableCell,
                    "table-row-group" => Display.TableRowGroup,
                    "table-header-group" => Display.TableHeaderGroup,
                    "table-footer-group" => Display.TableFooterGroup,
                    "table-caption" => Display.TableCaption,
                    "table-column" => Display.TableColumn,
                    "table-column-group" => Display.TableColumnGroup,
                    "none" => Display.None,
                    // display is not inherited, so unset is its initial value, inline.
                    "initial" or "unset" => Display.Inline,
                    "inherit" => parent.Display,
                    _ => s.Display,
                };
                if (lower == "flow-root") s.FormattingRoot = true;
                break;
            case "color":
                if (inherit) s.Color = parent.Color;
                else if (CssColor.TryParse(v, parent.Color, out var col)) s.Color = col;
                break;
            case "background-color":
                if (inherit) s.BackgroundColor = parent.BackgroundColor;
                else if (CssColor.TryParse(v, s.Color, out var bg)) s.BackgroundColor = bg;
                break;
            case "background-image":
                s.BackgroundImages = inherit ? parent.BackgroundImages : ParseBackgroundImage(v, s);
                break;
            case "background-repeat":
                s.BackgroundRepeats = inherit ? parent.BackgroundRepeats : BackgroundList(v, CssBackground.Repeat) ?? s.BackgroundRepeats;
                break;
            case "background-size":
                s.BackgroundSizes = inherit ? parent.BackgroundSizes
                    : BackgroundList(v, l => CssBackground.Size(l, em, _rootFontSize, _vw, _vh)) ?? s.BackgroundSizes;
                break;
            case "background-position":
                if (inherit) { s.BackgroundPositionsX = parent.BackgroundPositionsX; s.BackgroundPositionsY = parent.BackgroundPositionsY; break; }
                if (BackgroundList(v, l => CssBackground.Position(l, em, _rootFontSize, _vw, _vh)) is { } positions)
                {
                    s.BackgroundPositionsX = positions.Select(p => p.X).ToArray();
                    s.BackgroundPositionsY = positions.Select(p => p.Y).ToArray();
                }
                break;
            case "background-position-x":
                s.BackgroundPositionsX = inherit ? parent.BackgroundPositionsX
                    : BackgroundList(v, l => CssBackground.Axis(l, true, em, _rootFontSize, _vw, _vh)) ?? s.BackgroundPositionsX;
                break;
            case "background-position-y":
                s.BackgroundPositionsY = inherit ? parent.BackgroundPositionsY
                    : BackgroundList(v, l => CssBackground.Axis(l, false, em, _rootFontSize, _vw, _vh)) ?? s.BackgroundPositionsY;
                break;
            case "background-origin":
                s.BackgroundOrigins = inherit ? parent.BackgroundOrigins : BackgroundList(v, l => CssBackground.Box(l.Trim().ToLowerInvariant())) ?? s.BackgroundOrigins;
                break;
            case "background-clip":
                s.BackgroundClips = inherit ? parent.BackgroundClips : BackgroundList(v, l => CssBackground.Box(l.Trim().ToLowerInvariant())) ?? s.BackgroundClips;
                break;
            case "font-family":
                if (inherit) { s.FontFamily = parent.FontFamily; s.FontFamilyName = parent.FontFamilyName; break; }
                s.FontFamily = MapFontFamily(v, s.FontFamily);
                s.FontFamilyName = v;
                break;
            case "font-weight":
                s.FontWeight = lower switch
                {
                    "bold" => 700,
                    "normal" => 400,
                    "bolder" => parent.FontWeight < 350 ? 400 : parent.FontWeight < 550 ? 700 : Math.Max(900, parent.FontWeight),
                    "lighter" => parent.FontWeight < 100 ? parent.FontWeight : parent.FontWeight < 550 ? 100 : parent.FontWeight < 750 ? 400 : 700,
                    "inherit" => parent.FontWeight,
                    _ => int.TryParse(lower, out int w) && w >= 1 && w <= 1000 ? w : s.FontWeight,
                };
                s.Bold = s.FontWeight >= 600;
                break;
            case "font-stretch":
                if (inherit) s.FontStretch = parent.FontStretch;
                else if (FontStretchPercent(lower) is float stretch) s.FontStretch = stretch;
                break;
            case "font-variation-settings":
                s.FontVariationSettings = inherit ? parent.FontVariationSettings : lower is "normal" or "initial" or "unset" ? null : v;
                break;
            case "font-optical-sizing":
                s.FontOpticalSizingNone = inherit ? parent.FontOpticalSizingNone : lower == "none";
                break;
            case "font-style":
                s.Italic = lower switch { "italic" or "oblique" => true, "normal" => false, "inherit" => parent.Italic, _ => s.Italic };
                break;
            case "line-height":
                if (inherit) { s.LineHeightKind = parent.LineHeightKind; s.LineHeightValue = parent.LineHeightValue; break; }
                if (lower == "normal") { s.LineHeightKind = LineHeightKind.Normal; break; }
                if (float.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float factor))
                {
                    s.LineHeightKind = LineHeightKind.Factor;
                    s.LineHeightValue = factor;
                }
                else if (TryParseLength(lower, em, _rootFontSize, out var lh, _vw, _vh) && !lh.IsAuto)
                {
                    s.LineHeightKind = LineHeightKind.Absolute;
                    s.LineHeightValue = lh.Resolve(em);
                }
                break;
            case "text-align":
                s.TextAlign = lower switch
                {
                    "start" => TextAlign.Start,
                    "end" => TextAlign.End,
                    "left" or "-webkit-left" => TextAlign.Left,
                    "right" or "-webkit-right" => TextAlign.Right,
                    "center" or "middle" or "-webkit-center" => TextAlign.Center,
                    "justify" or "justify-all" => TextAlign.Justify,
                    "inherit" => parent.TextAlign,
                    _ => s.TextAlign,
                };
                if (lower == "justify-all") s.TextAlignLast = TextAlign.Justify;
                break;
            case "text-align-last":
                s.TextAlignLast = lower switch
                {
                    "auto" or "initial" or "unset" => null,
                    "start" => TextAlign.Start,
                    "end" => TextAlign.End,
                    "left" => TextAlign.Left,
                    "right" => TextAlign.Right,
                    "center" => TextAlign.Center,
                    "justify" => TextAlign.Justify,
                    "inherit" => parent.TextAlignLast,
                    _ => s.TextAlignLast,
                };
                break;
            case "tab-size":
            case "-moz-tab-size":
                if (inherit) s.TabSize = parent.TabSize;
                else if (int.TryParse(lower, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tabs) && tabs >= 0) s.TabSize = Math.Min(tabs, 64);
                else if (TryParseLength(v, em, _rootFontSize, out var tabLen, _vw, _vh) && !tabLen.IsAuto && !tabLen.IsPercent)
                {
                    // A length is turned into whole spaces of the current font.
                    float space = Math.Max(0.01f, s.Font.CharWidth(' ', s.FontSize) + s.LetterSpacing + s.WordSpacing);
                    s.TabSize = Math.Clamp((int)MathF.Round(tabLen.Value / space), 0, 64);
                }
                break;
            case "hyphens":
            case "-webkit-hyphens":
                s.NoHyphens = lower == "inherit" ? parent.NoHyphens : lower == "none";
                s.HyphensAuto = lower == "inherit" ? parent.HyphensAuto : lower == "auto";
                break;
            case "hyphenate-character":
            case "-webkit-hyphenate-character":
                if (inherit) s.HyphenateCharacter = parent.HyphenateCharacter;
                else if (lower == "auto" || lower == "initial" || lower == "unset") s.HyphenateCharacter = null;
                else if (GeneratedContent.UnquoteString(v) is { } hyphen) s.HyphenateCharacter = hyphen;
                break;
            case LangProperty:
                s.Lang = v;
                break;
            case "font-variant-caps":
                s.SmallCaps = lower switch
                {
                    "small-caps" or "petite-caps" => 1,
                    "all-small-caps" or "all-petite-caps" => 2,
                    "inherit" => parent.SmallCaps,
                    _ => 0,
                };
                break;
            case "string-set":
                s.StringSet = lower is "none" ? null : inherit ? parent.StringSet : v;
                break;
            case "page":
                if (inherit || lower == "auto") s.PageName = parent.PageName;
                else if (CssParser.Tokens(v) is { Count: 1 } name && CssParser.IsPageName(name[0]))
                {
                    s.PageName = name[0];
                    UsedPageNames = true;
                }
                break;
            case "-webkit-line-clamp":
            case "line-clamp":
                s.LineClamp = int.TryParse(lower, NumberStyles.Integer, CultureInfo.InvariantCulture, out int clamp) && clamp > 0 ? Math.Min(clamp, 10000) : 0;
                break;
            case "text-decoration-line":
            {
                if (inherit) { s.TextDecoration = parent.TextDecoration; break; }
                var td = TextDecoration.None;
                foreach (var t in CssParser.Tokens(lower))
                {
                    if (t == "underline") td |= TextDecoration.Underline;
                    else if (t == "line-through") td |= TextDecoration.LineThrough;
                    else if (t == "overline") td |= TextDecoration.Overline;
                }
                s.TextDecoration = td;
                _decorationLineSet = true;
                break;
            }
            case "text-decoration-color":
                if (inherit) s.DecorationColor = parent.DecorationColor;
                else if (lower == "currentcolor") { s.DecorationColor = null; _decorationLineSet = true; }
                else if (CssColor.TryParse(v, s.Color, out var dc)) s.DecorationColor = dc;
                break;
            case "text-decoration-style":
                s.DecorationStyle = lower switch
                {
                    "solid" => DecorationStyle.Solid,
                    "double" => DecorationStyle.Double,
                    "dotted" => DecorationStyle.Dotted,
                    "dashed" => DecorationStyle.Dashed,
                    "wavy" => DecorationStyle.Wavy,
                    "inherit" => parent.DecorationStyle,
                    _ => s.DecorationStyle,
                };
                break;
            case "text-decoration-thickness":
                if (inherit) s.DecorationThickness = parent.DecorationThickness;
                else if (lower is "auto" or "from-font" or "initial" or "unset") s.DecorationThickness = null;
                else if (TryParseLength(v, em, _rootFontSize, out var dt, _vw, _vh) && !dt.IsAuto) s.DecorationThickness = dt;
                break;
            case "text-underline-offset":
                if (inherit) s.UnderlineOffset = parent.UnderlineOffset;
                else if (lower is "auto" or "initial" or "unset") s.UnderlineOffset = null;
                else if (TryParseLength(v, em, _rootFontSize, out var uo, _vw, _vh) && !uo.IsAuto) s.UnderlineOffset = uo;
                break;
            case "text-transform":
                s.TextTransform = lower switch
                {
                    "uppercase" => TextTransform.Uppercase,
                    "lowercase" => TextTransform.Lowercase,
                    "capitalize" => TextTransform.Capitalize,
                    "none" => TextTransform.None,
                    "inherit" => parent.TextTransform,
                    _ => s.TextTransform,
                };
                break;
            case "text-indent":
                if (TryParseLength(lower, em, _rootFontSize, out var ti, _vw, _vh)) s.TextIndent = ti.IsPercent ? 0 : ti.Resolve(0);
                break;
            case "letter-spacing":
                if (lower == "normal") s.LetterSpacing = 0;
                else if (TryParseLength(lower, em, _rootFontSize, out var ls, _vw, _vh)) s.LetterSpacing = ls.Resolve(0);
                break;
            case "font-variant-ligatures":
                if (lower == "inherit") s.NoLigatures = parent.NoLigatures;
                else if (lower is "normal" or "initial" or "unset") s.NoLigatures = false;
                else if (lower == "none") s.NoLigatures = true;
                else if (lower.Contains("no-common-ligatures")) s.NoLigatures = true;
                else if (lower.Contains("common-ligatures")) s.NoLigatures = false;
                break;
            case "font-feature-settings":
                if (lower == "inherit")
                {
                    s.NoLigatures = parent.NoLigatures;
                    s.FontFeatureSettings = parent.FontFeatureSettings;
                }
                else
                {
                    if (LigaSetting(lower) is bool liga) s.NoLigatures = !liga;
                    // Tags are case-sensitive, so the value is kept as written.
                    s.FontFeatureSettings = lower is "normal" or "initial" or "unset" ? null : v;
                }
                break;
            case "font-variant-numeric":
                s.FontVariantNumeric = lower switch
                {
                    "inherit" => parent.FontVariantNumeric,
                    "normal" or "initial" or "unset" => null,
                    _ => lower,
                };
                break;
            case "word-spacing":
                if (lower == "normal") s.WordSpacing = 0;
                else if (TryParseLength(lower, em, _rootFontSize, out var ws, _vw, _vh)) s.WordSpacing = ws.Resolve(0);
                break;
            case "white-space":
                s.WhiteSpace = lower switch
                {
                    "normal" => WhiteSpace.Normal,
                    "nowrap" => WhiteSpace.NoWrap,
                    "pre" => WhiteSpace.Pre,
                    "pre-wrap" or "break-spaces" => WhiteSpace.PreWrap,
                    "pre-line" => WhiteSpace.PreLine,
                    "inherit" => parent.WhiteSpace,
                    _ => s.WhiteSpace,
                };
                break;
            case "overflow-wrap":
                s.OverflowWrap = lower switch
                {
                    "normal" => OverflowWrap.Normal,
                    "break-word" => OverflowWrap.BreakWord,
                    "anywhere" => OverflowWrap.Anywhere,
                    "inherit" => parent.OverflowWrap,
                    _ => s.OverflowWrap,
                };
                break;
            case "word-break":
                switch (lower)
                {
                    case "normal": s.WordBreak = WordBreak.Normal; break;
                    case "break-all": s.WordBreak = WordBreak.BreakAll; break;
                    case "keep-all": s.WordBreak = WordBreak.KeepAll; break;
                    // Deprecated: the same as word-break: normal with overflow-wrap: anywhere.
                    case "break-word": s.WordBreak = WordBreak.Normal; s.OverflowWrap = OverflowWrap.Anywhere; break;
                    case "inherit": s.WordBreak = parent.WordBreak; break;
                }
                break;
            case "line-break":
                s.LineBreak = lower switch
                {
                    "auto" => LineBreak.Auto,
                    "loose" => LineBreak.Loose,
                    "normal" => LineBreak.Normal,
                    "strict" => LineBreak.Strict,
                    "anywhere" => LineBreak.Anywhere,
                    "inherit" => parent.LineBreak,
                    _ => s.LineBreak,
                };
                break;
            case "vertical-align":
                s.VerticalAlign = lower switch
                {
                    "baseline" => VerticalAlign.Baseline,
                    "sub" => VerticalAlign.Sub,
                    "super" => VerticalAlign.Super,
                    "top" => VerticalAlign.Top,
                    "middle" => VerticalAlign.Middle,
                    "bottom" => VerticalAlign.Bottom,
                    "text-top" => VerticalAlign.TextTop,
                    "text-bottom" => VerticalAlign.TextBottom,
                    "inherit" => parent.VerticalAlign,
                    _ => s.VerticalAlign,
                };
                break;
            case "list-style-type":
                if (GeneratedContent.UnquoteString(v) is { } markerText)
                {
                    // A string is the marker text itself.
                    s.ListStyleString = markerText;
                    s.ListStyleType = ListStyleType.Disc;
                    s.ListCounterStyle = null;
                    break;
                }
                if (lower is "inherit" or "unset")
                {
                    s.ListStyleString = parent.ListStyleString;
                    s.ListStyleType = parent.ListStyleType;
                    s.ListCounterStyle = parent.ListCounterStyle;
                    break;
                }
                if (lower is "initial") lower = "disc";
                var listBullet = lower switch
                {
                    "disc" => ListStyleType.Disc,
                    "circle" => ListStyleType.Circle,
                    "square" => ListStyleType.Square,
                    "disclosure-open" => ListStyleType.DisclosureOpen,
                    "disclosure-closed" => ListStyleType.DisclosureClosed,
                    "none" => ListStyleType.None,
                    _ => (ListStyleType?)null,
                };
                if (listBullet is { } listBulletType)
                {
                    // The bullets are drawn as shapes, as Chrome draws them, and cannot be redefined by @counter-style.
                    s.ListStyleType = listBulletType;
                    s.ListStyleString = null;
                    s.ListCounterStyle = null;
                }
                else if (CounterStyles.TryGet(v, out var listCounter))
                {
                    s.ListStyleString = null;
                    s.ListCounterStyle = listCounter;
                    s.ListStyleType = listCounter.Name switch
                    {
                        "decimal" => ListStyleType.Decimal,
                        "decimal-leading-zero" => ListStyleType.DecimalLeadingZero,
                        "lower-alpha" or "lower-latin" => ListStyleType.LowerAlpha,
                        "upper-alpha" or "upper-latin" => ListStyleType.UpperAlpha,
                        "lower-roman" => ListStyleType.LowerRoman,
                        "upper-roman" => ListStyleType.UpperRoman,
                        _ => ListStyleType.Other,
                    };
                }
                break;
            case "list-style-image":
                if (lower is "inherit" or "unset") s.ListStyleImage = parent.ListStyleImage;
                else if (lower is "none" or "initial") s.ListStyleImage = null;
                else if (CssBackground.Url(v) is { } listImage) s.ListStyleImage = listImage;
                // Gradients and image-set() are not drawn as markers: like an image that fails to load, the type shows.
                else if (lower.Contains("gradient(", StringComparison.Ordinal) || lower.StartsWith("image-set(", StringComparison.Ordinal)) s.ListStyleImage = null;
                break;
            case "list-style-position":
                s.ListStylePositionInside = lower == "inside";
                break;
            case "visibility":
                s.Visible = lower is not ("hidden" or "collapse");
                break;
            case "box-shadow":
                if (inherit) s.BoxShadows = parent.BoxShadows;
                else if (BoxShadow.Parse(v, em, _rootFontSize, _vw, _vh) is { } shadows) s.BoxShadows = shadows.Length > 0 ? shadows : null;
                break;
            case "table-layout":
                s.TableLayoutFixed = lower == "fixed";
                break;
            case "border-collapse":
                s.BorderCollapse = lower == "collapse";
                break;
            case "border-spacing-h":
                if (TryParseLength(lower, em, _rootFontSize, out var bsh, _vw, _vh)) s.BorderSpacingH = ToLayoutUnits(bsh.Resolve(0));
                break;
            case "border-spacing-v":
                if (TryParseLength(lower, em, _rootFontSize, out var bsv, _vw, _vh)) s.BorderSpacingV = ToLayoutUnits(bsv.Resolve(0));
                break;
            case "box-sizing":
                s.BorderBox = lower == "border-box";
                break;
            case "float":
                s.Float = lower switch
                {
                    "left" or "inline-start" => FloatSide.Left,
                    "right" or "inline-end" => FloatSide.Right,
                    "none" or "footnote" => FloatSide.None,
                    "inherit" => parent.Float,
                    _ => s.Float,
                };
                if (lower is "left" or "inline-start" or "right" or "inline-end" or "none" or "footnote" or "inherit")
                    s.Footnote = lower == "footnote" || inherit && parent.Footnote;
                s.Floated = s.Float != FloatSide.None;
                break;
            case "footnote-display":
                s.FootnoteDisplay = inherit ? parent.FootnoteDisplay : lower switch
                {
                    "block" => FootnoteDisplay.Block,
                    "inline" => FootnoteDisplay.Inline,
                    "compact" => FootnoteDisplay.Compact,
                    _ => s.FootnoteDisplay,
                };
                break;
            case "footnote-policy":
                s.FootnotePolicy = inherit ? parent.FootnotePolicy : lower switch
                {
                    "auto" => FootnotePolicy.Auto,
                    "line" => FootnotePolicy.Line,
                    "block" => FootnotePolicy.Block,
                    _ => s.FootnotePolicy,
                };
                break;
            case "clear":
                if (inherit) { s.ClearLeft = parent.ClearLeft; s.ClearRight = parent.ClearRight; break; }
                s.ClearLeft = lower is "left" or "both" or "inline-start";
                s.ClearRight = lower is "right" or "both" or "inline-end";
                break;
            case "position":
                s.Position = lower switch
                {
                    "relative" or "sticky" => Position.Relative,
                    "absolute" => Position.Absolute,
                    "fixed" => Position.Fixed,
                    "static" => Position.Static,
                    "inherit" => parent.Position,
                    _ => s.Position,
                };
                break;
            case "top": SetLength(ref s.Top, lower, em, inherit ? parent.Top : (Length?)null); break;
            case "right": SetLength(ref s.Right, lower, em, inherit ? parent.Right : (Length?)null); break;
            case "bottom": SetLength(ref s.Bottom, lower, em, inherit ? parent.Bottom : (Length?)null); break;
            case "left": SetLength(ref s.Left, lower, em, inherit ? parent.Left : (Length?)null); break;
            case "z-index":
                if (lower == "auto") s.ZIndex = null;
                else if (inherit) s.ZIndex = parent.ZIndex;
                else if (int.TryParse(lower, NumberStyles.Integer, CultureInfo.InvariantCulture, out int z)) s.ZIndex = z;
                break;
            case "overflow":
            case "overflow-x":
            case "overflow-y":
            {
                // Any value but visible and clip makes the box a block formatting context root, which contains its floats.
                var parts = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var t in parts)
                    if (t is "hidden" or "auto" or "scroll" or "overlay") s.FormattingRoot = true;
                if (parts.Length == 0) break;
                Overflow Parse(string t, Overflow inherited) => t switch
                {
                    "visible" => Overflow.Visible,
                    "clip" => Overflow.Clip,
                    "hidden" or "auto" or "scroll" or "overlay" => Overflow.Hidden,
                    "inherit" => inherited,
                    _ => Overflow.Visible,
                };
                if (prop != "overflow-y") s.OverflowX = Parse(parts[0], parent.OverflowX);
                if (prop != "overflow-x") s.OverflowY = Parse(parts[prop == "overflow" && parts.Length > 1 ? 1 : 0], parent.OverflowY);
                break;
            }
            case "transform":
                if (lower == "none") s.Transform = null;
                else if (inherit) s.Transform = parent.Transform;
                else if (CssTransform.TryParse(lower, em, _rootFontSize, _vw, _vh, out var tf)) s.Transform = tf;
                break;
            case "translate":
            {
                // translate: x [y [z]]; a z offset is ignored (no 3D).
                var t = CssParser.Tokens(lower);
                if (lower is "none" or "initial" or "unset") s.Translate = null;
                else if (inherit) s.Translate = parent.Translate;
                else if (t.Count is >= 1 and <= 3 && CssTransform.TryParse("translate(" + t[0] + ", " + (t.Count > 1 ? t[1] : "0") + ")",
                             em, _rootFontSize, _vw, _vh, out var tr)) s.Translate = tr;
                break;
            }
            case "rotate":
            {
                // rotate: angle, or "z angle"; rotations about x, y or a vector are 3D and ignored.
                var t = CssParser.Tokens(lower);
                if (lower is "none" or "initial" or "unset") s.Rotate = null;
                else if (inherit) s.Rotate = parent.Rotate;
                else if (t.Count == 2 && t[0] == "z" || t.Count == 1)
                {
                    if (CssTransform.TryParse("rotate(" + t[^1] + ")", em, _rootFontSize, _vw, _vh, out var ro)) s.Rotate = ro;
                }
                else if (t.Count == 4 && t[0] == "0" && t[1] == "0" && float.TryParse(t[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) && z > 0
                         && CssTransform.TryParse("rotate(" + t[3] + ")", em, _rootFontSize, _vw, _vh, out var rz)) s.Rotate = rz;
                break;
            }
            case "scale":
            {
                var t = CssParser.Tokens(lower);
                if (lower is "none" or "initial" or "unset") s.Scale = null;
                else if (inherit) s.Scale = parent.Scale;
                else if (t.Count is >= 1 and <= 3 && CssTransform.TryParse("scale(" + t[0] + ", " + (t.Count > 1 ? t[1] : t[0]) + ")",
                             em, _rootFontSize, _vw, _vh, out var sc)) s.Scale = sc;
                break;
            }
            case "transform-origin":
                if (inherit) { s.TransformOriginX = parent.TransformOriginX; s.TransformOriginY = parent.TransformOriginY; }
                else if (CssTransform.TryParseOrigin(lower, em, _rootFontSize, _vw, _vh, out var ox, out var oy)) { s.TransformOriginX = ox; s.TransformOriginY = oy; }
                break;
            case "content":
                s.Content = lower is "normal" or "none" ? null : inherit ? parent.Content : v;
                break;
            case "counter-reset":
                s.CounterReset = inherit ? parent.CounterReset : ParseCounters(v, 0);
                break;
            case "counter-set":
                s.CounterSet = inherit ? parent.CounterSet : ParseCounters(v, 0);
                break;
            case "counter-increment":
                s.CounterIncrement = inherit ? parent.CounterIncrement : ParseCounters(v, 1);
                break;
            case "break-before":
                s.PageBreakBefore = lower is "always" or "page" or "left" or "right" or "recto" or "verso";
                s.AvoidBreakBefore = lower is "avoid" or "avoid-page";
                break;
            case "break-after":
                s.PageBreakAfter = lower is "always" or "page" or "left" or "right" or "recto" or "verso";
                s.AvoidBreakAfter = lower is "avoid" or "avoid-page";
                break;
            case "orphans":
                if (inherit) s.Orphans = parent.Orphans;
                else if (int.TryParse(lower, out int orphans) && orphans > 0) s.Orphans = orphans;
                break;
            case "widows":
                if (inherit) s.Widows = parent.Widows;
                else if (int.TryParse(lower, out int widows) && widows > 0) s.Widows = widows;
                break;
            case "break-inside":
                s.AvoidBreakInside = lower is "avoid" or "avoid-page";
                break;

            case "margin-top": SetLength(ref s.MarginTop, lower, em, inherit ? parent.MarginTop : (Length?)null); break;
            case "margin-right": SetLength(ref s.MarginRight, lower, em, inherit ? parent.MarginRight : (Length?)null); break;
            case "margin-bottom": SetLength(ref s.MarginBottom, lower, em, inherit ? parent.MarginBottom : (Length?)null); break;
            case "margin-left": SetLength(ref s.MarginLeft, lower, em, inherit ? parent.MarginLeft : (Length?)null); break;
            case "padding-top": SetLength(ref s.PaddingTop, lower, em, inherit ? parent.PaddingTop : (Length?)null, noAuto: true); break;
            case "padding-right": SetLength(ref s.PaddingRight, lower, em, inherit ? parent.PaddingRight : (Length?)null, noAuto: true); break;
            case "padding-bottom": SetLength(ref s.PaddingBottom, lower, em, inherit ? parent.PaddingBottom : (Length?)null, noAuto: true); break;
            case "direction":
                s.Rtl = lower switch { "rtl" => true, "ltr" => false, "inherit" => parent.Rtl, "initial" => false, _ => s.Rtl };
                break;
            case "unicode-bidi":
                s.UnicodeBidi = lower switch
                {
                    "normal" => UnicodeBidi.Normal,
                    "embed" => UnicodeBidi.Embed,
                    "isolate" or "-webkit-isolate" => UnicodeBidi.Isolate,
                    "bidi-override" => UnicodeBidi.BidiOverride,
                    "isolate-override" or "-webkit-isolate-override" => UnicodeBidi.IsolateOverride,
                    "plaintext" or "-webkit-plaintext" => UnicodeBidi.Plaintext,
                    "inherit" => parent.UnicodeBidi,
                    _ => s.UnicodeBidi,
                };
                break;
            // Logical properties map to the physical side the direction gives.
            case "margin-inline-start": Apply(s, parent, s.Rtl ? "margin-right" : "margin-left", raw); break;
            case "margin-inline-end": Apply(s, parent, s.Rtl ? "margin-left" : "margin-right", raw); break;
            case "padding-inline-start": Apply(s, parent, s.Rtl ? "padding-right" : "padding-left", raw); break;
            case "padding-inline-end": Apply(s, parent, s.Rtl ? "padding-left" : "padding-right", raw); break;
            case "margin-block-start": Apply(s, parent, "margin-top", raw); break;
            case "margin-block-end": Apply(s, parent, "margin-bottom", raw); break;
            case "padding-block-start": Apply(s, parent, "padding-top", raw); break;
            case "padding-block-end": Apply(s, parent, "padding-bottom", raw); break;
            case "padding-left": SetLength(ref s.PaddingLeft, lower, em, inherit ? parent.PaddingLeft : (Length?)null, noAuto: true); break;
            case "width": SetLength(ref s.Width, lower, em, inherit ? parent.Width : (Length?)null); break;
            case "height": SetLength(ref s.Height, lower, em, inherit ? parent.Height : (Length?)null); break;
            case "min-width": SetLength(ref s.MinWidth, lower, em, null); break;
            case "min-height": SetLength(ref s.MinHeight, lower, em, null); break;
            case "max-width": SetLength(ref s.MaxWidth, lower == "none" ? "auto" : lower, em, null); break;
            case "max-height": SetLength(ref s.MaxHeight, lower == "none" ? "auto" : lower, em, null); break;

            case "opacity":
                if (TryParseNumberOrPercent(lower, out float op)) s.Opacity = Math.Clamp(op, 0, 1);
                break;

            case "outline-style": s.OutlineStyle = lower == "auto" ? BorderStyle.Solid : ParseBorderStyle(lower, s.OutlineStyle); break;
            case "outline-width": s.OutlineWidth = BorderWidth(lower, em, s.OutlineWidth); break;
            case "outline-color":
                // invert is drawn as the text colour, as browsers without inversion do.
                s.OutlineColor = lower == "invert" ? null : BorderColor(v, s);
                break;
            case "outline-offset":
                if (TryParseLength(lower, em, _rootFontSize, out var oo, _vw, _vh) && !oo.IsAuto && !oo.IsPercent) s.OutlineOffset = oo.Value;
                break;
            case "text-shadow":
                if (inherit) s.TextShadows = parent.TextShadows;
                else if (lower == "initial") s.TextShadows = null;
                else if (BoxShadow.Parse(v, em, _rootFontSize, _vw, _vh) is { } ts && !ts.Any(t => t.Inset || t.Spread != 0))
                    s.TextShadows = ts.Length > 0 ? ts : null;
                break;
            case "text-overflow":
            {
                // One value for both ends, or start and end: only the end of the line is cut, as in Chrome.
                var tov = CssParser.Tokens(v.Trim());
                if (inherit || tov.Count is not (1 or 2)) break;
                var end = tov[^1];
                if (GeneratedContent.UnquoteString(end) is { } str) s.TextOverflow = str;
                else if (end.Equals("ellipsis", StringComparison.OrdinalIgnoreCase)) s.TextOverflow = "\u2026";
                else if (end.Equals("clip", StringComparison.OrdinalIgnoreCase) || end.Equals("initial", StringComparison.OrdinalIgnoreCase)) s.TextOverflow = null;
                break;
            }
            case "object-fit":
                s.ObjectFit = lower switch
                {
                    "fill" => ObjectFit.Fill,
                    "contain" => ObjectFit.Contain,
                    "cover" => ObjectFit.Cover,
                    "none" => ObjectFit.None,
                    "scale-down" => ObjectFit.ScaleDown,
                    "inherit" => parent.ObjectFit,
                    _ => s.ObjectFit,
                };
                break;
            case "object-position":
                if (inherit) { s.ObjectPositionX = parent.ObjectPositionX; s.ObjectPositionY = parent.ObjectPositionY; }
                else if (CssBackground.Position(lower, em, _rootFontSize, _vw, _vh) is { } opos) { s.ObjectPositionX = opos.X; s.ObjectPositionY = opos.Y; }
                break;
            case "aspect-ratio":
                if (inherit) { s.AspectRatio = parent.AspectRatio; s.AspectRatioAuto = parent.AspectRatioAuto; }
                else if (ParseAspectRatio(lower, out float ratio, out bool ratioAuto)) { s.AspectRatio = ratio; s.AspectRatioAuto = ratioAuto; }
                break;
            case "container-type":
                s.ContainerType = lower switch
                {
                    "inline-size" => ContainerType.InlineSize,
                    "size" => ContainerType.Size,
                    "normal" or "initial" or "unset" or "revert" or "revert-layer" => ContainerType.Normal,
                    "inherit" => parent.ContainerType,
                    _ => s.ContainerType,
                };
                break;
            case "container-name":
                if (inherit) s.ContainerNames = parent.ContainerNames;
                else if (lower is "none" or "initial" or "unset" or "revert" or "revert-layer") s.ContainerNames = null;
                else if (CssParser.ContainerNamesValid(v)) s.ContainerNames = v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                break;

            // ---- flexbox, grid, multi-column
            case "flex-direction":
                s.FlexDirection = lower switch
                {
                    "row" => FlexDirection.Row,
                    "row-reverse" => FlexDirection.RowReverse,
                    "column" => FlexDirection.Column,
                    "column-reverse" => FlexDirection.ColumnReverse,
                    _ => s.FlexDirection,
                };
                break;
            case "flex-wrap":
                s.FlexWrap = lower switch { "nowrap" => FlexWrap.NoWrap, "wrap" => FlexWrap.Wrap, "wrap-reverse" => FlexWrap.WrapReverse, _ => s.FlexWrap };
                break;
            case "flex-grow":
                if (float.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float fg) && fg >= 0) s.FlexGrow = fg;
                break;
            case "flex-shrink":
                if (float.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float fsh) && fsh >= 0) s.FlexShrink = fsh;
                break;
            case "flex-basis":
                if (lower is "content" or "auto" or "max-content" or "min-content" or "fit-content") s.FlexBasis = Length.Auto;
                else SetLength(ref s.FlexBasis, lower, em, null);
                break;
            case "order":
                if (int.TryParse(lower, out int order)) s.Order = order;
                break;
            case "justify-content": s.JustifyContent = ParseAlign(lower, s.JustifyContent); break;
            case "align-items": s.AlignItems = ParseAlign(lower, s.AlignItems); break;
            case "align-content": s.AlignContent = ParseAlign(lower, s.AlignContent); break;
            case "justify-items": s.JustifyItems = ParseAlign(lower, s.JustifyItems); break;
            case "align-self": s.AlignSelf = ParseAlign(lower, s.AlignSelf); break;
            case "justify-self": s.JustifySelf = ParseAlign(lower, s.JustifySelf); break;
            case "row-gap":
            case "column-gap":
            {
                Length? gap = null;
                if (lower != "normal")
                {
                    if (!TryParseLength(lower, em, _rootFontSize, out var gl, _vw, _vh) || gl.IsAuto) break;
                    gap = ToLayoutUnits(gl);
                }
                if (prop == "row-gap") s.RowGap = gap;
                else s.ColumnGap = gap;
                break;
            }
            case "grid-template-columns":
                s.GridTemplateColumns = lower == "none" ? null : ParseTrackList(lower, em);
                break;
            case "grid-template-rows":
                s.GridTemplateRows = lower == "none" ? null : ParseTrackList(lower, em);
                break;
            case "grid-auto-columns":
                s.GridAutoColumns = ParseTrackList(lower, em)?.Before is { Length: > 0 } ac ? ac : null;
                break;
            case "grid-auto-rows":
                s.GridAutoRows = ParseTrackList(lower, em)?.Before is { Length: > 0 } ar ? ar : null;
                break;
            case "grid-auto-flow":
                s.GridAutoFlowColumn = lower.Contains("column");
                s.GridAutoFlowDense = lower.Contains("dense");
                break;
            case "grid-template-areas":
                s.GridTemplateAreas = ParseGridAreas(v);
                break;
            case "grid-column-start": s.GridColumnStart = ParseGridLine(lower); break;
            case "grid-column-end": s.GridColumnEnd = ParseGridLine(lower); break;
            case "grid-row-start": s.GridRowStart = ParseGridLine(lower); break;
            case "grid-row-end": s.GridRowEnd = ParseGridLine(lower); break;
            case "column-count":
                s.ColumnCount = int.TryParse(lower, out int cc) && cc > 0 ? Math.Min(cc, 100) : 0;
                break;
            case "column-width":
                SetLength(ref s.ColumnWidth, lower, em, null);
                if (s.ColumnWidth.IsPercent) s.ColumnWidth = Length.Auto;
                break;
            case "column-rule-style": s.ColumnRuleStyle = ParseBorderStyle(lower, s.ColumnRuleStyle); break;
            case "column-rule-width": s.ColumnRuleWidth = BorderWidth(lower, em, s.ColumnRuleWidth); break;
            case "column-rule-color": s.ColumnRuleColor = BorderColor(v, s); break;
            case "column-span": s.ColumnSpanAll = lower == "all"; break;

            // ---- SVG presentation properties
            case "fill":
                ParsePaint(v, s, inherit ? parent.SvgFill : null, inherit ? parent.SvgFillUrl : null, inherit, out s.SvgFill, out s.SvgFillUrl, s.SvgFill, s.SvgFillUrl);
                break;
            case "stroke":
                ParsePaint(v, s, inherit ? parent.SvgStroke : null, inherit ? parent.SvgStrokeUrl : null, inherit, out s.SvgStroke, out s.SvgStrokeUrl, s.SvgStroke, s.SvgStrokeUrl);
                break;
            case "stroke-width":
                if (float.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float sw)) s.SvgStrokeWidth = Math.Max(0, sw);
                else if (TryParseLength(lower, em, _rootFontSize, out var swl, _vw, _vh) && !swl.IsAuto && !swl.IsPercent) s.SvgStrokeWidth = Math.Max(0, swl.Value / 0.75f);
                break;
            case "fill-opacity":
                if (TryParseNumberOrPercent(lower, out float fo)) s.SvgFillOpacity = Math.Clamp(fo, 0, 1);
                break;
            case "stroke-opacity":
                if (TryParseNumberOrPercent(lower, out float so)) s.SvgStrokeOpacity = Math.Clamp(so, 0, 1);
                break;
            case "fill-rule":
                s.SvgEvenOdd = lower == "evenodd";
                break;
            case "stroke-linecap":
                s.SvgLineCap = lower switch { "round" => SvgLineCap.Round, "square" => SvgLineCap.Square, "butt" => SvgLineCap.Butt, _ => s.SvgLineCap };
                break;
            case "stroke-linejoin":
                s.SvgLineJoin = lower switch { "round" => SvgLineJoin.Round, "bevel" => SvgLineJoin.Bevel, "miter" or "miter-clip" or "arcs" => SvgLineJoin.Miter, _ => s.SvgLineJoin };
                break;
            case "clip-path":
                if (inherit) { s.SvgClipPath = parent.SvgClipPath; s.ClipShape = parent.ClipShape; }
                else if (lower is "none" or "initial" or "unset") { s.SvgClipPath = null; s.ClipShape = null; }
                else if (SvgUrl(v, lower) is { } clipUrl) { s.SvgClipPath = clipUrl; s.ClipShape = null; }
                else if (ClipShape.Parse(v, em, _rootFontSize, _vw, _vh) is { } shape) { s.ClipShape = shape; s.SvgClipPath = null; }
                break;
            case "mix-blend-mode":
                if (inherit) s.BlendMode = parent.BlendMode;
                else if (lower is "initial" or "unset") s.BlendMode = null;
                else if (BlendModes.PdfName(lower, out bool blendValid) is var blend && blendValid) s.BlendMode = blend;
                break;
            case "background-blend-mode":
            {
                if (inherit) { s.BackgroundBlendModes = parent.BackgroundBlendModes; break; }
                if (lower is "initial" or "unset") { s.BackgroundBlendModes = null; break; }
                var parts = CssParser.SplitTopLevel(lower, ',');
                var modes = new string?[parts.Count];
                bool ok = parts.Count > 0, any = false;
                for (int i = 0; i < parts.Count && ok; i++)
                {
                    modes[i] = BlendModes.PdfName(parts[i].Trim(), out ok);
                    any |= modes[i] != null;
                }
                if (ok) s.BackgroundBlendModes = any ? modes : null;
                break;
            }
            case "isolation":
                s.Isolate = lower == "isolate" || inherit && parent.Isolate;
                break;
            case "mask":
                s.SvgMask = SvgUrl(v, lower);
                break;
            case "mask-type":
                s.SvgMaskAlpha = lower == "alpha";
                break;
            case "filter":
                if (inherit) { s.SvgFilter = parent.SvgFilter; s.Filters = parent.Filters; }
                else if (lower is "none" or "initial" or "unset") { s.SvgFilter = null; s.Filters = null; }
                else if (SvgUrl(v, lower) is { } filterUrl && CssParser.Tokens(lower).Count == 1) { s.SvgFilter = filterUrl; s.Filters = null; }
                else if (FilterFunction.Parse(v, em, _rootFontSize, _vw, _vh) is { } filters) { s.Filters = filters.Length > 0 ? filters : null; s.SvgFilter = null; }
                break;
            case "marker":
                s.SvgMarkerStart = s.SvgMarkerMid = s.SvgMarkerEnd = lower == "inherit" ? parent.SvgMarkerStart : SvgUrl(v, lower);
                break;
            case "marker-start":
                s.SvgMarkerStart = lower == "inherit" ? parent.SvgMarkerStart : SvgUrl(v, lower);
                break;
            case "marker-mid":
                s.SvgMarkerMid = lower == "inherit" ? parent.SvgMarkerMid : SvgUrl(v, lower);
                break;
            case "marker-end":
                s.SvgMarkerEnd = lower == "inherit" ? parent.SvgMarkerEnd : SvgUrl(v, lower);
                break;
            case "flood-color":
                if (CssColor.TryParse(v, s.Color, out uint flood)) s.SvgFloodColor = flood;
                break;
            case "lighting-color":
                if (CssColor.TryParse(v, s.Color, out uint light)) s.SvgLightingColor = light;
                break;
            case "flood-opacity":
                if (TryParseNumberOrPercent(lower, out float fop)) s.SvgFloodOpacity = Math.Clamp(fop, 0, 1);
                break;
            case "color-interpolation-filters":
                s.SvgFilterSrgb = lower switch { "srgb" => true, "linearrgb" or "auto" => false, "inherit" => parent.SvgFilterSrgb, _ => s.SvgFilterSrgb };
                break;
            case "clip-rule":
                s.SvgClipEvenOdd = lower == "evenodd";
                break;
            case "text-anchor":
                s.SvgTextAnchor = lower switch { "middle" => 1, "end" => 2, "start" => 0, "inherit" => parent.SvgTextAnchor, _ => s.SvgTextAnchor };
                break;
            case "dominant-baseline":
            case "alignment-baseline":
                s.SvgBaseline = lower switch
                {
                    "middle" or "central" => 1,
                    "hanging" or "text-before-edge" or "before-edge" => 2,
                    "text-after-edge" or "after-edge" or "ideographic" => 3,
                    "auto" or "alphabetic" or "baseline" => 0,
                    "inherit" => parent.SvgBaseline,
                    _ => s.SvgBaseline,
                };
                break;
            case "stroke-miterlimit":
                if (float.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float ml) && ml >= 1) s.SvgMiterLimit = ml;
                break;

            case "border-top-width": s.BorderTopWidth = BorderWidth(lower, em, s.BorderTopWidth); break;
            case "border-right-width": s.BorderRightWidth = BorderWidth(lower, em, s.BorderRightWidth); break;
            case "border-bottom-width": s.BorderBottomWidth = BorderWidth(lower, em, s.BorderBottomWidth); break;
            case "border-left-width": s.BorderLeftWidth = BorderWidth(lower, em, s.BorderLeftWidth); break;
            case "border-top-style": s.BorderTopStyle = ParseBorderStyle(lower, s.BorderTopStyle); break;
            case "border-right-style": s.BorderRightStyle = ParseBorderStyle(lower, s.BorderRightStyle); break;
            case "border-bottom-style": s.BorderBottomStyle = ParseBorderStyle(lower, s.BorderBottomStyle); break;
            case "border-left-style": s.BorderLeftStyle = ParseBorderStyle(lower, s.BorderLeftStyle); break;
            case "border-top-left-radius": SetRadius(s, 0, lower, em, inherit ? parent : null); break;
            case "border-top-right-radius": SetRadius(s, 1, lower, em, inherit ? parent : null); break;
            case "border-bottom-right-radius": SetRadius(s, 2, lower, em, inherit ? parent : null); break;
            case "border-bottom-left-radius": SetRadius(s, 3, lower, em, inherit ? parent : null); break;
            case "border-top-color": s.BorderTopColor = BorderColor(v, s); break;
            case "border-right-color": s.BorderRightColor = BorderColor(v, s); break;
            case "border-bottom-color": s.BorderBottomColor = BorderColor(v, s); break;
            case "border-left-color": s.BorderLeftColor = BorderColor(v, s); break;
            case "border-image-source":
                if (inherit) s.BorderImageSource = parent.BorderImageSource;
                else if (lower is "none" or "initial" or "unset") s.BorderImageSource = null;
                else if (ParseBackgroundImage(v, s) is [{ } source]) s.BorderImageSource = source;
                break;
            case "border-image-slice":
                if (inherit) { s.BorderImageSlice = parent.BorderImageSlice; s.BorderImageFill = parent.BorderImageFill; }
                else if (lower is "initial" or "unset") { s.BorderImageSlice = null; s.BorderImageFill = false; }
                else if (CssBorderImage.Slice(lower) is { } slice) { s.BorderImageSlice = slice.Slice; s.BorderImageFill = slice.Fill; }
                break;
            case "border-image-width":
                if (inherit) s.BorderImageWidths = parent.BorderImageWidths;
                else if (lower is "initial" or "unset") s.BorderImageWidths = null;
                else s.BorderImageWidths = CssBorderImage.Widths(lower, false, em, _rootFontSize, _vw, _vh) ?? s.BorderImageWidths;
                break;
            case "border-image-outset":
                if (inherit) s.BorderImageOutsets = parent.BorderImageOutsets;
                else if (lower is "initial" or "unset") s.BorderImageOutsets = null;
                else s.BorderImageOutsets = CssBorderImage.Widths(lower, true, em, _rootFontSize, _vw, _vh) ?? s.BorderImageOutsets;
                break;
            case "border-image-repeat":
                if (inherit) (s.BorderImageRepeatX, s.BorderImageRepeatY) = (parent.BorderImageRepeatX, parent.BorderImageRepeatY);
                else if (lower is "initial" or "unset") s.BorderImageRepeatX = s.BorderImageRepeatY = BorderImageRepeat.Stretch;
                else if (CssBorderImage.Repeat(lower) is { } rep) (s.BorderImageRepeatX, s.BorderImageRepeatY) = rep;
                break;
        }
    }
}
