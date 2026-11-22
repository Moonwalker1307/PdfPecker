using System.Globalization;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>
/// MathML Core layout: fractions, scripts, radicals, limits, stretchy fences and tables, positioned with the constants
/// of an OpenType MATH table (Latin Modern Math's, in ems of each element's font) since text fonts have none. The
/// letters, numbers and operators are laid out as text, each token on its own, and drawn where the formula puts them;
/// fraction bars, radicals and stretched fences and arrows are drawn as rules and paths.
/// </summary>
internal sealed partial class LayoutEngine
{
    /// <summary>
    /// A laid-out part of a formula: its width and its ascent and descent from its baseline, which is at y 0, with its
    /// drawing in those coordinates. An operator, or a script or limit around one, carries the operator's spacing.
    /// </summary>
    private sealed class MathBox
    {
        public float W, Asc, Desc;
        public readonly List<DrawOp> Ops = [];
        /// <summary>The operator this box is (or is embellished from), and its spacing in points.</summary>
        public MathOperator? Op;
        public float LSpace, RSpace;
        /// <summary>The operator element itself, when this box is one that can still be stretched.</summary>
        public Element? Stretchable;
        /// <summary>A token's text, laid out as it is: scripts go by its ink rather than the box drop rules.</summary>
        public bool Token;
        /// <summary>Drawn by the layout (a stretched bar, arrow or brace) rather than a glyph with its own placement.</summary>
        public bool Drawn;
        public float ItalicCorrection;

        public void Move(float dx, float dy)
        {
            if (dx == 0 && dy == 0) return;
            foreach (var op in Ops) op.Offset(dx, dy);
        }

        /// <summary>Moves this box's drawing by (dx, dy) and adds it to <paramref name="into"/>.</summary>
        public void Place(MathBox into, float dx, float dy)
        {
            Move(dx, dy);
            into.Ops.AddRange(Ops);
        }
    }

    /// <summary>How deep a formula's elements are laid out; deeper ones are left out.</summary>
    private const int MaxMathDepth = 120;

    // Latin Modern Math's MATH constants, in ems.
    private const float AxisHeight = 0.25f, RuleThickness = 0.05f;
    private const float FracNumShiftUp = 0.394f, FracNumDisplayShiftUp = 0.677f, FracDenShiftDown = 0.345f, FracDenDisplayShiftDown = 0.686f;
    private const float StackTopShiftUp = 0.444f, StackBottomShiftDown = 0.345f;
    private const float SupShiftUp = 0.363f, SubShiftDown = 0.247f, SupBottomMin = 0.108f, SubTopMax = 0.344f, SubSupGapMin = 0.16f;
    private const float SupBottomMaxWithSub = 0.344f, SupDropMax = 0.25f, SubDropMin = 0.2f, SpaceAfterScript = 0.056f;
    private const float UpperLimitGapMin = 0.2f, UpperLimitRiseMin = 0.111f, LowerLimitGapMin = 0.167f, LowerLimitDropMin = 0.6f;
    private const float OverbarGap = 0.12f, RadicalGap = 0.05f, RadicalDisplayGap = 0.148f;
    private const float RadicalExtraAscender = 0.04f, RadicalKernBeforeDegree = 0.278f, RadicalDegreeRaise = 0.6f;

    private Dictionary<Element, float>? _mathWidths;

    /// <summary>
    /// Lays out a math element's content in its content box at (x, y), a formula on its own baseline; a block formula
    /// is centred in the box. Returns the bottom of the content.
    /// </summary>
    private float LayoutMath(Element e, float x, float y, float w)
    {
        var box = MathOf(e);
        var s = e.Style;
        // The formula's line is at least as tall as a line of its font, so math in text keeps the text's line height.
        s.Font.LineMetrics(s.TextSize, out float ascent, out float descent);
        float asc = Math.Max(box.Asc, ascent), desc = Math.Max(box.Desc, descent);
        float dx = s.Display == Display.Block ? Math.Max(0, (w - box.W) / 2) : 0;
        box.Move(x + dx, y + asc);
        _ops.AddRange(box.Ops);
        SetBoxBaseline(y + asc);
        return y + asc + desc;
    }

    /// <summary>The width of a math element's formula, for its shrink-to-fit width.</summary>
    private float MathWidth(Element e)
    {
        _mathWidths ??= new Dictionary<Element, float>();
        if (_mathWidths.TryGetValue(e, out float w)) return w;
        bool saved = _measuring;
        _measuring = true;
        try
        {
            return _mathWidths[e] = MathOf(e).W;
        }
        finally
        {
            _measuring = saved;
        }
    }

    private MathBox MathOf(Element e) => MathRow(e, MathElements.Children(e), e.HasAttribute(MathElements.DisplayAttribute), 0);

