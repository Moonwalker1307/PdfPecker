using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>Colour (6.2.3, 6.2.4), graphics states (6.2.5, 6.2.10), rendering intents (6.2.6), images and XObjects (6.2.8, 6.2.9).</summary>
internal sealed partial class PdfAConverter
{
    [Flags]
    private enum DeviceSpace : byte { None = 0, Gray = 1, Rgb = 2, Cmyk = 4 }

    // The resource dictionaries met so far, which get the default colour spaces when the document needs them.
    private readonly HashSet<PdfDict> _resources = new(ReferenceEqualityComparer.Instance);

    // The device colour spaces the document uses anywhere, and those it uses where no default colour space replaces them.
    private DeviceSpace _usedSpaces;

    // The output intent's colour space, once known.
    private DeviceSpace _outputSpace;

    // The ICC profiles of colour spaces already checked, with the profile that replaces each one PDF/A cannot use.
    private readonly Dictionary<PdfStream, PdfRef?> _profileReplacements = new(ReferenceEqualityComparer.Instance);

    // The ICC profile streams added to the file, shared by every use.
    private PdfRef? _srgbProfile, _cmykProfile, _grayProfile;

    private static readonly HashSet<string> RenderingIntents = new(StringComparer.Ordinal)
    {
        "RelativeColorimetric", "AbsoluteColorimetric", "Perceptual", "Saturation",
    };

    private static readonly HashSet<string> BlendModes = new(StringComparer.Ordinal)
    {
        "Normal", "Compatible", "Multiply", "Screen", "Overlay", "Darken", "Lighten", "ColorDodge", "ColorBurn", "HardLight",
        "SoftLight", "Difference", "Exclusion", "Hue", "Saturation", "Color", "Luminosity",
    };

    // ------------------------------------------------------------------ resources, graphics states, XObjects

    private void Resources(PdfDict resources)
    {
        if (!_resources.Add(resources)) return;
        if (_file.GetDict(resources, "ExtGState") is { } states)
            foreach (var value in states.Entries.Values)
                if (_file.Resolve(value) is PdfDict gs) ExtGState(gs);
        if (_file.GetDict(resources, "XObject") is { } xobjects)
        {
            foreach (var key in xobjects.Entries.Keys.ToList())
            {
                if (_file.Resolve(xobjects[key]) is not PdfStream x || _file.GetName(x, "Subtype") != "PS") continue;
                Found("Removed {0} PostScript XObject{s}, which PDF/A forbids; viewers do not draw them.", "The document has {0} PostScript XObject{s}, which PDF/A forbids.");
                if (_fix) xobjects.Entries.Remove(key);
            }
        }
    }

