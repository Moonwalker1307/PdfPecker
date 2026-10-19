using System.Globalization;
using PdfPecker.Html;

namespace PdfPecker.Layout;

internal enum ControlKind { Text, CheckBox, Radio, Combo, List, Button }

/// <summary>
/// A form control (input, textarea, select or button) prepared for layout. Its value is laid out as ordinary content,
/// inside synthetic <c>pdf-field-value</c> children, and becomes the appearance of a PDF form field when
/// <see cref="Field"/> is set; otherwise it is drawn as it is.
/// </summary>
internal sealed class FormControl
{
    public ControlKind Kind;
    public bool Checked;
    public bool Multiline;
    public bool Disabled;
    public int Rows;            // list boxes and textareas: the rows shown
    public PdfFormField? Field; // null: drawn only (a button, a disabled control, or form fields turned off)
}

/// <summary>The interactive part of a control: what its PDF form field holds.</summary>
internal sealed class PdfFormField
{
    public string Name = "";
    public string? Value;           // text fields: the text; combo boxes: the selected option's export value
    public string OnState = "Yes";  // check boxes and radio buttons: the export value when checked
    public RadioGroup? Group;       // radio buttons with the same name
    public bool ReadOnly, Required, Password, MultiSelect, Multiline;
    public int MaxLength;
    public string? Tooltip;
    public List<(string Export, string Text)>? Options;
    public List<int>? Selected;
}

/// <summary>The radio buttons sharing a name: one PDF field whose widgets are the buttons.</summary>
internal sealed class RadioGroup
{
    public string Name = "";
    public string? Value; // the checked button's state, or null when none is checked
    public bool ReadOnly, Required;
    public readonly HashSet<string> States = new(StringComparer.Ordinal);
}

/// <summary>Turns form controls into boxes the layout engine can draw, with their values as content.</summary>
internal static class FormControls
{
    public const string ValueElement = "pdf-field-value";