    /// <summary>Lays out one element of a formula in the math-style given, as the form of operator its place in its row makes it.</summary>
    private MathBox MathElement(Element e, bool display, int depth, MathForm form)
    {
        if (depth > MaxMathDepth) return new MathBox();
        var kids = MathElements.Children(e);
        MathBox box;
        switch (e.Name)
        {
            case "mi":
            case "mn":
            case "mtext":
            case "ms":
                box = MathToken(e);
                break;
            case "mo":
                box = MathOperatorBox(e, display, form);
                break;
            case "mspace":
            {
                float em = e.Style.FontSize;
                box = new MathBox
                {
                    W = Math.Max(0, MathLength(e, "width", em, 0, 0)),
                    Asc = MathLength(e, "height", em, 0, 0),
                    Desc = MathLength(e, "depth", em, 0, 0),
                };
                break;
            }
            case "mfrac" when kids.Count == 2:
                box = MathFraction(e, kids, display, depth);
                break;
            case "msqrt":
                box = MathRadical(e, MathRow(e, kids, MathElements.ChildStyle(e, 0, display).Display, depth + 1), null, display);
                break;
            case "mroot" when kids.Count == 2:
                box = MathRadical(e, MathChild(e, kids, 0, display, depth), MathChild(e, kids, 1, display, depth), display);
                break;
            case "msub" when kids.Count == 2:
                box = MathScripts(e, MathChild(e, kids, 0, display, depth, form), MathChild(e, kids, 1, display, depth), null);
                break;
            case "msup" when kids.Count == 2:
                box = MathScripts(e, MathChild(e, kids, 0, display, depth, form), null, MathChild(e, kids, 1, display, depth));
                break;
            case "msubsup" when kids.Count == 3:
                box = MathScripts(e, MathChild(e, kids, 0, display, depth, form), MathChild(e, kids, 1, display, depth), MathChild(e, kids, 2, display, depth));
                break;
            case "munder" when kids.Count == 2:
            case "mover" when kids.Count == 2:
            case "munderover" when kids.Count == 3:
                box = MathUnderOver(e, kids, display, depth, form);
                break;
            case "mmultiscripts" when kids.Count >= 1:
                box = MathMultiscripts(e, display, depth, form);
                break;
            case "mtable":
                box = MathTable(e, kids, display, depth);
                break;
            case "semantics":
            case "maction":
                // The first child is what is shown: the annotations of semantics and the other actions are not.
                box = kids.Count > 0 ? MathChild(e, kids, 0, display, depth, form) : new MathBox();
                break;
            default:
                // An element MathML Core does not lay out on its own (or with the wrong number of children) is a row,
                // and an HTML element in a formula is laid out as a box of its own.
                box = MathElements.IsMathName(e.Name) ? MathRow(e, kids, display, depth) : MathToken(e);
                if (e.Name == "mpadded") MathPadded(e, box);
                else if (e.Name == "mphantom") box.Ops.Clear();
                break;
        }
        if (!MathElements.IsToken(e.Name) && MathElements.IsMathName(e.Name)) MathDecorate(e, box);
        return box;
    }

    private MathBox MathChild(Element parent, List<Element> kids, int index, bool display, int depth, MathForm form = MathForm.Infix)
        => MathElement(kids[index], MathElements.ChildStyle(parent, index, display).Display, depth + 1, form);

    /// <summary>
    /// A row: its children side by side on one baseline, each operator with its space before and after it. Stretchy
    /// fences grow to the height of the rest of the row, symmetric about the math axis.
    /// </summary>
    private MathBox MathRow(Element parent, List<Element> kids, bool display, int depth)
    {
        int n = kids.Count;
        var boxes = new MathBox[n];
        float asc = float.NegativeInfinity, desc = float.NegativeInfinity;
        bool stretchy = false;
        for (int i = 0; i < n; i++)
        {
            var form = n == 1 ? MathForm.Infix : i == 0 ? MathForm.Prefix : i == n - 1 ? MathForm.Postfix : MathForm.Infix;
            var b = boxes[i] = MathElement(kids[i], MathElements.ChildStyle(parent, i, display).Display, depth + 1, form);
            if (b.Stretchable != null && b.Op is { } op && op.Has(MathOperatorFlags.Stretchy) && !op.Has(MathOperatorFlags.Horizontal))
            {
                stretchy = true;
                continue;
            }
            asc = Math.Max(asc, b.Asc);
            desc = Math.Max(desc, b.Desc);
        }
        if (stretchy && asc > float.NegativeInfinity)
            for (int i = 0; i < n; i++)
                if (boxes[i] is { Stretchable: { } mo, Op: { } op } b && op.Has(MathOperatorFlags.Stretchy) && !op.Has(MathOperatorFlags.Horizontal))
                    StretchVertically(mo, b, op, asc, desc);

        var row = new MathBox();
        float x = 0;
        row.Asc = row.Desc = 0;
        for (int i = 0; i < n; i++)
        {
            var b = boxes[i];
            x += b.LSpace;
            b.Place(row, x, 0);
            x += b.W + b.RSpace;
            row.Asc = Math.Max(row.Asc, b.Asc);
            row.Desc = Math.Max(row.Desc, b.Desc);
        }
        row.W = x;
        // A row of one operator is that operator, embellished: its spacing goes to the row around it.
        if (n == 1)
        {
            var only = boxes[0];
            (row.Op, row.LSpace, row.RSpace, row.Token, row.ItalicCorrection) = (only.Op, only.LSpace, only.RSpace, only.Token, only.ItalicCorrection);
            row.W -= only.LSpace + only.RSpace;
            row.Move(-only.LSpace, 0);
        }
        return row;
    }

    /// <summary>A token: its text laid out as a line of its own, measured by its ink.</summary>
    private MathBox MathToken(Element e)
    {
        var c = CaptureBox(e, 100000, shrinkToFit: true);
        float baseline = float.IsNaN(c.Baseline) ? c.Height : c.Baseline;
        var box = new MathBox { W = c.Width, Token = true };
        foreach (var op in c.Ops)
        {
            op.Offset(0, -baseline);
            box.Ops.Add(op);
        }
        if (InkBox(box.Ops, out _, out float top, out float right, out float bottom, out _))
        {
            box.Asc = -top;
            box.Desc = bottom;
            // An italic letter or an integral leans out of its box; a superscript after it goes past the overhang.
            box.ItalicCorrection = Math.Max(0, right - box.W);
        }
        return box;
    }