    /// <summary>Transfer functions, halftones (6.2.5), blend modes (6.2.10), rendering intents (6.2.6) and overprint mode (6.2.4.2).</summary>
    private void ExtGState(PdfDict gs)
    {
        foreach (var key in new[] { "TR", "TR2", "HTP", "HTO" })
        {
            if (!gs.ContainsKey(key) || key == "TR2" && _file.GetName(gs, "TR2") == "Default" || key == "HTO") continue;
            Found("Removed {0} transfer function{s} and halftone setting{s} from graphics states; PDF/A forbids them, and only printers use them.",
                "{0} graphics state{s} set transfer functions or halftone phases, which PDF/A forbids.");
            if (_fix) gs.Entries.Remove(key);
        }
        if (_file.Resolve(gs["HT"]) is { } ht && !IsAllowedHalftone(ht))
        {
            Found("Removed {0} halftone{s} PDF/A does not allow from graphics states; only printers use them.", "{0} graphics state{s} set halftones that PDF/A does not allow.");
            if (_fix) gs.Entries.Remove("HT");
        }
        var bm = _file.Resolve(gs["BM"]);
        string? blend = bm switch
        {
            PdfName n => n.Value,
            PdfArray a => a.Items.Select(i => (_file.Resolve(i) as PdfName)?.Value).FirstOrDefault(v => v != null && BlendModes.Contains(v)) ?? "",
            _ => null,
        };
        if (blend != null && (bm is PdfArray || !BlendModes.Contains(blend)))
        {
            // An array lists blend modes in order of preference; the first one PDF/A knows is the one viewers use.
            bool known = BlendModes.Contains(blend);
            if (!known) Found("Replaced {0} blend mode{s} PDF/A does not know with Normal{names}.", "{0} graphics state{s} use blend modes PDF/A does not know{names}.", bm is PdfName n ? n.Value : null);
            if (_fix) gs.Entries["BM"] = PdfName.Get(known ? blend : "Normal");
        }
        RenderingIntent(gs, "RI");
        // Overprint mode 1 changes how zero CMYK components overprint, which PDF/A forbids with ICC-based CMYK.
        if (_file.GetInt(gs, "OPM", 0) == 1 && (_file.GetBool(gs, "OP", false) || _file.GetBool(gs, "op", false)))
        {
            Found("Set overprint mode 0 in {0} graphics state{s}, as PDF/A needs with device-independent CMYK.", "{0} graphics state{s} use overprint mode 1, which PDF/A forbids with ICC-based CMYK.");
            if (_fix) gs.Entries["OPM"] = PdfNumber.Int(0);
        }
    }

    private bool IsAllowedHalftone(PdfObject ht)
    {
        if (ht is PdfName { Value: "Default" }) return true;
        if (ht is not PdfDict d) return false;
        int type = _file.GetInt(d, "HalftoneType", 0);
        if (type is not (1 or 5) || d.ContainsKey("HalftoneName")) return false;
        if (type == 1) return !d.ContainsKey("TransferFunction");
        // A type 5 halftone may give the non-primary colourants transfer functions, and only them.
        foreach (var (key, value) in d.Entries)
        {
            if (_file.Resolve(value) is not PdfDict component) continue;
            bool primary = key is "Default" or "Cyan" or "Magenta" or "Yellow" or "Black";
            if (key != "Default" && primary == component.ContainsKey("TransferFunction")) return false;
        }
        return true;
    }

    private void RenderingIntent(PdfDict dict, string key)
    {
        if (_file.GetName(dict, key) is not { } intent || RenderingIntents.Contains(intent)) return;
        Found("Replaced {0} rendering intent{s} PDF/A does not know with RelativeColorimetric{names}.", "{0} rendering intent{s} are not one PDF/A knows{names}.", intent);
        if (_fix) dict.Entries[key] = PdfName.Get("RelativeColorimetric");
    }

    /// <summary>Image dictionaries (6.2.8).</summary>
    private void Image(PdfStream image)
    {
        foreach (var key in new[] { "Alternates", "OPI" })
        {
            if (!image.ContainsKey(key)) continue;
            Found("Removed {0} alternate image{s} and prepress (OPI) link{s}, which PDF/A forbids; the image itself stays.", "{0} image{s} have alternates or OPI links, which PDF/A forbids.");
            if (_fix) image.Entries.Remove(key);
        }
        if (_file.GetBool(image, "Interpolate", false))
        {
            Found("Turned off interpolation of {0} image{s}, which PDF/A forbids; viewers may show them a little sharper.", "{0} image{s} ask for interpolation, which PDF/A forbids.");
            if (_fix) image.Entries.Remove("Interpolate");
        }
        RenderingIntent(image, "Intent");
        bool mask = _file.GetBool(image, "ImageMask", false);
        int bits = _file.GetInt(image, "BitsPerComponent", 0);
        if (image.ContainsKey("BitsPerComponent") && (mask ? bits != 1 : bits is not (1 or 2 or 4 or 8 or 16)))
            Problem("{0} image{s} have a bit depth PDF/A does not allow.");
    }

