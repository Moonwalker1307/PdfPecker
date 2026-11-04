using System.Globalization;
using System.Text;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>Pages (6.10, 6.1.13), annotations (6.3), forms (6.4), optional content (6.9) and attachments (6.8).</summary>
internal sealed partial class PdfAConverter
{
    // Annotation types PDF/A allows (6.3.1).
    private static readonly HashSet<string> AllowedAnnotations = new(StringComparer.Ordinal)
    {
        "Text", "Link", "FreeText", "Line", "Square", "Circle", "Polygon", "PolyLine", "Highlight", "Underline", "Squiggly", "StrikeOut",
        "Stamp", "Caret", "Ink", "Popup", "FileAttachment", "Widget", "PrinterMark", "TrapNet", "Watermark", "Redact",
    };

    // Annotation flags (ISO 32000-1, 12.5.3).
    private const int InvisibleFlag = 1, HiddenFlag = 2, PrintFlag = 4, NoViewFlag = 32, ToggleNoViewFlag = 256;

    private static readonly string[] PageBoxes = { "MediaBox", "CropBox", "BleedBox", "TrimBox", "ArtBox" };

    // The colours viewers draw markup in when the annotation gives none.
    private static readonly double[] Yellow = { 1, 1, 0 }, Black = { 0, 0, 0 }, NoteYellow = { 1, 0.85, 0.2 };

    /// <summary>The largest and smallest page and box sizes PDF/A allows, in default user space units (6.1.13).</summary>
    private const double MinBoxSize = 3, MaxBoxSize = 14400;

    private void Pages(PdfDict catalog)
    {
        var acroForm = _file.GetDict(catalog, "AcroForm");
        var formResources = _file.GetDict(acroForm, "DR");
        int pages = 0;
        PageTree.Walk(_file, catalog["Pages"], (node, page, inherited) =>
        {
            if ((++pages & 15) == 0) PdfFile.CheckCancel();
            if (page.ContainsKey("PresSteps"))
            {
                Found("Removed the presentation steps of {0} page{s}, which PDF/A forbids.", "{0} page{s} have presentation steps, which PDF/A forbids.");
                if (_fix) page.Entries.Remove("PresSteps");
            }
            foreach (var key in PageBoxes)
            {
                var box = key == "MediaBox" ? _file.Resolve(inherited.MediaBox) : key == "CropBox" ? _file.Resolve(inherited.CropBox) : _file.Resolve(page[key]);
                if (box is PdfArray { Count: 4 } a && _file.ToNumbers(a) is { } n
                    && (Math.Abs(n[2] - n[0]) is < MinBoxSize or > MaxBoxSize || Math.Abs(n[3] - n[1]) is < MinBoxSize or > MaxBoxSize))
                    Problem("{0} page box{s} are smaller than 3 or larger than 14,400 units, the PDF/A limits.");
            }
            if (_file.GetDict(page, "Group") is { } group) Group(group);
            var resources = _file.Resolve(inherited.Resources) as PdfDict;
            if (resources == null && _fix && Uses(page))
            {
                // A page that draws without resources still needs a dictionary for the default colour spaces.
                resources = new PdfDict();
                page.Entries["Resources"] = resources;
            }
            else if (resources != null && _fix && !page.ContainsKey("Resources"))
            {
                // Resources inherited from the page tree are not explicit enough for validators (6.2.2).
                page.Entries["Resources"] = inherited.Resources!;
            }
            if (resources != null) Resources(resources);
            WalkPage(page, resources);
            Annotations(page, formResources);
            return true;
        });
    }

    private static bool Uses(PdfDict page) => page["Contents"] != null;

    // ------------------------------------------------------------------ annotations (6.3)