    /// <summary>An operator: a token with the spacing and properties the dictionary and its attributes give it.</summary>
    private MathBox MathOperatorBox(Element e, bool display, MathForm form)
    {
        string text = MathElements.TokenText(e);
        if (e.GetAttribute("form") is { } f)
            form = f.Trim().ToLowerInvariant() switch { "prefix" => MathForm.Prefix, "postfix" => MathForm.Postfix, "infix" => MathForm.Infix, _ => form };
        var entry = MathElements.Lookup(text, form);
        var flags = entry.Flags;
        flags = Flag(e, "stretchy", flags, MathOperatorFlags.Stretchy);
        flags = Flag(e, "symmetric", flags, MathOperatorFlags.Symmetric);
        flags = Flag(e, "largeop", flags, MathOperatorFlags.LargeOp);
        flags = Flag(e, "movablelimits", flags, MathOperatorFlags.MovableLimits);
        flags = Flag(e, "accent", flags, MathOperatorFlags.Accent);
        float em = e.Style.FontSize;
        var op = new MathOperator(MathLength(e, "lspace", em, em, entry.LSpace * em) / Math.Max(em, 0.001f),
            MathLength(e, "rspace", em, em, entry.RSpace * em) / Math.Max(em, 0.001f), flags);
        var box = MathToken(e);
        box.Op = op;
        box.LSpace = Math.Max(0, op.LSpace * em);
        box.RSpace = Math.Max(0, op.RSpace * em);
        box.Stretchable = e;
        // A large operator in display style is drawn larger, as the display variant of a math font draws it, and
        // centred on the math axis.
        if (display && op.Has(MathOperatorFlags.LargeOp) && box.Ops.Count > 0)
        {
            bool integral = text.Length == 1 && "∫∬∭∮∯∰∱∲∳⨌⨍⨏⨑".Contains(text[0]);
            ScaleMath(box, integral ? 1.8f : 1.4f);
            CentreOnAxis(box, em);
            box.Token = false;
        }
        return box;

        static MathOperatorFlags Flag(Element e, string name, MathOperatorFlags flags, MathOperatorFlags flag)
            => e.GetAttribute(name)?.Trim().ToLowerInvariant() switch { "true" => flags | flag, "false" => flags & ~flag, _ => flags };
    }

    /// <summary>Scales a box and its text about its origin, for a large operator's display size.</summary>
    private void ScaleMath(MathBox box, float k)
    {
        foreach (var op in box.Ops)
            switch (op)
            {
                case TextOp t:
                    t.X *= k;
                    t.Baseline *= k;
                    t.LineTop *= k;
                    t.Size *= k;
                    break;
                case RectOp r:
                    (r.X, r.Y, r.W, r.H) = (r.X * k, r.Y * k, r.W * k, r.H * k);
                    break;
            }
        box.W *= k;
        if (InkBox(box.Ops, out _, out float top, out float right, out float bottom, out _))
            (box.Asc, box.Desc, box.ItalicCorrection) = (-top, bottom, Math.Max(0, right - box.W));
        else (box.Asc, box.Desc, box.ItalicCorrection) = (box.Asc * k, box.Desc * k, box.ItalicCorrection * k);
    }

    /// <summary>Moves a box up or down so that its middle is on the math axis.</summary>
    private static void CentreOnAxis(MathBox box, float em)
    {
        float shift = (box.Asc - box.Desc) / 2 - AxisHeight * em;
        box.Move(0, shift);
        box.Asc -= shift;
        box.Desc += shift;
    }

    /// <summary>
    /// Stretches a fence (or another operator that stretches up and down) to the ascent and descent of the rest of its
    /// row, symmetric about the axis when it is symmetric. Text fonts have no larger variants, so a fence that has to
    /// grow is drawn as a path of that size; one that is tall enough already stays the glyph it is.
    /// </summary>
    private static void StretchVertically(Element mo, MathBox box, MathOperator op, float asc, float desc)
    {
        var s = mo.Style;
        float em = s.FontSize, axis = AxisHeight * em;
        if (op.Has(MathOperatorFlags.Symmetric))
        {
            float half = Math.Max(asc - axis, desc + axis);
            (asc, desc) = (axis + half, half - axis);
        }
        float need = asc + desc, natural = box.Asc + box.Desc;
        if (need <= natural * 1.1f || need <= 0) return;
        string text = MathElements.TokenText(mo);
        if (text.Length != 1 || !s.Visible) return;
        float w = Math.Max(box.W, Math.Min(0.3f * em + 0.1f * need, 0.8f * em));
        var ops = Fence(text[0], w, -asc, desc, em, CssColor.Fade(s.Color, s.InlineFade));
        if (ops == null) return;
        box.Ops.Clear();
        box.Ops.AddRange(ops);
        (box.W, box.Asc, box.Desc, box.Token) = (w, asc, desc, false);
    }