    /// <summary>Form XObjects (6.2.9).</summary>
    private void Form(PdfStream form)
    {
        bool postScript = _file.GetName(form, "Subtype2") == "PS";
        if (form.ContainsKey("OPI") || form.ContainsKey("PS") || postScript)
        {
            Found("Removed the PostScript and prepress (OPI) parts of {0} form XObject{s}, which PDF/A forbids; viewers do not use them.",
                "{0} form XObject{s} carry PostScript or OPI links, which PDF/A forbids.");
            if (_fix)
            {
                form.Entries.Remove("OPI");
                form.Entries.Remove("PS");
                if (postScript) form.Entries.Remove("Subtype2");
            }
        }
        if (form.ContainsKey("Ref"))
        {
            Found("Removed the links of {0} reference XObject{s} to other files, which PDF/A forbids; the copy in this file stays.",
                "{0} form XObject{s} refer to pages in other files, which PDF/A forbids.");
            if (_fix) form.Entries.Remove("Ref");
        }
        if (_file.GetDict(form, "Group") is { } group) Group(group);
    }

    // ------------------------------------------------------------------ the output intent (6.2.3)

    /// <summary>
    /// Makes sure the file has one PDF/A output intent with a usable ICC profile, shared by every output intent: the
    /// file's own PDF/A or PDF/X profile when it has a usable one, else sRGB.
    /// </summary>
    private void OutputIntent(PdfDict catalog)
    {
        var intents = _file.GetArray(catalog, "OutputIntents");
        PdfObject? profileRef = null;
        DeviceSpace space = DeviceSpace.None;
        PdfDict? pdfa = null;
        if (intents != null)
        {
            // The PDF/A intent's profile first, then any other's.
            foreach (bool pdfaOnly in new[] { true, false })
            {
                foreach (var item in intents.Items)
                {
                    if (_file.Resolve(item) is not PdfDict intent) continue;
                    bool isPdfa = _file.GetName(intent, "S") == "GTS_PDFA1";
                    if (isPdfa) pdfa ??= intent;
                    if (pdfaOnly != isPdfa || profileRef != null) continue;
                    if (_file.Resolve(intent["DestOutputProfile"]) is PdfStream profile && ProfileSpace(profile, output: true) is { } s and not DeviceSpace.None)
                    {
                        profileRef = intent["DestOutputProfile"];
                        space = s;
                    }
                }
            }
        }
        if (profileRef == null)
        {
            if (!_fix)
            {
                Problem("The document has no PDF/A output intent with a usable ICC profile.");
                _outputSpace = DeviceSpace.None;
                return;
            }
            profileRef = SrgbProfile();
            space = DeviceSpace.Rgb;
            _report.Add(pdfa == null
                ? "Added an sRGB output intent, which PDF/A needs to make the document's colours device independent."
                : "Replaced the output intent's ICC profile, which PDF/A cannot use, with sRGB.");
        }
        _outputSpace = space;

        bool shared = intents == null || intents.Items.All(i => _file.Resolve(i) is not PdfDict d || d["DestOutputProfile"] == null
            || SameObject(d["DestOutputProfile"], profileRef));
        bool refs = intents != null && intents.Items.Any(i => _file.Resolve(i) is PdfDict d && d.ContainsKey("DestOutputProfileRef"));
        if (!_fix)
        {
            if (pdfa == null) Problem("The document has no PDF/A output intent.");
            if (!shared) Problem("The document's output intents use different ICC profiles, which PDF/A forbids.");
            if (refs) Problem("An output intent refers to an external ICC profile, which PDF/A forbids.");
            return;
        }
        if (pdfa == null)
        {
            pdfa = NewDict(("Type", PdfName.Get("OutputIntent")), ("S", PdfName.Get("GTS_PDFA1")),
                ("OutputConditionIdentifier", new PdfString(System.Text.Encoding.ASCII.GetBytes(PdfA.OutputCondition))),
                ("Info", new PdfString(System.Text.Encoding.ASCII.GetBytes(PdfA.OutputCondition))));
            if (intents == null) catalog.Entries["OutputIntents"] = intents = new PdfArray();
            else if (catalog["OutputIntents"] is not PdfRef) catalog.Entries["OutputIntents"] = intents;
            intents.Items.Insert(0, pdfa);
        }
        if (!shared) _report.Add("Made every output intent use the same ICC profile, as PDF/A needs.");
        foreach (var item in intents!.Items)
        {
            if (_file.Resolve(item) is not PdfDict intent) continue;
            if (intent.Entries.Remove("DestOutputProfileRef")) _report.Add("Removed an output intent's reference to an external ICC profile, which PDF/A forbids.");
            if (ReferenceEquals(intent, pdfa) || intent.ContainsKey("DestOutputProfile")) intent.Entries["DestOutputProfile"] = profileRef;
        }
    }

