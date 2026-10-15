using System.Globalization;
using PdfPecker.Fonts;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Pdf;

/// <summary>Fillable form fields (AcroForm): one widget annotation per control, with its appearance drawn from its layout.</summary>
internal sealed partial class PdfDocumentWriter
{
    // Field flags (ISO 32000-1, tables 221, 226, 228 and 230).
    private const int FfReadOnly = 1, FfRequired = 1 << 1, FfMultiline = 1 << 12, FfPassword = 1 << 13, FfNoToggleToOff = 1 << 14,
        FfRadio = 1 << 15, FfCombo = 1 << 17, FfMultiSelect = 1 << 21, FfDoNotSpellCheck = 1 << 22;

    private static readonly string[] DaFonts = { "Helv", "Cour", "TiRo" };
    private static readonly string[] DaBaseFonts = { "Helvetica", "Courier", "Times-Roman" };

    private readonly Dictionary<FieldBlock, int> _fieldIds = new();
    private readonly Dictionary<RadioGroup, (int Id, List<int> Kids)> _radioGroups = new();
    private readonly List<int> _topFields = new();
    private readonly int[] _daFontIds = new int[DaFonts.Length];

    /// <summary>Finds the fields of each page and numbers their objects: a widget each, and a parent for each radio group.</summary>
    private List<FieldBlock>[] AllocateFields(List<List<Placed>> pages, int pageCount, ref int next)
    {
        var result = new List<FieldBlock>[pageCount];
        for (int p = 0; p < pageCount; p++)
        {
            var list = result[p] = new List<FieldBlock>();
            if (p < pages.Count) CollectFields(pages[p], list);
            foreach (var fb in list)
            {
                int id = next++;
                _fieldIds[fb] = id;
                if (fb.Field.Field.Group is { } group)
                {
                    if (!_radioGroups.TryGetValue(group, out var g))
                    {
                        _radioGroups[group] = g = (next++, new List<int>());
                        _topFields.Add(g.Id);
                    }
                    g.Kids.Add(id);
                }
                else _topFields.Add(id);
                if (fb.Field.Kind is not (ControlKind.CheckBox or ControlKind.Radio))
                {
                    int font = DaFont(fb.Field.Font);
                    if (_daFontIds[font] == 0) _daFontIds[font] = next++;
                }
            }
        }
        return result;
    }

    /// <summary>The standard font a viewer redraws a field's text with while it is edited: the closest to its own font.</summary>
    private static int DaFont(TextFont font)
    {
        if (font is StandardFont sf)
        {
            if (sf.BaseFont.StartsWith("Courier", StringComparison.Ordinal)) return 1;
            if (sf.BaseFont.StartsWith("Times", StringComparison.Ordinal)) return 2;
        }
        return 0;
    }

    /// <summary>The catalog's AcroForm entry, when the document has fields.</summary>
    private void WriteAcroForm()
    {
        if (_topFields.Count == 0) return;
        _out.Ascii(" /AcroForm << /Fields [");
        foreach (int id in _topFields) _out.Int(id).Ascii(" 0 R ");
        _out.Ascii("] /DR << /Font <<");
        for (int i = 0; i < DaFonts.Length; i++)
            if (_daFontIds[i] != 0) _out.Ascii(" /").Ascii(DaFonts[i]).Sp().Int(_daFontIds[i]).Ascii(" 0 R");
        _out.Ascii(" >> >> /DA ");
        WriteTextString(_out, "/Helv 0 Tf 0 g");
        _out.Ascii(" >>");
    }