    /// <summary>A fence drawn from top to bottom (y down, the baseline at 0) in a box w wide, or null for a character it cannot draw.</summary>
    private static List<DrawOp>? Fence(char c, float w, float top, float bottom, float em, uint color)
    {
        float h = bottom - top, mid = (top + bottom) / 2;
        float thick = 0.07f * em, thin = 0.03f * em, stem = 0.055f * em;
        var ops = new List<DrawOp>();
        switch (c)
        {
            case '(':
            case ')':
            case '⟮':
            case '⟯':
            {
                // A crescent between two curves, thick in the middle and thin at the tips.
                bool left = c is '(' or '⟮';
                float xOut = left ? 0.15f * w : 0.85f * w, xTip = left ? 0.85f * w : 0.15f * w;
                float sign = left ? 1 : -1;
                var pts = new List<float>();
                Curve(pts, xTip, top, 2 * xOut - xTip, mid, xTip, bottom);
                Curve(pts, xTip + sign * thin * 0.6f, bottom - thin, 2 * (xOut + sign * thick) - (xTip + sign * thin * 0.6f), mid, xTip + sign * thin * 0.6f, top + thin);
                ops.Add(new PathOp([pts.ToArray()], null, color));
                break;
            }
            case '[':
            case ']':
            case '⌈':
            case '⌉':
            case '⌊':
            case '⌋':
            case '⟦':
            case '⟧':
            {
                bool left = c is '[' or '⌈' or '⌊' or '⟦';
                float xStem = left ? 0.3f * w : 0.7f * w - stem, xEnd = left ? 0.85f * w : 0.15f * w;
                ops.Add(new RectOp(xStem, top, stem, h, color));
                if (c is '⟦' or '⟧') ops.Add(new RectOp(xStem + (left ? 2.5f : -2.5f) * stem, top, stem * 0.6f, h, color));
                float x0 = Math.Min(xStem, xEnd), x1 = Math.Max(xStem + stem, xEnd);
                if (c is not ('⌊' or '⌋')) ops.Add(new RectOp(x0, top, x1 - x0, thin * 1.5f, color));
                if (c is not ('⌈' or '⌉')) ops.Add(new RectOp(x0, bottom - thin * 1.5f, x1 - x0, thin * 1.5f, color));
                break;
            }
            case '{':
            case '}':
            {
                bool left = c == '{';
                float xm = 0.5f * w, xIn = left ? 0.15f * w : 0.85f * w, xOut = left ? 0.85f * w : 0.15f * w;
                float r = Math.Min(0.18f * em, h / 6);
                var pts = new List<float>();
                Quarter(pts, xOut, top, xm, top + r, true);
                pts.Add(xm); pts.Add(mid - r);
                Quarter(pts, xm, mid - r, xIn, mid, false);
                Quarter(pts, xIn, mid, xm, mid + r, true);
                pts.Add(xm); pts.Add(bottom - r);
                Quarter(pts, xm, bottom - r, xOut, bottom, false);
                ops.Add(new PathOp([pts.ToArray()], null, color) { StrokeWidth = stem, Open = true });
                break;
            }
            case '|':
            case '∣':
                ops.Add(new RectOp(w / 2 - stem / 2, top, stem, h, color));
                break;
            case '‖':
            case '∥':
                ops.Add(new RectOp(w / 2 - 1.6f * stem, top, stem, h, color));
                ops.Add(new RectOp(w / 2 + 0.6f * stem, top, stem, h, color));
                break;
            case '⟨':
            case '〈':
            case '⟩':
            case '〉':
            case '⟪':
            case '⟫':
            {
                bool left = c is '⟨' or '〈' or '⟪';
                float xPoint = left ? 0.15f * w : 0.85f * w, xEnd = left ? 0.85f * w : 0.15f * w;
                ops.Add(new PathOp([[xEnd, top, xPoint, mid, xEnd, bottom]], null, color) { StrokeWidth = stem * 0.8f, Open = true });
                break;
            }
            default:
                return null;
        }
        return ops;

        // A quadratic curve from (x0, y0) through the control point (cx, cy) to (x1, y1), as points.
        static void Curve(List<float> pts, float x0, float y0, float cx, float cy, float x1, float y1)
        {
            const int steps = 16;
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps, u = 1 - t;
                pts.Add(u * u * x0 + 2 * t * u * cx + t * t * x1);
                pts.Add(u * u * y0 + 2 * t * u * cy + t * t * y1);
            }
        }