    private static bool SameObject(PdfObject? a, PdfObject? b)
        => ReferenceEquals(a, b) || a is PdfRef ra && b is PdfRef rb && ra.Num == rb.Num;

    private PdfRef SrgbProfile() => _srgbProfile ??= AddProfile(PdfA.SrgbProfile, 3);

    private PdfRef CmykProfile() => _cmykProfile ??= AddProfile(PdfA.CmykProfile, 4);

    private PdfRef GrayProfile() => _grayProfile ??= AddProfile(PdfA.GrayProfile, 1);

    private PdfRef AddProfile(byte[] profile, int components)
        => _file.AddObject(new PdfStream(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
        {
            ["N"] = PdfNumber.Int(components),
            ["Filter"] = PdfName.Get("FlateDecode"),
        }, Deflate(profile)));

    /// <summary>
    /// The colour space of an ICC profile PDF/A accepts (6.2.3 for output intents, 6.2.4.2 for ICC-based colour spaces),
    /// or null when PDF/A does not accept it. Lab profiles are accepted for colour spaces and give None.
    /// </summary>
    private DeviceSpace? ProfileSpace(PdfStream profile, bool output)
    {
        byte[] data;
        try
        {
            data = _file.DecodeStream(profile);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or PdfFormatException or IndexOutOfRangeException or ArgumentException)
        {
            SwallowedErrors.Record(ex);
            return null;
        }
        if (data.Length < 132 || data[36] != 'a' || data[37] != 'c' || data[38] != 's' || data[39] != 'p' || data[8] >= 5) return null;
        var deviceClass = System.Text.Encoding.ASCII.GetString(data, 12, 4);
        var colorSpace = System.Text.Encoding.ASCII.GetString(data, 16, 4);
        bool classOk = deviceClass is "prtr" or "mntr" || !output && deviceClass is "scnr" or "spac";
        if (!classOk) return null;
        return colorSpace switch
        {
            "RGB " => DeviceSpace.Rgb,
            "CMYK" => DeviceSpace.Cmyk,
            "GRAY" => DeviceSpace.Gray,
            "Lab " when !output => DeviceSpace.None,
            _ => null,
        };
    }

    // ------------------------------------------------------------------ device colour (6.2.4)

    /// <summary>The device colour spaces PDF/A allows only through a default colour space, given the output intent.</summary>
    private DeviceSpace Conflicting => _outputSpace switch
    {
        DeviceSpace.Rgb => DeviceSpace.Cmyk,
        DeviceSpace.Cmyk => DeviceSpace.Rgb,
        DeviceSpace.Gray => DeviceSpace.Rgb | DeviceSpace.Cmyk,
        // Without a usable output intent every device colour space needs a default one; grey too.
        _ => DeviceSpace.Rgb | DeviceSpace.Cmyk | DeviceSpace.Gray,
    };

