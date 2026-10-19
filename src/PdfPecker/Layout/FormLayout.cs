using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Layout;

internal sealed partial class LayoutEngine
{
    private const uint Accent = 0xFF0075FF, DisabledAccent = 0xFFBDBDBD; // Chrome's checked check box and radio button

    /// <summary>
    /// Finishes a form control laid out as the border box (x, y, w, h) whose content, its value, is the ops from
    /// <paramref name="start"/>: centres a one-line value vertically, as Chrome does, draws a check box's or radio
    /// button's checked look, and turns it all into a form field, or clips it to the box when the control is only drawn.
    /// </summary>
    private void FinishControl(Element e, FormControl control, int start, float x, float y, float w, float h, float laidH, float contentH)
    {
        var s = e.Style;
        if (!s.Visible) return;
        float px = x + s.BorderLeft, py = y + s.BorderTop;
        float pw = Math.Max(0, w - s.BorderLeft - s.BorderRight), ph = Math.Max(0, h - s.BorderTop - s.BorderBottom);
        if (control.Kind is ControlKind.Text or ControlKind.Combo or ControlKind.Button && !control.Multiline && contentH > laidH + Eps)
        {
            float dy = (contentH - laidH) / 2;
            for (int i = start; i < _ops.Count; i++) _ops[i].Offset(0, dy);
            if (!float.IsNaN(_lastBaseline)) _lastBaseline += dy;
        }
        // The box's baseline, for the text beside it: a one-line value's (a drop-down's other lines only give it its
        // width); a textarea, list box, check box or radio button sits on its bottom edge, as in Chrome.
        if (control.Kind is ControlKind.Combo or ControlKind.Text && !control.Multiline)
            _lastBaseline = FirstBaseline(start) ?? float.NaN;
        else if (control.Kind != ControlKind.Button) _lastBaseline = float.NaN;

        bool button = control.Kind is ControlKind.CheckBox or ControlKind.Radio;
        if (button)
        {
            // Drawn checked over the whole box, border included; the unchecked box is the page's own drawing. A field
            // keeps the checked look as its "on" appearance either way.
            if (control.Checked || control.Field != null) AddCheckedLook(control.Kind, x, y, w, h, control.Disabled ? DisabledAccent : Accent);
            if (control.Field == null) return;
        }

        RoundRectOp? arrow = null;
        if (control.Kind == ControlKind.Combo && pw > 12)
        {
            // The drop-down arrow, part of the page like the box, in the 20px (15pt) Chrome keeps for it at the end
            // whatever the page's padding.
            float size = Math.Min(s.FontSize * 0.5f, 6), ax = px + pw - Math.Max(15, size + 4) / 2 - size / 2;
            float ay = py + ph / 2 - size / 4;
            arrow = new RoundRectOp(ax, ay, size, size / 2, default, s.Color) { Clip = new[] { ax, ay, ax + size, ay, ax + size / 2, ay + size / 2 } };
        }

        // The value is clipped to the box, as a viewer clips it to the field.
        if (!button && _ops.Count > start) WrapOps(start, new ClipOp(px, py, pw, ph, null));
        if (control.Field != null && control.Kind != ControlKind.Button)
        {
            var field = button ? new FieldOp(x, y, w, h, control.Field) : new FieldOp(px, py, pw, ph, control.Field);
            field.Kind = control.Kind;
            field.Checked = control.Checked;
            field.Font = s.Font;
            field.FontSize = s.FontSize;
            field.Color = s.Color;
            field.Align = ComputedStyle.Resolve(s.TextAlign, s.Rtl) switch { TextAlign.Center => 1, TextAlign.Right => 2, _ => 0 };
            field.Form = Tags?.ElementOf(e);
            WrapOps(start, field);
        }
        if (arrow != null) _ops.Add(arrow);
    }

    private float? FirstBaseline(int start)
    {
        static float? Find(DrawOp op) => op switch
        {
            TextOp t => t.Baseline,
            GroupOp g => g.Ops.Select(Find).FirstOrDefault(b => b != null),
            _ => null,
        };
        for (int i = start; i < _ops.Count; i++)
            if (Find(_ops[i]) is float b) return b;
        return null;
    }

    /// <summary>Moves the ops from <paramref name="start"/> into <paramref name="group"/>, which takes their place.</summary>
    private void WrapOps(int start, GroupOp group)
    {
        for (int i = start; i < _ops.Count; i++) group.Ops.Add(_ops[i]);
        Truncate(start, keepFloats: true);
        _ops.Add(group);
        _floats.Fold(start);
    }

    /// <summary>A checked check box (a blue square with a white tick) or radio button (a blue ring round a blue dot).</summary>
    private void AddCheckedLook(ControlKind kind, float x, float y, float w, float h, uint accent)
    {
        if (w <= 0 || h <= 0) return;
        if (kind == ControlKind.Radio)
        {
            float cx = x + w / 2, cy = y + h / 2, r = Math.Min(w, h) / 2;
            _ops.Add(new CircleOp(cx, cy, r, accent, filled: true));
            _ops.Add(new CircleOp(cx, cy, r * 0.77f, 0xFFFFFFFF, filled: true));
            _ops.Add(new CircleOp(cx, cy, r * 0.54f, accent, filled: true));
            return;
        }
        float rad = Math.Min(1.5f, Math.Min(w, h) / 4);
        _ops.Add(new RoundRectOp(x, y, w, h, new CornerRadii(rad, rad, rad, rad, rad, rad, rad, rad), accent));
        // The tick, as a polygon: two strokes of thickness t meeting at the bottom.
        float t = Math.Min(w, h) * 0.14f;
        (float X, float Y) a = (x + w * 0.2f, y + h * 0.52f), b = (x + w * 0.42f, y + h * 0.74f), c = (x + w * 0.8f, y + h * 0.28f);
        static (float, float) Normal((float X, float Y) p, (float X, float Y) q, float d)
        {
            float dx = q.X - p.X, dy = q.Y - p.Y, len = MathF.Sqrt(dx * dx + dy * dy);
            return (-dy / len * d, dx / len * d);
        }
        var n1 = Normal(a, b, t / 2);
        var n2 = Normal(b, c, t / 2);
        // The outer corner at b, below the tick, and the inner one above it.
        var poly = new[]
        {
            a.X - n1.Item1, a.Y - n1.Item2,
            b.X - (n1.Item1 + n2.Item1) / 2 * 1.4f, b.Y - (n1.Item2 + n2.Item2) / 2 * 1.4f,
            c.X - n2.Item1, c.Y - n2.Item2,
            c.X + n2.Item1, c.Y + n2.Item2,
            b.X + (n1.Item1 + n2.Item1) / 2 * 1.4f, b.Y + (n1.Item2 + n2.Item2) / 2 * 1.4f,
            a.X + n1.Item1, a.Y + n1.Item2,
        };
        _ops.Add(new RoundRectOp(x, y, w, h, default, 0xFFFFFFFF) { Clip = poly });
    }
}