        // A quarter of an ellipse from (x0, y0) to (x1, y1), leaving along x and arriving along y when acrossFirst, else
        // leaving along y and arriving along x.
        static void Quarter(List<float> pts, float x0, float y0, float x1, float y1, bool acrossFirst)
        {
            const int steps = 6;
            for (int i = pts.Count == 0 ? 0 : 1; i <= steps; i++)
            {
                double a = i / (double)steps * Math.PI / 2;
                float sin = (float)Math.Sin(a), cos = 1 - (float)Math.Cos(a);
                var (fx, fy) = acrossFirst ? (sin, cos) : (cos, sin);
                pts.Add(x0 + (x1 - x0) * fx);
                pts.Add(y0 + (y1 - y0) * fy);
            }
        }
    }

    /// <summary>
    /// Stretches an operator that stretches across (an arrow, a bar or a brace over or under its base) to a width, drawn
    /// as rules and paths. A glyph as wide already stays the glyph.
    /// </summary>
    private static void StretchHorizontally(Element mo, MathBox box, float width)
    {
        var s = mo.Style;
        if (width <= box.W * 1.05f || !s.Visible) return;
        string text = MathElements.TokenText(mo);
        if (text.Length != 1) return;
        float em = s.FontSize, t = RuleThickness * em;
        uint color = CssColor.Fade(s.Color, s.InlineFade);
        var ops = new List<DrawOp>();
        float asc, desc;
        switch (text[0])
        {
            case '¯':
            case '‾':
                ops.Add(new RectOp(0, -t, width, t, color));
                (asc, desc) = (t, 0);
                break;
            case '_':
                ops.Add(new RectOp(0, 0, width, t, color));
                (asc, desc) = (0, t);
                break;
            case '→':
            case '←':
            case '↔':
            case '⟶':
            case '⟵':
            case '⟷':
            {
                float y = -AxisHeight * em, head = 0.12f * em, len = 0.28f * em;
                bool right = text[0] is '→' or '↔' or '⟶' or '⟷', left = text[0] is '←' or '↔' or '⟵' or '⟷';
                ops.Add(new RectOp(left ? t : 0, y - t / 2, width - (left ? t : 0) - (right ? t : 0), t, color));
                if (right) ops.Add(new PathOp([[width - len, y - head, width, y, width - len, y + head]], null, color) { StrokeWidth = t, Open = true });
                if (left) ops.Add(new PathOp([[len, y - head, 0, y, len, y + head]], null, color) { StrokeWidth = t, Open = true });
                (asc, desc) = (-y + head, y + head);
                break;
            }
            case '⏞':
            case '⏟':
            case '⏜':
            case '⏝':
            case '⎴':
            case '⎵':
            {
                // A brace across, its tips towards the base: over the base it opens downwards, under it upwards.
                bool over = text[0] is '⏞' or '⏜' or '⎴';
                float hb = 0.3f * em, r = Math.Min(hb / 2, width / 6), half = width / 2;
                float edge = over ? 0 : -hb, line = -hb / 2, tip = over ? -hb : 0;
                var pts = new List<float> { 0, edge };
                if (text[0] is '⎴' or '⎵') pts.AddRange([0, tip, width, tip, width, edge]);
                else if (text[0] is '⏜' or '⏝')
                    for (int i = 1; i <= 24; i++)
                    {
                        double a = i / 24.0 * Math.PI;
                        pts.Add(half - half * (float)Math.Cos(a));
                        pts.Add(edge + (tip - edge) * (float)Math.Sin(a));
                    }
                else
                {
                    pts.AddRange([r, line, half - r, line, half, tip, half + r, line, width - r, line, width, edge]);
                }
                ops.Add(new PathOp([pts.ToArray()], null, color) { StrokeWidth = t * 1.2f, Open = true });
                (asc, desc) = (hb, 0);
                if (!over)
                {
                    foreach (var op in ops) op.Offset(0, hb);
                    (asc, desc) = (0, hb);
                }
                break;
            }
            default:
                return;
        }
        box.Ops.Clear();
        box.Ops.AddRange(ops);
        (box.W, box.Asc, box.Desc, box.Drawn, box.Token) = (width, asc, desc, true, false);
    }

    /// <summary>A fraction: the numerator over the denominator, with a bar on the math axis between them.</summary>
    private MathBox MathFraction(Element e, List<Element> kids, bool display, int depth)
    {
        var s = e.Style;
        float em = s.FontSize, axis = AxisHeight * em, rule = RuleThickness * em;
        float t = e.GetAttribute("linethickness") is { } lt ? Math.Max(0, MathLength(lt, em, rule, rule, s)) : rule;
        var num = MathChild(e, kids, 0, display, depth);
        var den = MathChild(e, kids, 1, display, depth);
        float pad = InlineLayout.CssPixel;
        float inner = Math.Max(num.W, den.W), w = inner + 2 * pad;
        float up, down;
        if (t > 0)
        {
            up = Math.Max(display ? FracNumDisplayShiftUp * em : FracNumShiftUp * em, axis + t / 2 + (display ? 3 : 1) * t + num.Desc);
            down = Math.Max(display ? FracDenDisplayShiftDown * em : FracDenShiftDown * em, (display ? 3 : 1) * t + t / 2 - axis + den.Asc);
        }
        else
        {
            up = display ? FracNumDisplayShiftUp * em : StackTopShiftUp * em;
            down = display ? FracDenDisplayShiftDown * em : StackBottomShiftDown * em;
            float gap = (up - num.Desc) - (den.Asc - down), min = (display ? 7 : 3) * rule;
            if (gap < min)
            {
                up += (min - gap) / 2;
                down += (min - gap) / 2;
            }
        }
        var box = new MathBox { W = w, Asc = up + num.Asc, Desc = down + den.Desc };
        num.Place(box, pad + Align(e, "numalign", inner, num.W), -up);
        den.Place(box, pad + Align(e, "denalign", inner, den.W), down);
        if (t > 0 && s.Visible) box.Ops.Add(new RectOp(pad, -axis - t / 2, inner, t, CssColor.Fade(s.Color, s.InlineFade)));
        return box;

        static float Align(Element e, string attribute, float width, float w) => e.GetAttribute(attribute)?.Trim() switch
        {
            "left" => 0,
            "right" => width - w,
            _ => (width - w) / 2,
        };
    }

    /// <summary>
    /// A square root of the base, or an nth root with the index: a radical sign as tall as the base and a bar over it.
    /// The sign is drawn: a text font's √ cannot grow.
    /// </summary>
    private static MathBox MathRadical(Element e, MathBox inner, MathBox? index, bool display)
    {
        var s = e.Style;
        float em = s.FontSize, t = RuleThickness * em;
        float gap = (display ? RadicalDisplayGap : RadicalGap) * em + t / 4;
        float top = -(inner.Asc + gap + t), bottom = Math.Max(inner.Desc, 0.1f * em) + 0.05f * em;
        float h = bottom - top;
        float sign = Math.Min(0.833f * em, 0.5f * em + 0.08f * h);
        var box = new MathBox();
        float x = 0;
        if (index != null)
        {
            // The index sits over the radical's tick, clear of the stroke that rises to the bar.
            float before = RadicalKernBeforeDegree * em;
            float raise = RadicalDegreeRaise * h - bottom;
            index.Place(box, before, -raise - index.Desc);
            x = Math.Max(0, before + index.W - 0.6f * sign);
            box.Asc = Math.Max(box.Asc, raise + index.Desc + index.Asc);
        }
        if (s.Visible)
        {
            uint color = CssColor.Fade(s.Color, s.InlineFade);
            float y0 = bottom - Math.Min(0.55f * em, 0.5f * h), xv = x + 0.42f * sign, barY = top + t / 2;
            // The thin tick, the thick stroke down and the thin stroke up to the bar, which runs on over the base.
            box.Ops.Add(new PathOp([[x, y0 + 0.06f * em, x + 0.12f * sign, y0, xv, bottom, x + sign, barY, x + sign + inner.W + 0.05f * em, barY]], null, color)
                { StrokeWidth = t, Open = true });
            box.Ops.Add(new PathOp([[x + 0.12f * sign, y0, xv, bottom]], null, color) { StrokeWidth = 2.2f * t, Open = true });
        }
        inner.Place(box, x + sign, 0);
        box.W = x + sign + inner.W + 0.05f * em;
        box.Asc = Math.Max(box.Asc, -top + RadicalExtraAscender * em);
        box.Desc = Math.Max(inner.Desc, bottom);
        return box;
    }

    /// <summary>A base with a subscript, a superscript or both, shifted as the MATH constants say.</summary>
    private static MathBox MathScripts(Element e, MathBox b, MathBox? sub, MathBox? sup)
    {
        float em = e.Style.FontSize;
        float u = 0, v = 0;
        // A base that is more than a letter drops its scripts' baselines from its own top and bottom.
        bool drop = !b.Token;
        if (sup != null) u = Math.Max(Math.Max(SupShiftUp * em, drop ? b.Asc - SupDropMax * em : 0), sup.Desc + SupBottomMin * em);
        if (sub != null) v = Math.Max(Math.Max(SubShiftDown * em, drop ? b.Desc + SubDropMin * em : 0), sub.Asc - SubTopMax * em);
        if (sub != null && sup != null)
        {
            // The scripts keep a gap between them: the subscript goes down first, and the superscript up as far as its
            // bottom may go with a subscript under it, taking the subscript with it.
            float gap = (u - sup.Desc) - (sub.Asc - v);
            if (gap < SubSupGapMin * em)
            {
                v += SubSupGapMin * em - gap;
                float lift = SupBottomMaxWithSub * em - (u - sup.Desc);
                if (lift > 0)
                {
                    u += lift;
                    v = Math.Max(v - lift, SubSupGapMin * em + sub.Asc - (u - sup.Desc));
                }
            }
        }
        var box = new MathBox { Asc = b.Asc, Desc = b.Desc };
        b.Place(box, 0, 0);
        float scriptW = 0;
        if (sup != null)
        {
            sup.Place(box, b.W + b.ItalicCorrection, -u);
            scriptW = sup.W + b.ItalicCorrection;
            box.Asc = Math.Max(box.Asc, u + sup.Asc);
            box.Desc = Math.Max(box.Desc, sup.Desc - u);
        }
        if (sub != null)
        {
            sub.Place(box, b.W, v);
            scriptW = Math.Max(scriptW, sub.W);
            box.Asc = Math.Max(box.Asc, sub.Asc - v);
            box.Desc = Math.Max(box.Desc, v + sub.Desc);
        }
        box.W = b.W + scriptW + SpaceAfterScript * em;
        (box.Op, box.LSpace, box.RSpace) = (b.Op, b.LSpace, b.RSpace);
        return box;
    }

    /// <summary>
    /// A base with limits or accents under and over it, centred on it. A large operator with movable limits in compact
    /// style takes them as scripts instead, as in text.
    /// </summary>
    private MathBox MathUnderOver(Element e, List<Element> kids, bool display, int depth, MathForm form)
    {
        bool hasUnder = e.Name != "mover", hasOver = e.Name != "munder";
        var b = MathChild(e, kids, 0, display, depth, form);
        var under = hasUnder ? MathChild(e, kids, 1, display, depth) : null;
        var over = hasOver ? MathChild(e, kids, e.Name == "mover" ? 1 : kids.Count - 1, display, depth) : null;
        if (!display && b.Op is { } bop && bop.Has(MathOperatorFlags.MovableLimits))
            return MathScripts(e, b, under, over);
        float em = e.Style.FontSize, t = RuleThickness * em;
        bool largeOp = b.Op is { } o && o.Has(MathOperatorFlags.LargeOp);
        bool accentUnder = hasUnder && MathElements.IsAccent(e, true, kids[1]);
        bool accentOver = hasOver && MathElements.IsAccent(e, false, kids[^1]);

        // Operators that stretch across grow to the widest of the other parts.
        float target = 0;
        foreach (var part in (ReadOnlySpan<MathBox?>)[b, under, over])
            if (part != null && !(part.Stretchable != null && part.Op is { } p && p.Has(MathOperatorFlags.Stretchy) && p.Has(MathOperatorFlags.Horizontal)))
                target = Math.Max(target, part.W);
        foreach (var part in (ReadOnlySpan<MathBox?>)[b, under, over])
            if (part is { Stretchable: { } mo, Op: { } p } && p.Has(MathOperatorFlags.Stretchy) && p.Has(MathOperatorFlags.Horizontal) && target > 0)
                StretchHorizontally(mo, part, target);

        float w = Math.Max(b.W, Math.Max(under?.W ?? 0, over?.W ?? 0));
        var box = new MathBox { W = w, Asc = b.Asc, Desc = b.Desc };
        b.Place(box, (w - b.W) / 2, 0);
        if (over != null)
        {
            float shift;
            if (accentOver && !over.Drawn)
                shift = b.Asc + t + over.Desc; // an accent's ink sits just over the base's, wherever its glyph puts it
            else if (largeOp)
                shift = Math.Max(b.Asc + UpperLimitGapMin * em + over.Desc, b.Asc + UpperLimitRiseMin * em);
            else
                shift = b.Asc + (over.Drawn ? OverbarGap * em : 3 * t) + over.Desc;
            // An accent over an italic letter leans with it.
            float lean = accentOver ? b.ItalicCorrection / 2 : 0;
            over.Place(box, (w - over.W) / 2 + lean, -shift);
            box.Asc = Math.Max(box.Asc, shift + over.Asc);
        }
        if (under != null)
        {
            float shift;
            if (largeOp)
                shift = Math.Max(b.Desc + LowerLimitGapMin * em + under.Asc, b.Desc + LowerLimitDropMin * em);
            else
                shift = b.Desc + (accentUnder ? t : under.Drawn ? OverbarGap * em : 3 * t) + under.Asc;
            under.Place(box, (w - under.W) / 2, shift);
            box.Desc = Math.Max(box.Desc, shift + under.Desc);
        }
        (box.Op, box.LSpace, box.RSpace) = (b.Op, b.LSpace, b.RSpace);
        return box;
    }

    /// <summary>
    /// A base with any number of subscript and superscript pairs after it, and before it after mprescripts. Each pair
    /// is a column; none stands for an empty script.
    /// </summary>
    private MathBox MathMultiscripts(Element e, bool display, int depth, MathForm form)
    {
        var all = new List<Element>();
        foreach (var c in e.Children)
            if (c is Element el && !el.Detached && el.Name is not ("annotation" or "annotation-xml")) all.Add(el);
        float em = e.Style.FontSize;
        var b = MathElement(all[0], display, depth + 1, form);
        var post = new List<(MathBox? Sub, MathBox? Sup)>();
        var pre = new List<(MathBox? Sub, MathBox? Sup)>();
        var into = post;
        for (int i = 1; i < all.Count; i++)
        {
            if (all[i].Name == "mprescripts")
            {
                into = pre;
                continue;
            }
            var sub = Script(all[i]);
            var sup = i + 1 < all.Count && all[i + 1].Name != "mprescripts" ? Script(all[++i]) : null;
            into.Add((sub, sup));
        }
        float u = SupShiftUp * em, v = SubShiftDown * em;
        bool drop = !b.Token;
        if (drop) (u, v) = (Math.Max(u, b.Asc - SupDropMax * em), Math.Max(v, b.Desc + SubDropMin * em));
        foreach (var (sub, sup) in post.Concat(pre))
        {
            if (sup != null) u = Math.Max(u, sup.Desc + SupBottomMin * em);
            if (sub != null) v = Math.Max(v, sub.Asc - SubTopMax * em);
            if (sub != null && sup != null && (u - sup.Desc) - (sub.Asc - v) < SubSupGapMin * em) v += SubSupGapMin * em - ((u - sup.Desc) - (sub.Asc - v));
        }
        var box = new MathBox { Asc = b.Asc, Desc = b.Desc };
        float x = 0;
        foreach (var (sub, sup) in pre)
        {
            float cw = Math.Max(sub?.W ?? 0, sup?.W ?? 0);
            Put(sub, x + cw - (sub?.W ?? 0), v);
            Put(sup, x + cw - (sup?.W ?? 0), -u);
            x += cw + SpaceAfterScript * em;
        }
        b.Place(box, x, 0);
        x += b.W;
        foreach (var (sub, sup) in post)
        {
            Put(sub, x, v);
            Put(sup, x + b.ItalicCorrection, -u);
            x += Math.Max(sub?.W ?? 0, (sup?.W ?? 0) + b.ItalicCorrection) + SpaceAfterScript * em;
        }
        box.W = x;
        (box.Op, box.LSpace, box.RSpace) = (b.Op, b.LSpace, b.RSpace);
        return box;

        MathBox? Script(Element s) => s.Name == "none" || s.Style is not { Display: not Display.None } ? null
            : MathElement(s, false, depth + 1, MathForm.Infix);

        void Put(MathBox? s, float x0, float dy)
        {
            if (s == null) return;
            s.Place(box, x0, dy);
            box.Asc = Math.Max(box.Asc, s.Asc - dy);
            box.Desc = Math.Max(box.Desc, s.Desc + dy);
        }
    }

    /// <summary>
    /// A table: rows of cells, each column as wide as its widest cell and the cells centred in it, with the table
    /// centred on the math axis. A cell's padding is its style's (0.5ex by 0.4em in the user agent sheet).
    /// </summary>
    private MathBox MathTable(Element e, List<Element> rows, bool display, int depth)
    {
        float em = e.Style.FontSize;
        var cells = new List<List<(MathBox Box, float PadX, float PadY, string Align)>>();
        int columns = 0;
        string[] tableAlign = Words(e.GetAttribute("columnalign"));
        foreach (var row in rows)
        {
            var list = new List<(MathBox, float, float, string)>();
            var rowKids = row.Name is "mtr" or "mlabeledtr" ? MathElements.Children(row) : [row];
            string[] rowAlign = row.Name is "mtr" or "mlabeledtr" ? Words(row.GetAttribute("columnalign")) : [];
            // A labeled row's first cell is its label, which MathML Core does not show.
            int start = row.Name == "mlabeledtr" ? 1 : 0;
            for (int i = start; i < rowKids.Count; i++)
            {
                var cell = rowKids[i];
                var cs = cell.Style;
                bool isCell = cell.Name == "mtd";
                var box = isCell ? MathRow(cell, MathElements.Children(cell), false, depth + 2) : MathElement(cell, false, depth + 2, MathForm.Infix);
                if (isCell) MathDecorate(cell, box);
                float padX = isCell ? Fixed(cs.PaddingLeft) : 0, padY = isCell ? Fixed(cs.PaddingTop) : 0;
                int col = i - start;
                string align = (isCell ? cell.GetAttribute("columnalign")?.Trim() : null)
                    ?? (col < rowAlign.Length ? rowAlign[col] : rowAlign.Length > 0 ? rowAlign[^1] : null)
                    ?? (col < tableAlign.Length ? tableAlign[col] : tableAlign.Length > 0 ? tableAlign[^1] : "center");
                list.Add((box, padX, padY, align));
            }
            columns = Math.Max(columns, list.Count);
            cells.Add(list);
        }
        var widths = new float[columns];
        foreach (var row in cells)
            for (int c = 0; c < row.Count; c++) widths[c] = Math.Max(widths[c], row[c].Box.W + 2 * row[c].PadX);
        float total = 0;
        var rowAsc = new float[cells.Count];
        var rowDesc = new float[cells.Count];
        for (int r = 0; r < cells.Count; r++)
        {
            foreach (var (box, _, padY, _) in cells[r])
            {
                rowAsc[r] = Math.Max(rowAsc[r], box.Asc + padY);
                rowDesc[r] = Math.Max(rowDesc[r], box.Desc + padY);
            }
            total += rowAsc[r] + rowDesc[r];
        }
        float axis = AxisHeight * em;
        var table = new MathBox { W = widths.Sum(), Asc = total / 2 + axis, Desc = total / 2 - axis };
        float y = -table.Asc;
        for (int r = 0; r < cells.Count; r++)
        {
            float baseline = y + rowAsc[r], x = 0;
            for (int c = 0; c < cells[r].Count; c++)
            {
                var (box, padX, _, align) = cells[r][c];
                float free = widths[c] - 2 * padX - box.W;
                box.Place(table, x + padX + (align == "left" ? 0 : align == "right" ? free : free / 2), baseline);
                x += widths[c];
            }
            y += rowAsc[r] + rowDesc[r];
        }
        return table;

        static string[] Words(string? value) => value?.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries) ?? [];
    }

    /// <summary>mpadded: its own width, height and depth, and its content moved by lspace and voffset (MathML Core §3.3.6).</summary>
    private static void MathPadded(Element e, MathBox box)
    {
        float em = e.Style.FontSize;
        float w = box.W, asc = box.Asc, desc = box.Desc;
        float l = MathLength(e, "lspace", em, w, 0), dy = MathLength(e, "voffset", em, asc, 0);
        box.Move(l, -dy);
        box.W = Math.Max(0, MathLength(e, "width", em, w, w));
        box.Asc = MathLength(e, "height", em, asc, asc);
        box.Desc = MathLength(e, "depth", em, desc, desc);
    }

    /// <summary>The background and border a formula's element has of its own (mathbackground, merror), behind its content.</summary>
    private static void MathDecorate(Element e, MathBox box)
    {
        var s = e.Style;
        if (!s.Visible || box.W <= 0) return;
        float h = box.Asc + box.Desc;
        if (CssColor.IsVisible(s.BackgroundColor)) box.Ops.Insert(0, new RectOp(0, -box.Asc, box.W, h, CssColor.Fade(s.BackgroundColor, s.InlineFade)));
        if (e.Name == "merror" && s.BorderTop > 0)
        {
            uint c = CssColor.Fade(s.BorderTopColor ?? s.Color, s.InlineFade);
            float t = s.BorderTop;
            box.Ops.Add(new RectOp(0, -box.Asc, box.W, t, c));
            box.Ops.Add(new RectOp(0, box.Desc - t, box.W, t, c));
            box.Ops.Add(new RectOp(0, -box.Asc, t, h, c));
            box.Ops.Add(new RectOp(box.W - t, -box.Asc, t, h, c));
        }
    }

    private static float Fixed(Length l) => l.Unit == LengthUnit.Pt ? l.Value : 0;

    private static float MathLength(Element e, string attribute, float em, float percentOf, float fallback)
        => e.GetAttribute(attribute) is { } v ? MathLength(v, em, percentOf, fallback, e.Style) : fallback;

    /// <summary>
    /// A MathML length attribute in points: a CSS length or percentage, or one of the named spaces of MathML 3, or the
    /// fallback when it is neither.
    /// </summary>
    private static float MathLength(string value, float em, float percentOf, float fallback, ComputedStyle s)
    {
        var v = value.Trim().ToLowerInvariant();
        float named = v switch
        {
            "veryverythinmathspace" => 1, "verythinmathspace" => 2, "thinmathspace" => 3, "mediummathspace" => 4,
            "thickmathspace" => 5, "verythickmathspace" => 6, "veryverythickmathspace" => 7,
            "negativeveryverythinmathspace" => -1, "negativeverythinmathspace" => -2, "negativethinmathspace" => -3,
            "negativemediummathspace" => -4, "negativethickmathspace" => -5, "negativeverythickmathspace" => -6,
            "negativeveryverythickmathspace" => -7, _ => 0,
        };
        if (named != 0) return named / 18f * em;
        int end = 0;
        while (end < v.Length && (char.IsAsciiDigit(v[end]) || v[end] is '.' or '-' or '+')) end++;
        if (end == 0 || end > 24 || !float.TryParse(v.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out float n) || !float.IsFinite(n))
            return fallback;
        float size = v[end..].Trim() switch
        {
            "em" => n * em,
            "ex" => n * em * (s.Font.Metric(FontMetric.XHeight) is > 0 and var x ? x : 0.5f),
            "px" => n * InlineLayout.CssPixel,
            "pt" => n,
            "pc" => n * 12,
            "in" => n * 72,
            "cm" => n * 72 / 2.54f,
            "mm" => n * 72 / 25.4f,
            "%" => n / 100 * percentOf,
            // A bare number scales the default (MathML 3), as linethickness="2" does.
            "" => n * percentOf,
            _ => fallback,
        };
        return Math.Clamp(size, -Length.MaxMagnitude, Length.MaxMagnitude);
    }
}