    /// <summary>The device colour spaces a colour space uses itself or through its base or alternate space.</summary>
    private DeviceSpace Devices(PdfObject? cs, PdfDict? resources, int depth = 0)
    {
        cs = _file.Resolve(cs);
        if (depth > 8 || cs == null) return DeviceSpace.None;
        if (cs is PdfName n)
        {
            switch (n.Value)
            {
                case "DeviceGray" or "G": return DeviceSpace.Gray;
                case "DeviceRGB" or "RGB": return DeviceSpace.Rgb;
                case "DeviceCMYK" or "CMYK": return DeviceSpace.Cmyk;
                case "Pattern": return DeviceSpace.None;
            }
            return resources != null && _file.GetDict(resources, "ColorSpace") is { } spaces && spaces[n.Value] is { } named
                ? Devices(named, null, depth + 1) : DeviceSpace.None;
        }
        if (cs is not PdfArray a || a.Count == 0) return DeviceSpace.None;
        switch ((_file.Resolve(a[0]) as PdfName)?.Value)
        {
            case "Indexed" or "I" when a.Count > 1:
            case "Pattern" when a.Count > 1:
                return Devices(a[1], resources, depth + 1);
            case "Separation" when a.Count > 2:
                return Devices(a[2], resources, depth + 1);
            case "DeviceN" when a.Count > 2:
                var spaces = Devices(a[2], resources, depth + 1);
                if (a.Count > 4 && _file.Resolve(a[4]) is PdfDict attrs && _file.GetDict(attrs, "Process") is { } process)
                    spaces |= Devices(process["ColorSpace"], resources, depth + 1);
                return spaces;
            default:
                return DeviceSpace.None;
        }
    }

    /// <summary>
    /// Records a use of device colour where it is drawn: allowed by the output intent, replaced by a default colour space
    /// of the resources in force, or (when checking) a problem.
    /// </summary>
    private void UseDevice(DeviceSpace spaces, PdfDict? resources)
    {
        _usedSpaces |= spaces;
        if (_fix) return;
        foreach (var space in new[] { DeviceSpace.Gray, DeviceSpace.Rgb, DeviceSpace.Cmyk })
        {
            if ((spaces & space) == 0 || (Conflicting & space) == 0) continue;
            if (resources != null && _file.GetDict(resources, "ColorSpace") is { } cs && cs.ContainsKey(DefaultName(space))) continue;
            Problem(space switch
            {
                DeviceSpace.Rgb => "The document uses RGB colour that no ICC profile describes, which PDF/A forbids with this output intent.",
                DeviceSpace.Cmyk => "The document uses CMYK colour that no ICC profile describes, which PDF/A forbids with this output intent.",
                _ => "The document uses grey that no ICC profile describes, which PDF/A forbids without an output intent.",
            });
        }
    }

    private static string DefaultName(DeviceSpace space) => space switch
    {
        DeviceSpace.Rgb => "DefaultRGB",
        DeviceSpace.Cmyk => "DefaultCMYK",
        _ => "DefaultGray",
    };

    /// <summary>A transparency group's blending colour space, which no default colour space replaces.</summary>
    private void Group(PdfDict group)
    {
        var spaces = Devices(group["CS"], null) & Conflicting;
        if (spaces == DeviceSpace.None) return;
        if (!_fix)
        {
            Problem("{0} transparency group{s} blend in a device colour space the output intent does not cover.");
            return;
        }
        // Replaced when the colours are made device independent below, once every use is known.
        _usedSpaces |= spaces;
    }