    /// <summary>Writes the widgets, their appearance streams, the radio groups and the fonts of the default appearances.</summary>
    private void WriteFields(List<FieldBlock>[] pageFields, int[] pageIds, Dictionary<PdfImage, int> imageIds,
        Dictionary<int, int> annotKeys, ref int next)
    {
        if (_fieldIds.Count == 0) return;
        // An appearance is not page content: it has no marked content of its own.
        var savedMc = _mc;
        _mc = null;
        for (int p = 0; p < pageFields.Length; p++)
            foreach (var fb in pageFields[p])
            {
                var f = fb.Field;
                var field = f.Field;
                SetPage(p, out _);
                float k = _page.Scale, origin = _page.MarginTop + _page.BandTop;
                float pageTop = PageTop(p);
                float x1 = (_page.MarginLeft + f.X) * k, x2 = x1 + f.W * k;
                float yTop = (_page.Height - origin - (f.Y - pageTop)) * k + _paperDy, yBottom = yTop - f.H * k;
                float w = x2 - x1, h = yTop - yBottom;
                bool button = f.Kind is ControlKind.CheckBox or ControlKind.Radio;

                // The appearance: the field's laid-out content, moved from the page to the widget's own space.
                var ap = new ByteBuffer(512);
                ResetState();
                _yOrigin = origin;
                if (!button) ap.Ascii("/Tx BMC\n");
                ap.Ascii("q ").Ascii(k.ToString("0.######", CultureInfo.InvariantCulture)).Ascii(" 0 0 ")
                    .Ascii(k.ToString("0.######", CultureInfo.InvariantCulture)).Sp().Num(-x1).Sp().Num(_paperDy - yBottom).Ascii(" cm\n");
                foreach (var item in fb.Items)
                    if (item.Op is not LinkOp) WriteOp(ap, item, pageTop, imageIds);
                EndText(ap);
                ap.Ascii("Q\n");
                if (!button) ap.Ascii("EMC\n");
                string bbox = "/Type /XObject /Subtype /Form /BBox [0 0 " + N(w) + " " + N(h) + "]";
                int onId = next++;
                WriteStream(onId, ap.Span, _compress, bbox + " /Resources " + _resourcesId.ToString(CultureInfo.InvariantCulture) + " 0 R");
                int offId = 0;
                if (button)
                {
                    offId = next++;
                    WriteStream(offId, ReadOnlySpan<byte>.Empty, false, bbox);
                }

                Begin(_fieldIds[fb]).Ascii("<< /Type /Annot /Subtype /Widget /Rect [").Num(x1).Sp().Num(yBottom).Sp().Num(x2).Sp().Num(yTop)
                    .Ascii("] /F 4 /P ").Int(pageIds[p]).Ascii(" 0 R");
                if (annotKeys.TryGetValue(_fieldIds[fb], out int key)) _out.Ascii(" /StructParent ").Int(key);
                if (field.Group is { } group)
                {
                    _out.Ascii(" /Parent ").Int(_radioGroups[group].Id).Ascii(" 0 R");
                    if (field.Tooltip != null) { _out.Ascii(" /TU "); WriteTextString(_out, field.Tooltip); }
                }
                else
                {
                    _out.Ascii(" /T ");
                    WriteTextString(_out, field.Name);
                    if (field.Tooltip != null) { _out.Ascii(" /TU "); WriteTextString(_out, field.Tooltip); }
                    int flags = (field.ReadOnly ? FfReadOnly : 0) | (field.Required ? FfRequired : 0);
                    switch (f.Kind)
                    {
                        case ControlKind.CheckBox:
                            _out.Ascii(" /FT /Btn");
                            if (flags != 0) _out.Ascii(" /Ff ").Int(flags);
                            _out.Ascii(" /V ");
                            WriteName(_out, f.Checked ? field.OnState : "Off");
                            break;
                        case ControlKind.Text:
                            flags |= (field.Multiline ? FfMultiline : 0) | (field.Password ? FfPassword | FfDoNotSpellCheck : 0);
                            _out.Ascii(" /FT /Tx");
                            if (flags != 0) _out.Ascii(" /Ff ").Int(flags);
                            if (field.MaxLength > 0) _out.Ascii(" /MaxLen ").Int(field.MaxLength);
                            if (!string.IsNullOrEmpty(field.Value)) { _out.Ascii(" /V "); WriteTextString(_out, field.Value); }
                            break;
                        default: // a drop-down or list box
                        {
                            flags |= f.Kind == ControlKind.Combo ? FfCombo : field.MultiSelect ? FfMultiSelect : 0;
                            _out.Ascii(" /FT /Ch");
                            if (flags != 0) _out.Ascii(" /Ff ").Int(flags);
                            _out.Ascii(" /Opt [");
                            foreach (var (export, text) in field.Options!)
                            {
                                if (export == text) WriteTextString(_out, text);
                                else
                                {
                                    _out.Ascii("[");
                                    WriteTextString(_out, export);
                                    _out.Sp();
                                    WriteTextString(_out, text);
                                    _out.Ascii("]");
                                }
                                _out.Sp();
                            }
                            _out.Ascii("]");
                            var selected = field.Selected!;
                            if (selected.Count == 1) { _out.Ascii(" /V "); WriteTextString(_out, field.Options![selected[0]].Export); }
                            else if (selected.Count > 1)
                            {
                                _out.Ascii(" /V [");
                                foreach (int i in selected) { WriteTextString(_out, field.Options![i].Export); _out.Sp(); }
                                _out.Ascii("]");
                            }
                            if (selected.Count > 0)
                            {
                                _out.Ascii(" /I [");
                                foreach (int i in selected) _out.Int(i).Sp();
                                _out.Ascii("]");
                            }
                            break;
                        }
                    }
                }
                if (button)
                {
                    _out.Ascii(" /AS ");
                    WriteName(_out, f.Checked ? field.OnState : "Off");
                    _out.Ascii(" /AP << /N << ");
                    WriteName(_out, field.OnState);
                    _out.Sp().Int(onId).Ascii(" 0 R /Off ").Int(offId).Ascii(" 0 R >> >>");
                }
                else
                {
                    // How a viewer draws the text while it is edited: the closest standard font, the field's size and colour.
                    uint c = f.Color;
                    string da = "/" + DaFonts[DaFont(f.Font)] + " " + N(f.FontSize * k) + " Tf "
                                + N(((c >> 16) & 0xFF) / 255f) + " " + N(((c >> 8) & 0xFF) / 255f) + " " + N((c & 0xFF) / 255f) + " rg";
                    _out.Ascii(" /DA ");
                    WriteTextString(_out, da);
                    if (f.Align != 0) _out.Ascii(" /Q ").Int(f.Align);
                    _out.Ascii(" /AP << /N ").Int(onId).Ascii(" 0 R >>");
                }
                _out.Ascii(" >>");
                End();
            }

        _mc = savedMc;

        foreach (var (group, (id, kids)) in _radioGroups)
        {
            int flags = FfRadio | FfNoToggleToOff | (group.ReadOnly ? FfReadOnly : 0) | (group.Required ? FfRequired : 0);
            Begin(id).Ascii("<< /FT /Btn /Ff ").Int(flags).Ascii(" /T ");
            WriteTextString(_out, group.Name);
            _out.Ascii(" /V ");
            WriteName(_out, group.Value ?? "Off");
            _out.Ascii(" /Kids [");
            foreach (int kid in kids) _out.Int(kid).Ascii(" 0 R ");
            _out.Ascii("] >>");
            End();
        }

        for (int i = 0; i < DaFonts.Length; i++)
        {
            if (_daFontIds[i] == 0) continue;
            Begin(_daFontIds[i]).Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /").Ascii(DaBaseFonts[i]).Ascii(" /Encoding /WinAnsiEncoding >>");
            End();
        }
    }

    private static string N(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Writes a name object: bytes outside the printable ASCII range, delimiters and # are written as #xx.</summary>
    private static void WriteName(ByteBuffer b, string name)
    {
        b.Byte((byte)'/');
        foreach (byte c in System.Text.Encoding.UTF8.GetBytes(name))
        {
            if (c < 33 || c > 126 || c is (byte)'#' or (byte)'/' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
                or (byte)'{' or (byte)'}' or (byte)'%')
                b.Byte((byte)'#').Ascii(c.ToString("X2", CultureInfo.InvariantCulture));
            else b.Byte(c);
        }
    }
}