    /// <summary>The characters a text field is wide by its size attribute (a textarea's cols), or null without one.</summary>
    public static int? FieldChars(Element e)
        => int.TryParse(e.GetAttribute(e.Name == "textarea" ? "cols" : "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
           && n > 0 ? Math.Min(n, 10000) : null;

    /// <summary>
    /// The content width, in em, Chrome gives a text field <paramref name="chars"/> characters wide: 0.6em a character,
    /// and a little more. Fields are 20 characters wide by default.
    /// </summary>
    public static float FieldWidthEm(int chars) => chars * 0.6f + 1.275f;

    private static readonly HashSet<string> TextTypes = new(StringComparer.Ordinal)
    {
        "text", "search", "email", "url", "tel", "number", "password", "date", "time", "datetime-local", "month", "week",
    };

    /// <summary>
    /// Prepares every control under <paramref name="root"/>: normalizes its type, gives it synthetic children holding its
    /// value, and, when <paramref name="interactive"/>, a uniquely named form field.
    /// </summary>
    public static void Prepare(Element root, bool interactive)
    {
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var groups = new Dictionary<string, RadioGroup>(StringComparer.Ordinal);
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        var labels = new Dictionary<string, Element>(StringComparer.Ordinal);
        CollectLabels(root, labels);
        Walk(root);

        void Walk(Element e)
        {
            for (int i = 0; i < e.Children.Count; i++)
                if (e.Children[i] is Element c)
                {
                    if (c.Name is "input" or "textarea" or "select" or "button") PrepareControl(c);
                    else if (c.Name != "svg") Walk(c);
                }
        }

        string Unique(string? requested, string fallback)
        {
            string name = Clean(requested);
            if (name.Length == 0)
            {
                counters.TryGetValue(fallback, out int n);
                counters[fallback] = ++n;
                name = fallback + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            string candidate = name;
            for (int k = 2; names.ContainsKey(candidate); k++) candidate = name + "_" + k.ToString(System.Globalization.CultureInfo.InvariantCulture);
            names[candidate] = 1;
            return candidate;
        }

        void PrepareControl(Element e)
        {
            bool disabled = e.HasAttribute("disabled") || InDisabledFieldset(e);
            bool fieldable = interactive && !disabled;
            var control = new FormControl { Disabled = disabled };
            PdfFormField NewField(string fallback) => new()
            {
                Name = Unique(e.GetAttribute("name"), fallback),
                ReadOnly = e.HasAttribute("readonly"),
                Required = e.HasAttribute("required"),
                Tooltip = Tooltip(e, labels),
            };
            switch (e.Name)
            {
                case "input":
                {
                    string type = (e.GetAttribute("type") ?? "text").Trim().ToLowerInvariant();
                    if (type.Length == 0 || type is not ("checkbox" or "radio" or "submit" or "reset" or "button" or "hidden" or "image" or "file" or "range" or "color")
                        && !TextTypes.Contains(type)) type = "text"; // an unknown type is a text field (HTML §4.10.5)
                    SetAttribute(e, "type", type);
                    if (type is "hidden" or "image" or "file" or "range" or "color") return; // not drawn (see the user agent styles)
                    string? value = e.GetAttribute("value");
                    switch (type)
                    {
                        case "checkbox":
                        case "radio":
                        {
                            control.Kind = type == "checkbox" ? ControlKind.CheckBox : ControlKind.Radio;
                            control.Checked = e.HasAttribute("checked");
                            if (!fieldable) break;
                            string state = Clean(value);
                            if (state == "Off") state = "Off_"; // Off is the unchecked state's name
                            if (type == "checkbox")
                            {
                                control.Field = NewField("checkbox");
                                control.Field.OnState = state.Length > 0 && state != "on" ? state : "Yes";
                                break;
                            }
                            // Radio buttons with the same name are one field; a button without a name stands alone.
                            string groupName = Clean(e.GetAttribute("name"));
                            if (groupName.Length == 0 || !groups.TryGetValue(groupName, out var group))
                            {
                                group = new RadioGroup { Name = Unique(groupName, "radio") };
                                if (groupName.Length > 0) groups[groupName] = group;
                            }
                            var f = new PdfFormField { Name = group.Name, Group = group, Tooltip = Tooltip(e, labels) };
                            // Each button needs its own state name: the value, or its position in the group.
                            f.OnState = state.Length > 0 && state != "on" ? state : "Choice";
                            int count = 1;
                            string baseState = f.OnState;
                            while (group.States.Contains(f.OnState)) f.OnState = baseState + (++count).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            group.States.Add(f.OnState);
                            group.ReadOnly |= e.HasAttribute("readonly");
                            group.Required |= e.HasAttribute("required");
                            // The last checked button wins, as in a browser.
                            if (control.Checked) group.Value = f.OnState;
                            control.Field = f;
                            break;
                        }
                        case "submit":
                        case "reset":
                        case "button":
                            control.Kind = ControlKind.Button;
                            SetValueChildren(e, Line(value ?? (type == "submit" ? "Submit" : type == "reset" ? "Reset" : "")));
                            break;
                        default:
                        {
                            control.Kind = ControlKind.Text;
                            value = SingleLine(value ?? "");
                            string shown = type == "password" ? new string('•', value.Length) : value;
                            if (shown.Length == 0 && e.GetAttribute("placeholder") is { Length: > 0 } ph)
                                SetValueChildren(e, Line(SingleLine(ph), "placeholder"));
                            else SetValueChildren(e, Line(shown));
                            if (!fieldable) break;
                            var f = control.Field = NewField("text");
                            f.Password = type == "password";
                            if (!f.Password) f.Value = value; // a password is not stored in the file
                            if (Invariant.TryParseInt(e.GetAttribute("maxlength"), out int max) && max > 0) f.MaxLength = max;
                            break;
                        }
                    }
                    break;
                }
                case "textarea":
                {
                    control.Kind = ControlKind.Text;
                    control.Multiline = true;
                    control.Rows = Invariant.TryParseInt(e.GetAttribute("rows"), out int rows) && rows > 0 ? Math.Min(rows, 10000) : 2;
                    string value = e.InnerText();
                    // A line break right after the start tag is not part of the value (HTML §13.2.6.4.7).
                    if (value.StartsWith("\r\n", StringComparison.Ordinal)) value = value.Substring(2);
                    else if (value.StartsWith('\n')) value = value.Substring(1);
                    value = value.Replace("\r\n", "\n").Replace('\r', '\n');
                    if (value.Length == 0 && e.GetAttribute("placeholder") is { Length: > 0 } ph)
                        SetValueChildren(e, Line(ph, "placeholder"));
                    else SetValueChildren(e, Line(value.Length == 0 ? " " : value));
                    if (!fieldable) break;
                    var f = control.Field = NewField("textarea");
                    f.Multiline = true;
                    f.Value = value;
                    if (Invariant.TryParseInt(e.GetAttribute("maxlength"), out int max) && max > 0) f.MaxLength = max;
                    break;
                }
                case "select":
                {
                    var options = new List<(string Export, string Text, bool Selected)>();
                    CollectOptions(e, options);
                    bool multiple = e.HasAttribute("multiple");
                    Invariant.TryParseInt(e.GetAttribute("size"), out int size);
                    bool list = multiple || size > 1;
                    control.Kind = list ? ControlKind.List : ControlKind.Combo;
                    var selected = new List<int>();
                    for (int i = 0; i < options.Count; i++)
                        if (options[i].Selected)
                        {
                            if (!multiple) selected.Clear(); // a single select keeps the last selected option
                            selected.Add(i);
                        }
                    if (!list && selected.Count == 0 && options.Count > 0) selected.Add(0); // a drop-down shows its first option
                    var children = new List<Element>();
                    if (list)
                    {
                        // The rows that fit; the others only give the box its width.
                        control.Rows = size > 1 ? size : 4;
                        for (int i = 0; i < options.Count; i++)
                        {
                            var line = Line(options[i].Text, i >= control.Rows ? "sizer" : "option");
                            if (selected.Contains(i)) line.AddAttribute("selected", "");
                            children.Add(line);
                        }
                        if (options.Count == 0) children.Add(Line(" "));
                    }
                    else
                    {
                        children.Add(Line(selected.Count > 0 && options[selected[0]].Text.Length > 0 ? options[selected[0]].Text : " "));
                        // A drop-down is as wide as its widest option: the others take width, but no height or ink.
                        foreach (var o in options) children.Add(Line(o.Text, "sizer"));
                        // The lines carry the room Chrome keeps inside a drop-down, around the page's own padding.
                        foreach (var line in children) line.AddAttribute("combo", "");
                    }
                    SetValueChildren(e, children.ToArray());
                    if (!fieldable) break;
                    var f = control.Field = NewField("select");
                    f.MultiSelect = multiple;
                    f.Options = options.Select(o => (o.Export, o.Text)).ToList();
                    f.Selected = selected;
                    if (selected.Count > 0) f.Value = options[selected[0]].Export;
                    break;
                }
                case "button":
                    control.Kind = ControlKind.Button; // drawn with its content; a PDF has nothing to submit
                    Walk(e);
                    break;
            }
            e.Control = control;
        }
    }

    private static void CollectOptions(Element e, List<(string Export, string Text, bool Selected)> options)
    {
        foreach (var c in e.Children)
        {
            if (c is not Element o) continue;
            if (o.Name == "optgroup") { CollectOptions(o, options); continue; }
            if (o.Name != "option") continue;
            string text = Collapse(o.GetAttribute("label") ?? o.InnerText());
            string export = o.GetAttribute("value") ?? Collapse(o.InnerText());
            options.Add((export, text, o.HasAttribute("selected")));
        }
    }

    private static bool InDisabledFieldset(Element e)
    {
        for (var p = e.Parent; p != null; p = p.Parent)
            if (p.Name == "fieldset" && p.HasAttribute("disabled")) return true;
        return false;
    }

    /// <summary>The field's description: its title or aria-label, or else the text of its label.</summary>
    private static string? Tooltip(Element e, Dictionary<string, Element> labels)
    {
        var t = e.GetAttribute("title") ?? e.GetAttribute("aria-label");
        if (!string.IsNullOrWhiteSpace(t)) return t.Trim();
        Element? label = null;
        if (e.GetAttribute("id") is { Length: > 0 } id) labels.TryGetValue(id, out label);
        for (var p = e.Parent; label == null && p != null; p = p.Parent)
            if (p.Name == "label") label = p;
        if (label == null) return null;
        var sb = new System.Text.StringBuilder();
        LabelText(label, sb);
        var text = Collapse(sb.ToString());
        return text.Length == 0 ? null : text;
    }

    private static void LabelText(Element e, System.Text.StringBuilder sb)
    {
        foreach (var c in e.Children)
            if (c is TextNode t) sb.Append(t.Text);
            else if (c is Element el && el.Name is not ("input" or "select" or "textarea" or "button" or "script" or "style")) LabelText(el, sb);
    }

    private static void CollectLabels(Element e, Dictionary<string, Element> labels)
    {
        foreach (var c in e.Children)
            if (c is Element el)
            {
                if (el.Name == "label" && el.GetAttribute("for") is { Length: > 0 } f) labels.TryAdd(f, el);
                CollectLabels(el, labels);
            }
    }

    /// <summary>A field name: PDF joins the names of nested fields with periods, so a period cannot be part of one.</summary>
    private static string Clean(string? name) => (name ?? "").Trim().Replace('.', '_');

    private static string Collapse(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string SingleLine(string s) => s.Replace("\r", "").Replace("\n", "");

    private static Element Line(string text, string? flag = null)
    {
        var el = new Element(ValueElement);
        if (flag != null) el.AddAttribute(flag, "");
        el.AppendChild(new TextNode(text.Length == 0 ? " " : text));
        return el;
    }

    private static void SetValueChildren(Element e, params Element[] children)
    {
        e.Children.Clear();
        foreach (var c in children) e.AppendChild(c);
    }

    private static void SetAttribute(Element e, string name, string value)
    {
        var attrs = e.Attributes;
        for (int i = 0; i < attrs.Count; i++)
            if (attrs[i].Key == name) { attrs[i] = new KeyValuePair<string, string>(name, value); return; }
        e.AddAttribute(name, value);
    }
}