    /// <summary>
    /// Makes the device colours the output intent does not cover device independent: every resource dictionary gets a
    /// default colour space with an ICC profile (sRGB for RGB, a CMYK profile matching the plain conversion viewers do),
    /// and the colour spaces of images, shadings and transparency groups name that profile instead of the device.
    /// </summary>
    private void Colours()
    {
        if (!_fix) return;
        var needed = _usedSpaces & Conflicting;
        if (needed == DeviceSpace.None) return;
        var replacements = new Dictionary<DeviceSpace, PdfArray>();
        foreach (var space in new[] { DeviceSpace.Gray, DeviceSpace.Rgb, DeviceSpace.Cmyk })
        {
            if ((needed & space) == 0) continue;
            var profile = space switch
            {
                DeviceSpace.Rgb => SrgbProfile(),
                DeviceSpace.Cmyk => CmykProfile(),
                _ => GrayProfile(),
            };
            replacements[space] = NewArray(PdfName.Get("ICCBased"), profile);
            _report.Add(space switch
            {
                DeviceSpace.Rgb => "Made the RGB colours device independent with an sRGB profile, as PDF/A needs with a CMYK or grey output intent.",
                DeviceSpace.Cmyk => "Made the CMYK colours device independent with an ICC profile that keeps the colours viewers showed, as PDF/A needs with an RGB output intent.",
                _ => "Made the grey colours device independent with an ICC profile, as PDF/A needs without an output intent.",
            });
        }
        foreach (var resources in _resources)
        {
            var spaces = _file.GetDict(resources, "ColorSpace");
            if (spaces == null)
            {
                spaces = new PdfDict();
                resources.Entries["ColorSpace"] = spaces;
            }
            foreach (var (space, icc) in replacements) spaces.Entries[DefaultName(space)] = icc;
        }
        // Colour spaces named in dictionaries: no default colour space replaces a group's, and naming the profile directly
        // keeps images and shadings right wherever they are drawn.
        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        VisitDicts((dict, _) =>
        {
            switch (_file.GetName(dict, "Subtype"))
            {
                case "Image":
                    ReplaceIn(dict, "ColorSpace", replacements, seen);
                    break;
            }
            if (dict.ContainsKey("ShadingType")) ReplaceIn(dict, "ColorSpace", replacements, seen);
            if (_file.GetName(dict, "S") == "Transparency") ReplaceIn(dict, "CS", replacements, seen);
            if (_file.GetDict(dict, "Resources") is { } resources && _file.GetDict(resources, "ColorSpace") is { } spaces)
                foreach (var key in spaces.Entries.Keys.ToList())
                    if (!key.StartsWith("Default", StringComparison.Ordinal)) ReplaceIn(spaces, key, replacements, seen);
        });
    }

    /// <summary>Replaces the device colour spaces of <paramref name="dict"/>[<paramref name="key"/>], arrays included, with ICC profiles.</summary>
    private void ReplaceIn(PdfDict dict, string key, Dictionary<DeviceSpace, PdfArray> replacements, HashSet<PdfObject> seen)
    {
        if (dict[key] is not { } value) return;
        if (Replacement(value, replacements) is { } icc) dict.Entries[key] = icc;
        else if (_file.Resolve(value) is PdfArray a && seen.Add(a)) ReplaceInArray(a, replacements, seen, 0);
    }

    private void ReplaceInArray(PdfArray a, Dictionary<DeviceSpace, PdfArray> replacements, HashSet<PdfObject> seen, int depth)
    {
        if (depth > 8 || a.Count < 2) return;
        int index = (_file.Resolve(a[0]) as PdfName)?.Value switch
        {
            "Indexed" or "I" or "Pattern" => 1,
            "Separation" or "DeviceN" => 2,
            _ => -1,
        };
        if (index < 0 || index >= a.Count) return;
        if (Replacement(a[index], replacements) is { } icc) a.Items[index] = icc;
        else if (_file.Resolve(a[index]) is PdfArray inner && seen.Add(inner)) ReplaceInArray(inner, replacements, seen, depth + 1);
        if (a.Count > 4 && _file.Resolve(a[4]) is PdfDict attrs && _file.GetDict(attrs, "Process") is { } process)
            ReplaceIn(process, "ColorSpace", replacements, seen);
    }