    private void Annotations(PdfDict page, PdfDict? formResources)
    {
        if (_file.GetArray(page, "Annots") is not { } annots) return;
        var removed = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < annots.Count; i++)
        {
            if (_file.Resolve(annots[i]) is not PdfDict annot) continue;
            var subtype = _file.GetName(annot, "Subtype") ?? "";
            if (!AllowedAnnotations.Contains(subtype))
            {
                Found("Removed {0} annotation{s} of types PDF/A forbids, such as sound, video and 3D{names}.",
                    "The document has {0} annotation{s} of types PDF/A forbids{names}.", Printable(subtype));
                removed.Add(annot);
                continue;
            }
            int flags = _file.GetInt(annot, "F", 0);
            bool hidden = (flags & (HiddenFlag | NoViewFlag)) != 0;
            if (hidden && subtype != "Widget" && subtype != "Popup")
            {
                Found("Removed {0} hidden annotation{s}; PDF/A requires every annotation to be visible and printable.",
                    "{0} annotation{s} are hidden, which PDF/A forbids.");
                removed.Add(annot);
                continue;
            }
            Flags(annot, subtype, flags, hidden);
            if (subtype == "Widget") Widget(annot);
            Appearance(annot, subtype, hidden);
            // The appearances draw with their own resources, or (fields) with the form's default resources.
            if (_file.GetDict(annot, "AP") is { } ap)
            {
                switch (_file.Resolve(ap["N"]))
                {
                    case PdfStream normal:
                        WalkStream(normal, subtype == "Widget" ? formResources : null, new TextState(null, 0), 0);
                        break;
                    case PdfDict states:
                        foreach (var state in states.Entries.Values)
                            if (_file.Resolve(state) is PdfStream s) WalkStream(s, subtype == "Widget" ? formResources : null, new TextState(null, 0), 0);
                        break;
                }
            }
        }
        if (removed.Count == 0 || !_fix) return;
        // A popup goes with the annotation it belongs to.
        foreach (var item in annots.Items)
            if (_file.Resolve(item) is PdfDict a && _file.GetName(a, "Subtype") == "Popup" && _file.Resolve(a["Parent"]) is PdfDict parent && removed.Contains(parent))
                removed.Add(a);
        annots.Items.RemoveAll(item => _file.Resolve(item) is PdfDict a && removed.Contains(a));
    }

    /// <summary>Annotations must print and may not be hidden (6.3.2); hidden form fields keep their place with an empty appearance.</summary>
    private void Flags(PdfDict annot, string subtype, int flags, bool hidden)
    {
        if (subtype == "Popup" && !annot.ContainsKey("F")) return;
        int wanted = (flags | PrintFlag) & ~(InvisibleFlag | HiddenFlag | NoViewFlag | ToggleNoViewFlag);
        if (annot.ContainsKey("F") && wanted == flags) return;
        Found("Set {0} annotation{s} to print, as PDF/A requires; they look the same on screen.", "{0} annotation{s} are not set to print or are hidden, which PDF/A forbids.");
        if (!_fix) return;
        annot.Entries["F"] = PdfNumber.Int(wanted);
        if (hidden)
        {
            // A hidden field stays hidden by drawing nothing.
            _report.Add("Gave {0} hidden form field{s} an empty appearance, since PDF/A does not allow hidden annotations.");
            annot.Entries["AP"] = NewDict(("N", IsButton(annot) ? NewDict((State(annot), EmptyAppearance(annot))) : EmptyAppearance(annot)));
        }
    }

    /// <summary>Form fields may not have actions (6.4.1): a button that ran an action no longer does.</summary>
    private void Widget(PdfDict annot)
    {
        if (!annot.ContainsKey("A")) return;
        Found("Removed the actions of {0} form field{s}, which PDF/A forbids.", "{0} form field{s} have actions, which PDF/A forbids.");
        if (_fix) annot.Entries.Remove("A");
    }

    /// <summary>
    /// Appearances (6.3.3): only a normal appearance; a dictionary of states for buttons and a stream for everything else;
    /// and one for every annotation but popups, links and those of no size.
    /// </summary>
    private void Appearance(PdfDict annot, string subtype, bool hidden)
    {
        var ap = _file.GetDict(annot, "AP");
        if (ap != null && ap.Entries.Keys.Any(k => k != "N"))
        {
            Found("Removed the rollover and pressed appearances of {0} annotation{s}, which PDF/A forbids; the normal appearance stays.",
                "{0} annotation{s} have rollover or pressed appearances, which PDF/A forbids.");
            if (_fix)
                foreach (var key in ap.Entries.Keys.Where(k => k != "N").ToList())
                    ap.Entries.Remove(key);
        }
        var normal = _file.Resolve(ap?["N"]);
        bool button = subtype == "Widget" && IsButton(annot);
        if (button)
        {
            if (normal is PdfDict states && !(normal is PdfStream) && states.Entries.Values.Any(v => _file.Resolve(v) is PdfStream)) return;
            if (normal is PdfStream stream)
            {
                Found("Put the appearance of {0} button{s} into a state dictionary, as PDF/A requires.", "{0} button{s} have an appearance PDF/A does not accept.");
                if (_fix)
                {
                    var state = State(annot);
                    ap!.Entries["N"] = NewDict((state, ap["N"]!));
                    annot.Entries["AS"] = PdfName.Get(state);
                }
                return;
            }
        }
        else
        {
            if (normal is PdfStream) return;
            if (normal is PdfDict states && _fix)
            {
                // A state dictionary on anything but a button: the current state's appearance is what viewers show.
                var current = _file.GetName(annot, "AS") is { } asName ? states[asName] : null;
                current ??= states.Entries.Values.FirstOrDefault(v => _file.Resolve(v) is PdfStream);
                if (current != null)
                {
                    _report.Add("Reduced the appearance states of {0} annotation{s} to the one shown, as PDF/A requires.");
                    ap!.Entries["N"] = current;
                    return;
                }
            }
        }
        if (subtype is "Popup" or "Link") return;
        if (_file.GetNumbers(annot, "Rect") is { Length: 4 } r && r[2] - r[0] == 0 && r[3] - r[1] == 0) return;
        if (!_fix)
        {
            Problem("{0} annotation{s} have no appearance, which PDF/A requires{names}.", Printable(subtype));
            return;
        }
        var generated = GeneratedAppearance(annot, subtype);
        var appearance = generated ?? EmptyAppearance(annot);
        annot.Entries["AP"] = NewDict(("N", button ? NewDict((State(annot), appearance)) : appearance));
        if (button) annot.Entries["AS"] = PdfName.Get(State(annot));
        _report.Add(generated != null
            ? "Drew appearances for {0} annotation{s} that had none, as PDF/A requires{names}."
            : "Gave {0} annotation{s} without an appearance an empty one, as PDF/A requires{names}.",
            Printable(subtype));
    }

    private bool IsButton(PdfDict annot)
    {
        PdfDict? field = annot;
        for (int depth = 0; field != null && depth < MaxDepth; depth++)
        {
            if (_file.GetName(field, "FT") is { } ft) return ft == "Btn";
            field = _file.GetDict(field, "Parent");
        }
        return false;
    }

    /// <summary>The state name a button's appearance is kept under: its current state, or one that says the appearance is the default.</summary>
    private string State(PdfDict annot) => _file.GetName(annot, "AS") is { Length: > 0 } state ? state : "Off";

    /// <summary>An appearance that draws nothing, covering the annotation's rectangle.</summary>
    private PdfRef EmptyAppearance(PdfDict annot)
    {
        var rect = _file.GetNumbers(annot, "Rect") ?? new double[] { 0, 0, 0, 0 };
        double w = rect.Length >= 4 ? Math.Abs(rect[2] - rect[0]) : 0, h = rect.Length >= 4 ? Math.Abs(rect[3] - rect[1]) : 0;
        return _file.AddObject(new PdfStream(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = NewArray(PdfNumber.Int(0), PdfNumber.Int(0), Number(w), Number(h)),
        }, Array.Empty<byte>()));
    }

    // ------------------------------------------------------------------ drawn appearances

    /// <summary>
    /// An appearance drawn from the annotation's own properties, as viewers draw annotations that have none: the markup
    /// over text, shapes, lines and ink, and a note icon. Null for the types whose look depends on the viewer.
    /// </summary>
    private PdfRef? GeneratedAppearance(PdfDict annot, string subtype)
    {
        var rect = _file.GetNumbers(annot, "Rect");
        if (rect == null || rect.Length < 4) return null;
        double x0 = Math.Min(rect[0], rect[2]), y0 = Math.Min(rect[1], rect[3]), x1 = Math.Max(rect[0], rect[2]), y1 = Math.Max(rect[1], rect[3]);
        var colour = _file.GetNumbers(annot, "C");
        var interior = _file.GetNumbers(annot, "IC");
        double width = _file.GetNumber(_file.GetDict(annot, "BS"), "W", _file.GetNumbers(annot, "Border") is { Length: >= 3 } border ? border[2] : 1);
        var b = new StringBuilder();
        var space = DeviceSpace.None;
        bool multiply = false;
        string? Colour(double[]? c, bool forStroke)
        {
            if (c == null) return null;
            string Num(double v) => Math.Round(Math.Clamp(v, 0, 1), 4).ToString(CultureInfo.InvariantCulture);
            switch (c.Length)
            {
                case 1:
                    space |= DeviceSpace.Gray;
                    return Num(c[0]) + (forStroke ? " G\n" : " g\n");
                case 3:
                    space |= DeviceSpace.Rgb;
                    return Num(c[0]) + " " + Num(c[1]) + " " + Num(c[2]) + (forStroke ? " RG\n" : " rg\n");
                case 4:
                    space |= DeviceSpace.Cmyk;
                    return Num(c[0]) + " " + Num(c[1]) + " " + Num(c[2]) + " " + Num(c[3]) + (forStroke ? " K\n" : " k\n");
                default:
                    return null;
            }
        }
        string P(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);
        var quads = _file.GetNumbers(annot, "QuadPoints");
        switch (subtype)
        {
            case "Highlight" when quads != null && quads.Length >= 8:
                if (Colour(colour ?? Yellow, false) is not { } highlight) return null;
                multiply = true;
                b.Append("/M gs\n").Append(highlight);
                for (int i = 0; i + 8 <= quads.Length; i += 8)
                    b.Append(P(quads[i])).Append(' ').Append(P(quads[i + 1])).Append(" m ").Append(P(quads[i + 2])).Append(' ').Append(P(quads[i + 3]))
                     .Append(" l ").Append(P(quads[i + 6])).Append(' ').Append(P(quads[i + 7])).Append(" l ").Append(P(quads[i + 4])).Append(' ')
                     .Append(P(quads[i + 5])).Append(" l h\n");
                b.Append("f\n");
                break;
            case "Underline" or "StrikeOut" or "Squiggly" when quads != null && quads.Length >= 8:
                if (Colour(colour ?? Black, true) is not { } markup) return null;
                b.Append(markup);
                for (int i = 0; i + 8 <= quads.Length; i += 8)
                {
                    // The quadrilateral's bottom edge runs from its third point to its fourth, its top from the first to the second.
                    double bx0 = quads[i + 4], by0 = quads[i + 5], bx1 = quads[i + 6], by1 = quads[i + 7];
                    double tx0 = quads[i], ty0 = quads[i + 1];
                    double height = Math.Sqrt((tx0 - bx0) * (tx0 - bx0) + (ty0 - by0) * (ty0 - by0));
                    double lw = Math.Max(0.5, height / 14);
                    b.Append(P(lw)).Append(" w\n");
                    double t = subtype == "StrikeOut" ? 0.4 : 0.08;
                    double sx0 = bx0 + (tx0 - bx0) * t, sy0 = by0 + (ty0 - by0) * t;
                    double sx1 = bx1 + (quads[i + 2] - bx1) * t, sy1 = by1 + (quads[i + 3] - by1) * t;
                    if (subtype == "Squiggly")
                    {
                        double length = Math.Sqrt((sx1 - sx0) * (sx1 - sx0) + (sy1 - sy0) * (sy1 - sy0));
                        int steps = Math.Clamp((int)(length / Math.Max(1, height / 6)), 1, 2000);
                        double nx = -(sy1 - sy0) / Math.Max(length, 1e-9) * height / 12, ny = (sx1 - sx0) / Math.Max(length, 1e-9) * height / 12;
                        b.Append(P(sx0)).Append(' ').Append(P(sy0)).Append(" m\n");
                        for (int k = 1; k <= steps; k++)
                        {
                            double f = (double)k / steps, s = (k & 1) == 1 ? 1 : -1;
                            b.Append(P(sx0 + (sx1 - sx0) * f + nx * s)).Append(' ').Append(P(sy0 + (sy1 - sy0) * f + ny * s)).Append(" l\n");
                        }
                        b.Append("S\n");
                    }
                    else b.Append(P(sx0)).Append(' ').Append(P(sy0)).Append(" m ").Append(P(sx1)).Append(' ').Append(P(sy1)).Append(" l S\n");
                }
                break;
            case "Square" or "Circle":
            {
                var stroke = Colour(colour, true);
                var fill = Colour(interior, false);
                if (stroke == null && fill == null) return null;
                if (stroke != null) b.Append(stroke).Append(P(width)).Append(" w\n");
                if (fill != null) b.Append(fill);
                double inset = stroke != null ? width / 2 : 0;
                double ax0 = x0 + inset, ay0 = y0 + inset, ax1 = x1 - inset, ay1 = y1 - inset;
                if (subtype == "Square")
                    b.Append(P(ax0)).Append(' ').Append(P(ay0)).Append(' ').Append(P(ax1 - ax0)).Append(' ').Append(P(ay1 - ay0)).Append(" re\n");
                else
                {
                    // Four Bézier arcs, each a quarter of the ellipse.
                    const double k = 0.5523;
                    double cx = (ax0 + ax1) / 2, cy = (ay0 + ay1) / 2, rx = (ax1 - ax0) / 2, ry = (ay1 - ay0) / 2;
                    b.Append(P(cx + rx)).Append(' ').Append(P(cy)).Append(" m\n")
                     .Append(P(cx + rx)).Append(' ').Append(P(cy + ry * k)).Append(' ').Append(P(cx + rx * k)).Append(' ').Append(P(cy + ry)).Append(' ').Append(P(cx)).Append(' ').Append(P(cy + ry)).Append(" c\n")
                     .Append(P(cx - rx * k)).Append(' ').Append(P(cy + ry)).Append(' ').Append(P(cx - rx)).Append(' ').Append(P(cy + ry * k)).Append(' ').Append(P(cx - rx)).Append(' ').Append(P(cy)).Append(" c\n")
                     .Append(P(cx - rx)).Append(' ').Append(P(cy - ry * k)).Append(' ').Append(P(cx - rx * k)).Append(' ').Append(P(cy - ry)).Append(' ').Append(P(cx)).Append(' ').Append(P(cy - ry)).Append(" c\n")
                     .Append(P(cx + rx * k)).Append(' ').Append(P(cy - ry)).Append(' ').Append(P(cx + rx)).Append(' ').Append(P(cy - ry * k)).Append(' ').Append(P(cx + rx)).Append(' ').Append(P(cy)).Append(" c h\n");
                }
                b.Append(stroke != null && fill != null ? "B\n" : stroke != null ? "S\n" : "f\n");
                break;
            }
            case "Line" or "Ink" or "Polygon" or "PolyLine":
            {
                if (Colour(colour, true) is not { } pen || width <= 0) return null;
                b.Append(pen).Append(P(width)).Append(" w 1 J 1 j\n");
                var paths = new List<double[]>();
                if (subtype == "Line" && _file.GetNumbers(annot, "L") is { Length: 4 } line) paths.Add(line);
                else if (subtype == "Ink" && _file.GetArray(annot, "InkList") is { } ink)
                    foreach (var item in ink.Items.Take(1000))
                        if (_file.ToNumbers(_file.Resolve(item) as PdfArray) is { Length: >= 2 } points) paths.Add(points);
                if (subtype is "Polygon" or "PolyLine" && _file.GetNumbers(annot, "Vertices") is { Length: >= 4 } vertices) paths.Add(vertices);
                if (paths.Count == 0) return null;
                foreach (var points in paths)
                {
                    for (int i = 0; i + 1 < points.Length && i < 20000; i += 2)
                        b.Append(P(points[i])).Append(' ').Append(P(points[i + 1])).Append(i == 0 ? " m\n" : " l\n");
                    if (subtype == "Polygon") b.Append("h\n");
                }
                b.Append("S\n");
                break;
            }
            case "Text":
            {
                // A note icon: a page with a folded corner and three lines, in the annotation's colour.
                var paper = Colour(colour ?? NoteYellow, false) ?? "1 0.85 0.2 rg\n";
                space |= DeviceSpace.Gray;
                double w = Math.Min(x1 - x0, 20), h = Math.Min(y1 - y0, 20);
                double top = y1;
                b.Append(paper).Append("0 G 0.6 w\n")
                 .Append(P(x0 + 1)).Append(' ').Append(P(top - h + 1)).Append(" m ").Append(P(x0 + w - 1)).Append(' ').Append(P(top - h + 1)).Append(" l ")
                 .Append(P(x0 + w - 1)).Append(' ').Append(P(top - 6)).Append(" l ").Append(P(x0 + w - 6)).Append(' ').Append(P(top - 1)).Append(" l ")
                 .Append(P(x0 + 1)).Append(' ').Append(P(top - 1)).Append(" l h B\n");
                for (int i = 1; i <= 3; i++)
                {
                    double ly = top - h * i / 4.5;
                    b.Append(P(x0 + 4)).Append(' ').Append(P(ly)).Append(" m ").Append(P(x0 + w - 5)).Append(' ').Append(P(ly)).Append(" l S\n");
                }
                break;
            }
            default:
                return null;
        }
        var resources = new PdfDict();
        if (multiply)
            resources.Entries["ExtGState"] = NewDict(("M", NewDict(("Type", PdfName.Get("ExtGState")), ("BM", PdfName.Get("Multiply")))));
        _resources.Add(resources);
        UseDevice(space, resources);
        var form = new PdfStream(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = NewArray(Number(x0), Number(y0), Number(x1), Number(y1)),
            ["Resources"] = resources,
            ["Filter"] = PdfName.Get("FlateDecode"),
        }, Deflate(Encoding.ASCII.GetBytes(b.ToString())));
        return _file.AddObject(form);
    }

    // ------------------------------------------------------------------ forms (6.4)

    /// <summary>Whether the form asks viewers to draw its fields, which PDF/A forbids, so they must be drawn before converting.</summary>
    public static bool NeedsAppearances(PdfFile file)
        => file.GetDict(file.Catalog, "AcroForm") is { } acroForm && file.GetBool(acroForm, "NeedAppearances", false);

    private void Forms(PdfDict catalog)
    {
        if (_file.GetDict(catalog, "AcroForm") is not { } acroForm) return;
        if (acroForm.ContainsKey("XFA"))
        {
            Found("Removed the XFA form, which PDF/A forbids; the form's fields stay.", "The document has an XFA form, which PDF/A forbids.");
            if (_fix) acroForm.Entries.Remove("XFA");
        }
        if (_file.GetBool(acroForm, "NeedAppearances", false))
        {
            Found("Turned off the request that viewers draw the form's fields, which PDF/A forbids; every field has its own appearance.",
                "The form asks viewers to draw its fields (NeedAppearances), which PDF/A forbids.");
            if (_fix) acroForm.Entries.Remove("NeedAppearances");
        }
    }

    // ------------------------------------------------------------------ optional content (6.9)

    private void OptionalContent(PdfDict catalog)
    {
        if (_file.GetDict(catalog, "OCProperties") is not { } properties) return;
        var groups = _file.GetArray(properties, "OCGs")?.Items.Where(g => _file.Resolve(g) is PdfDict).ToList() ?? new List<PdfObject>();
        var configs = new List<PdfDict>();
        if (_file.GetDict(properties, "D") is { } d) configs.Add(d);
        if (_file.GetArray(properties, "Configs") is { } list)
            foreach (var item in list.Items)
                if (_file.Resolve(item) is PdfDict c && !configs.Contains(c)) configs.Add(c);
        var names = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (var config in configs)
        {
            index++;
            var name = (_file.Resolve(config["Name"]) as PdfString)?.Text;
            if (string.IsNullOrEmpty(name) || !names.Add(name))
            {
                Found("Named {0} optional content configuration{s} that had no name or a duplicate one, as PDF/A requires.",
                    "{0} optional content configuration{s} have no name or a duplicate one, which PDF/A forbids.");
                if (_fix)
                {
                    var unique = index == 1 ? "Default" : "Configuration " + index.ToString(CultureInfo.InvariantCulture);
                    while (!names.Add(unique)) unique += "+";
                    config.Entries["Name"] = new PdfString(Encoding.ASCII.GetBytes(unique));
                }
            }
            if (config.ContainsKey("AS"))
            {
                Found("Removed the automatic layer changes of {0} optional content configuration{s}, which PDF/A forbids.",
                    "{0} optional content configuration{s} change layers automatically (AS), which PDF/A forbids.");
                if (_fix) config.Entries.Remove("AS");
            }
            var order = _file.GetArray(config, "Order");
            var listed = new HashSet<int>();
            var listedDirect = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
            if (order != null) Listed(order, listed, listedDirect, 0);
            var missing = groups.Where(g => g is PdfRef r ? !listed.Contains(r.Num) : !listedDirect.Contains(g)).ToList();
            if (missing.Count == 0) continue;
            Found("Listed every layer in the layer order of {0} optional content configuration{s}, as PDF/A requires.",
                "{0} optional content configuration{s} leave layers out of their order, which PDF/A forbids.");
            if (!_fix) continue;
            if (order == null)
            {
                order = new PdfArray();
                config.Entries["Order"] = order;
            }
            order.Items.AddRange(missing);
        }
    }

    private void Listed(PdfArray order, HashSet<int> listed, HashSet<PdfObject> direct, int depth)
    {
        if (depth > MaxDepth) return;
        foreach (var item in order.Items)
        {
            if (item is PdfRef r && listed.Add(r.Num) && _file.Resolve(r) is PdfArray nested) Listed(nested, listed, direct, depth + 1);
            else if (item is PdfArray inner) Listed(inner, listed, direct, depth + 1);
            else if (item is PdfDict) direct.Add(item);
        }
    }

    // ------------------------------------------------------------------ attachments (6.8)

    /// <summary>Embedded PDF files that PDF/A-2 keeps only once they are PDF/A-1 or PDF/A-2 themselves, converted after the walk.</summary>
    private readonly List<(PdfDict Spec, PdfStream File, byte[] Data, string Name)> _pendingAttachments = new();

    /// <summary>The most attachments converted to PDF/A for one document; the rest are removed.</summary>
    private const int MaxConvertedAttachments = 64;

    private void Attachments(PdfDict catalog)
    {
        var specs = new List<PdfDict>();
        VisitDicts((dict, _) =>
        {
            if (_file.GetDict(dict, "EF") is { } ef && (_file.GetStream(ef, "F") ?? _file.GetStream(ef, "UF")) != null) specs.Add(dict);
        });
        if (specs.Count == 0) return;
        if (_part == 3 && _fix) IndirectSpecs(specs);
        var associated = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        VisitDicts((dict, _) =>
        {
            if (_file.GetArray(dict, "AF") is { } af)
                foreach (var item in af.Items)
                    if (_file.Resolve(item) is PdfDict spec) associated.Add(spec);
        });
        var removed = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        int index = 0;
        foreach (var spec in specs)
        {
            index++;
            var ef = _file.GetDict(spec, "EF")!;
            var stream = (_file.GetStream(ef, "F") ?? _file.GetStream(ef, "UF"))!;
            var fileName = (_file.Resolve(spec["UF"]) as PdfString)?.Text ?? (_file.Resolve(spec["F"]) as PdfString)?.Text;
            if (spec["F"] == null || spec["UF"] == null)
            {
                Found("Gave {0} attachment{s} both file name entries (F and UF), as PDF/A requires.", "{0} attachment{s} lack the F or UF file name, which PDF/A requires.");
                if (_fix)
                {
                    var name = fileName ?? "attachment" + index.ToString(CultureInfo.InvariantCulture);
                    spec.Entries.TryAdd("F", new PdfString(Encoding.ASCII.GetBytes(new string(name.Select(c => c is >= ' ' and < '\u007f' ? c : '_').ToArray()))));
                    spec.Entries.TryAdd("UF", Signing.SignatureWriter.Text(name));
                }
            }
            if (_part == 3)
            {
                Associated(catalog, spec, stream, fileName, associated);
                continue;
            }
            // PDF/A-2 keeps only PDF/A-1 and PDF/A-2 files, so other PDFs are converted and anything else goes.
            byte[]? data = null;
            try
            {
                data = EmbeddedFiles.Decode(_file, stream);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or PdfFormatException or IndexOutOfRangeException or ArgumentException)
            {
                SwallowedErrors.Record(ex);
            }
            bool isPdf = data != null && data.AsSpan(0, Math.Min(data.Length, 1024)).IndexOf("%PDF-"u8) >= 0;
            if (!_fix)
            {
                if (!isPdf || PdfAPartOf(data!) is not (1 or 2)) Problem("{0} attachment{s} are not PDF/A-1 or PDF/A-2 files, which PDF/A-2 requires.");
                continue;
            }
            if (isPdf && PdfAPartOf(data!) is 1 or 2) continue;
            if (isPdf && _depth < PdfToPdfA.MaxAttachmentDepth && _pendingAttachments.Count < MaxConvertedAttachments)
            {
                _pendingAttachments.Add((spec, stream, data!, Printable(fileName ?? "attachment")));
                continue;
            }
            _report.Add("Removed {0} attachment{s} that PDF/A-2 cannot keep, since only PDF/A files may be attached; PDF/A-3 keeps any file{names}.",
                Printable(fileName ?? "attachment"));
            removed.Add(spec);
        }
        RemoveAttachments(removed);
    }

    /// <summary>The PDF/A part an attached PDF claims, from its metadata, or 0.</summary>
    private static int PdfAPartOf(byte[] data)
    {
        try
        {
            using var _ = PdfFile.BeginRender(PdfFile.CurrentCancellation, PdfFile.MaxDecodedPerPage);
            return EmbeddedFiles.PdfAPart(PdfFile.Open(data, null));
        }
        catch (Exception ex) when (ex is PdfFormatException or InvalidDataException or NotSupportedException or PdfPasswordException or IndexOutOfRangeException or ArgumentException)
        {
            SwallowedErrors.Record(ex);
            return 0;
        }
    }

    /// <summary>
    /// Converts the attached PDFs PDF/A-2 needs as PDF/A and puts the results in place; an attachment that does not
    /// convert to a conforming file is removed. Runs after <see cref="Run"/>, outside its decoding scope, because each
    /// conversion opens its own.
    /// </summary>
    public void ConvertAttachments(Func<byte[], PdfAConversionResult?> convert)
    {
        var removed = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        foreach (var (spec, stream, data, name) in _pendingAttachments)
        {
            var result = convert(data);
            if (result is not { IsConformant: true })
            {
                _report.Add("Removed {0} attached PDF{s} that could not be converted to PDF/A, which PDF/A-2 requires of attachments{names}.", name);
                removed.Add(spec);
                continue;
            }
            stream.RawData = Deflate(result.Pdf);
            stream.Entries["Filter"] = PdfName.Get("FlateDecode");
            stream.Entries.Remove("DecodeParms");
            stream.Entries.Remove("DL");
            if (_file.GetDict(stream, "Params") is { } parms)
            {
                parms.Entries["Size"] = PdfNumber.Int(result.Pdf.Length);
                parms.Entries["CheckSum"] = new PdfString(System.Security.Cryptography.MD5.HashData(result.Pdf), hex: true);
            }
            _report.Add("Converted {0} attached PDF{s} to PDF/A-2b, as PDF/A-2 requires of attachments{names}.", name);
        }
        _pendingAttachments.Clear();
        RemoveAttachments(removed);
    }

    /// <summary>
    /// Removes attachments: their entries in the embedded files list, the file attachment annotations that show them,
    /// and the embedded data of their file specifications, which may still be named elsewhere.
    /// </summary>
    private void RemoveAttachments(HashSet<PdfDict> removed)
    {
        if (removed.Count == 0) return;
        var catalog = _file.Catalog!;
        if (_file.GetDict(catalog, "Names") is { } names && _file.GetDict(names, "EmbeddedFiles") != null)
        {
            var kept = EmbeddedFiles.NameTree(_file, catalog).Where(p => _file.Resolve(p.Value) is not PdfDict spec || !removed.Contains(spec)).ToList();
            if (kept.Count == 0) names.Entries.Remove("EmbeddedFiles");
            else
            {
                var flat = new PdfArray();
                foreach (var (key, value) in kept.OrderBy(p => p.Key.Bytes, ByteOrder.Instance))
                {
                    flat.Items.Add(key);
                    flat.Items.Add(value);
                }
                names.Entries["EmbeddedFiles"] = NewDict(("Names", flat));
            }
        }
        PageTree.Walk(_file, catalog["Pages"], (node, page) =>
        {
            if (_file.GetArray(page, "Annots") is { } annots)
                annots.Items.RemoveAll(item => _file.Resolve(item) is PdfDict a && _file.GetName(a, "Subtype") == "FileAttachment"
                    && _file.Resolve(a["FS"]) is PdfDict fs && removed.Contains(fs));
            return true;
        });
        foreach (var spec in removed)
        {
            spec.Entries.Remove("EF");
            spec.Entries.Remove("RF");
        }
    }

    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());
    }

    /// <summary>PDF/A-3 keeps any file as an associated file (6.8): with a MIME type, a relationship, and listed in an AF array.</summary>
    private void Associated(PdfDict catalog, PdfDict spec, PdfStream stream, string? fileName, HashSet<PdfDict> associated)
    {
        if (_file.GetName(stream, "Subtype") is not { } mime || !IsMimeType(mime))
        {
            Found("Gave {0} attachment{s} a MIME type, as PDF/A-3 requires.", "{0} attachment{s} have no valid MIME type, which PDF/A-3 requires.");
            if (_fix) stream.Entries["Subtype"] = PdfName.Get(EmbeddedFiles.GuessMimeType(fileName ?? ""));
        }
        if (spec["AFRelationship"] == null)
        {
            Found("Gave {0} attachment{s} the relationship Unspecified, as PDF/A-3 requires.", "{0} attachment{s} have no relationship (AFRelationship), which PDF/A-3 requires.");
            if (_fix) spec.Entries["AFRelationship"] = PdfName.Get("Unspecified");
        }
        if (associated.Contains(spec)) return;
        Found("Listed {0} attachment{s} as associated files of the document, as PDF/A-3 requires.", "{0} attachment{s} are not associated with the document or a part of it, which PDF/A-3 requires.");
        if (!_fix) return;
        if (_file.GetArray(catalog, "AF") is not { } af)
        {
            af = new PdfArray();
            catalog.Entries["AF"] = af;
        }
        // The array must name the specification itself, so a direct one (rare) cannot be listed without moving it.
        if (ObjectNumber(spec) is not { } number)
        {
            _report.Add("{0} attachment{s} could not be listed as associated files, which PDF/A-3 requires.");
            return;
        }
        af.Items.Add(new PdfRef(number, 0));
        associated.Add(spec);
    }

    /// <summary>
    /// Makes file specifications written in place (in a name tree or an annotation) objects of their own, so that the
    /// catalog's AF array can name the same specification, as PDF/A-3 associations need.
    /// </summary>
    private void IndirectSpecs(List<PdfDict> specs)
    {
        var direct = new HashSet<PdfDict>(specs, ReferenceEqualityComparer.Instance);
        var moved = new Dictionary<PdfDict, PdfRef>(ReferenceEqualityComparer.Instance);
        PdfRef Move(PdfDict spec) => moved.TryGetValue(spec, out var r) ? r : moved[spec] = _file.AddObject(spec);
        void InArray(PdfArray array)
        {
            for (int i = 0; i < array.Count; i++)
                if (array.Items[i] is PdfDict spec && direct.Contains(spec)) array.Items[i] = Move(spec);
        }
        VisitDicts((dict, _) =>
        {
            foreach (var key in dict.Entries.Keys.ToList())
            {
                var value = dict.Entries[key];
                if (value is PdfDict spec && direct.Contains(spec)) dict.Entries[key] = Move(spec);
                else if (value is PdfArray array) InArray(array);
            }
        });
        if (moved.Count > 0) _objectNumbers = null;
    }

    private Dictionary<PdfObject, int>? _objectNumbers;

    /// <summary>The object number of an indirect object, or null for a direct one.</summary>
    private int? ObjectNumber(PdfObject o)
    {
        _objectNumbers ??= _file.ObjectNumbers();
        return _objectNumbers.TryGetValue(o, out int n) ? n : null;
    }

    private static bool IsMimeType(string s)
    {
        int slash = s.IndexOf('/');
        if (slash <= 0 || slash == s.Length - 1 || s.IndexOf('/', slash + 1) >= 0) return false;
        foreach (char c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '+' or '.' or '_' or '/')) return false;
        return true;
    }
}