    private PdfArray? Replacement(PdfObject value, Dictionary<DeviceSpace, PdfArray> replacements)
    {
        if (_file.Resolve(value) is not PdfName n) return null;
        var space = n.Value switch
        {
            "DeviceGray" or "G" => DeviceSpace.Gray,
            "DeviceRGB" or "RGB" => DeviceSpace.Rgb,
            "DeviceCMYK" or "CMYK" => DeviceSpace.Cmyk,
            _ => DeviceSpace.None,
        };
        return replacements.TryGetValue(space, out var icc) ? icc : null;
    }

    /// <summary>A colour space array: its ICC profile, or those of its base and alternate spaces.</summary>
    private void ColourSpaceArray(PdfArray cs, int depth)
    {
        if (depth > 8 || cs.Count < 2 || _file.Resolve(cs[0]) is not PdfName { Value: var family }) return;
        int inner = family switch
        {
            "ICCBased" => -1,
            "Indexed" or "I" or "Pattern" => 1,
            "Separation" or "DeviceN" => 2,
            _ => 0,
        };
        if (inner < 0) IccBased(cs);
        else if (inner > 0 && inner < cs.Count && _file.Resolve(cs[inner]) is PdfArray nested) ColourSpaceArray(nested, depth + 1);
        if (family == "DeviceN") Colorants(cs);
    }

    // Colorants that are not spot colours, and need no entry in a DeviceN space's Colorants dictionary.
    private static readonly HashSet<string> ProcessColorants = new(StringComparer.Ordinal) { "Cyan", "Magenta", "Yellow", "Black", "None" };

    /// <summary>The most colorants a DeviceN space may have in PDF/A (6.1.13).</summary>
    private const int MaxDeviceNColorants = 32;

    /// <summary>The most samples of the tint transforms written for a DeviceN space's spot colours.</summary>
    private const int ColorantSamples = 33;

    /// <summary>
    /// Every spot colour of a DeviceN space needs a Separation space of its own in the attributes' Colorants dictionary
    /// (6.2.4.4). One that lacks it gets one whose tint transform samples the DeviceN transform with the other
    /// colorants at zero, which is how the colour looks on its own.
    /// </summary>
    private void Colorants(PdfArray cs)
    {
        if (cs.Count < 4 || _file.Resolve(cs[1]) is not PdfArray names) return;
        if (names.Count > MaxDeviceNColorants)
        {
            Problem("{0} DeviceN colour space{s} have more than 32 colorants, the PDF/A limit.");
            return;
        }
        var attributes = cs.Count > 4 ? _file.Resolve(cs[4]) as PdfDict : null;
        var colorants = _file.GetDict(attributes, "Colorants");
        var process = new HashSet<string>(StringComparer.Ordinal);
        if (_file.GetArray(_file.GetDict(attributes, "Process"), "Components") is { } components)
            foreach (var c in components.Items)
                if (_file.Resolve(c) is PdfName n) process.Add(n.Value);
        var missing = new List<(int Index, string Name)>();
        for (int i = 0; i < names.Count; i++)
            if (_file.Resolve(names[i]) is PdfName n && !ProcessColorants.Contains(n.Value) && !process.Contains(n.Value) && colorants?[n.Value] == null)
                missing.Add((i, n.Value));
        if (missing.Count == 0) return;
        int outputs = Components(cs[2]);
        var transform = Rendering.Content.PdfFunction.Create(_file, cs[3]);
        if (!_fix || outputs == 0 || transform == null)
        {
            Problem("{0} DeviceN colour space{s} lack the Colorants entries PDF/A requires for their spot colours{names}.", Printable(missing[0].Name));
            return;
        }
        if (attributes == null)
        {
            attributes = new PdfDict();
            if (cs.Count > 4) cs.Items[4] = attributes;
            else cs.Items.Add(attributes);
        }
        if (colorants == null)
        {
            colorants = new PdfDict();
            attributes.Entries["Colorants"] = colorants;
        }
        var input = new double[names.Count];
        var output = new double[Math.Max(outputs, 8)];
        foreach (var (index, name) in missing)
        {
            var samples = new byte[ColorantSamples * outputs * 2];
            for (int k = 0; k < ColorantSamples; k++)
            {
                Array.Clear(input);
                input[index] = (double)k / (ColorantSamples - 1);
                Array.Clear(output);
                transform.Eval(input, output);
                for (int o = 0; o < outputs; o++)
                {
                    int v = (int)Math.Round(Math.Clamp(double.IsFinite(output[o]) ? output[o] : 0, 0, 1) * 65535);
                    samples[(k * outputs + o) * 2] = (byte)(v >> 8);
                    samples[(k * outputs + o) * 2 + 1] = (byte)v;
                }
            }
            var range = new PdfArray();
            for (int o = 0; o < outputs; o++)
            {
                range.Items.Add(PdfNumber.Int(0));
                range.Items.Add(PdfNumber.Int(1));
            }
            var function = _file.AddObject(new PdfStream(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
            {
                ["FunctionType"] = PdfNumber.Int(0),
                ["Domain"] = NewArray(PdfNumber.Int(0), PdfNumber.Int(1)),
                ["Range"] = range,
                ["Size"] = NewArray(PdfNumber.Int(ColorantSamples)),
                ["BitsPerSample"] = PdfNumber.Int(16),
                ["Filter"] = PdfName.Get("FlateDecode"),
            }, Deflate(samples)));
            colorants.Entries[name] = NewArray(PdfName.Get("Separation"), PdfName.Get(name), cs[2], function);
        }
        _report.Add("Described the spot colours of {0} DeviceN colour space{s} on their own, as PDF/A requires{names}.", Printable(missing[0].Name));
    }

    /// <summary>The number of components of a device, calibrated or ICC-based colour space with values from 0 to 1, or 0.</summary>
    private int Components(PdfObject? cs)
    {
        switch (_file.Resolve(cs))
        {
            case PdfName { Value: "DeviceGray" or "G" or "CalGray" }:
                return 1;
            case PdfName { Value: "DeviceRGB" or "RGB" }:
                return 3;
            case PdfName { Value: "DeviceCMYK" or "CMYK" }:
                return 4;
            case PdfArray { Count: >= 2 } a when _file.Resolve(a[0]) is PdfName family:
                return family.Value switch
                {
                    "CalGray" => 1,
                    "CalRGB" => 3,
                    "ICCBased" when _file.Resolve(a[1]) is PdfStream profile => _file.GetInt(profile, "N", 0) is var n and (1 or 3 or 4) ? n : 0,
                    _ => 0,
                };
            default:
                return 0;
        }
    }

    /// <summary>ICC-based colour spaces whose profile PDF/A cannot use (6.2.4.2) get a profile it can, by component count.</summary>
    private void IccBased(PdfArray cs)
    {
        if (cs.Count < 2 || _file.Resolve(cs[0]) is not PdfName { Value: "ICCBased" } || _file.Resolve(cs[1]) is not PdfStream profile) return;
        if (!_profileReplacements.TryGetValue(profile, out var replacement))
        {
            if (ProfileSpace(profile, output: false) == null)
            {
                int n = _file.GetInt(profile, "N", 0);
                if (!_fix || n is not (1 or 3 or 4)) Problem("{0} ICC-based colour space{s} use profiles PDF/A cannot use.");
                else
                {
                    _report.Add("Replaced {0} ICC profile{s} PDF/A cannot use with standard ones (sRGB, grey or CMYK).");
                    replacement = n switch
                    {
                        1 => GrayProfile(),
                        3 => SrgbProfile(),
                        _ => CmykProfile(),
                    };
                }
            }
            _profileReplacements[profile] = replacement;
        }
        if (replacement != null) cs.Items[1] = replacement;
    }
}
