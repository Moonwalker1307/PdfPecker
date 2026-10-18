using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PdfPecker.Text;

namespace PdfPecker.Fonts;

/// <summary>
/// OpenType shaping for scripts whose glyphs depend on their neighbours, along the lines of HarfBuzz: Arabic, Syriac,
/// N'Ko and other joining scripts get their initial, medial, final and isolated forms and required ligatures;
/// Hebrew and the joining scripts get mark positioning (GPOS mark, mkmk), cursive attachment and kerning. Fonts
/// without OpenType forms fall back to the Unicode Arabic presentation forms. Glyphs come out in logical order, with
/// positions for the direction the run is drawn in.
/// </summary>
internal static class ComplexShaper
{
    /// <summary>True when the text has characters of a script this shaper handles.</summary>
    public static bool Handles(string s, int start, int length)
    {
        for (int i = start, end = start + length; i < end; i++)
        {
            char c = s[i];
            if (c >= 0x0590 && (c <= 0x08FF || c is >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿')) return true;
        }
        return false;
    }

    /// <summary>The direction complex text is shaped for when the caller does not know it: its first strong character's.</summary>
    public static bool GuessRtl(string s, int start, int length)
    {
        for (int i = start, end = start + length; i < end; i++)
        {
            var t = UnicodeTables.Bidi(s[i]);
            if (t == BidiClass.L) return false;
            if (t is BidiClass.R or BidiClass.AL) return true;
        }
        return false;
    }

    private enum Script : byte { Hebrew, Arabic, Syriac, Nko, Mandaic, Other }

    private static Script ScriptOf(int cp) => cp switch
    {
        >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F => Script.Hebrew,
        >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x0870 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF
            or >= 0x10EC0 and <= 0x10EFF => Script.Arabic,
        >= 0x0700 and <= 0x074F or >= 0x0860 and <= 0x086F => Script.Syriac,
        >= 0x07C0 and <= 0x07FF => Script.Nko,
        >= 0x0840 and <= 0x085F => Script.Mandaic,
        _ => Script.Other,
    };

    private static uint ScriptTag(Script s) => FontProgram.Tag(s switch
    {
        Script.Hebrew => "hebr",
        Script.Arabic => "arab",
        Script.Syriac => "syrc",
        Script.Nko => "nko ",
        Script.Mandaic => "mand",
        _ => "DFLT",
    });

    private static bool Joins(Script s) => s is Script.Arabic or Script.Syriac or Script.Nko or Script.Mandaic;

    // Feature mask bits.
    private const uint Global = 1, Isol = 1 << 1, Fina = 1 << 2, Fin2 = 1 << 3, Fin3 = 1 << 4, Medi = 1 << 5, Med2 = 1 << 6, Init = 1 << 7, Rtlm = 1 << 8;

    private enum Form : byte { None, Isol, Fina, Fin2, Fin3, Medi, Med2, Init }

    private static readonly uint[] FormMask = [0, Isol, Fina, Fin2, Fin3, Medi, Med2, Init];

    /// <summary>The lookups of each stage, with the mask of the glyphs each one applies to.</summary>
    private sealed class Plan
    {
        public readonly List<List<(int Lookup, uint Mask)>> Gsub = new();
        public readonly List<(int Lookup, uint Mask)> Gpos = new();
        public bool HasForms, HasRlig, HasMarkPositioning, HasGposKern;
    }

    // Keyed weakly by the font, so that a font registered for one conversion is not kept alive by its plans for the
    // life of the process.
    private static readonly ConditionalWeakTable<FontProgram, ConcurrentDictionary<Script, Plan>> s_plans = new();

    private static Plan GetPlan(FontProgram font, OpenTypeLayout? ot, Script script)
        => s_plans.GetValue(font, static _ => new ConcurrentDictionary<Script, Plan>()).GetOrAdd(script, static (script, ot) =>
        {
            // OpenTypeLayout checks the lists a plan reads when it loads the font; should a damaged font still get
            // past that, its text is shaped without the layout tables rather than failing the document. The plan is
            // cached, so this runs once per font and script.
            try { return BuildPlan(ot, script); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException) { return new Plan(); }
        }, ot);

    private static Plan BuildPlan(OpenTypeLayout? ot, Script script)
    {
        var plan = new Plan();
        if (ot == null) return plan;
        uint tag = ScriptTag(script);

        void Stage(params (string Feature, uint Mask)[] features)
        {
            var stage = new Dictionary<int, uint>();
            foreach (var (f, mask) in features)
                foreach (int l in ot.FeatureLookups(false, tag, FontProgram.Tag(f)))
                    stage[l] = stage.TryGetValue(l, out uint m) ? m | mask : mask;
            if (stage.Count > 0) plan.Gsub.Add(stage.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToList());
        }

        const uint All = 0xFFFFFFFF;
        Stage(("rvrn", All));
        if (Joins(script))
        {
            Stage(("ccmp", All), ("locl", All), ("rtlm", Rtlm));
            foreach (var (f, m) in new[] { ("isol", Isol), ("fina", Fina), ("fin2", Fin2), ("fin3", Fin3), ("medi", Medi), ("med2", Med2), ("init", Init) })
            {
                if (ot.HasFeature(false, tag, FontProgram.Tag(f))) plan.HasForms = true;
                Stage((f, m));
            }
            plan.HasRlig = ot.HasFeature(false, tag, FontProgram.Tag("rlig"));
            Stage(("rlig", All));
            Stage(("calt", All), ("rclt", All), ("liga", All), ("clig", All), ("mset", All));
        }
        else
        {
            plan.HasRlig = ot.HasFeature(false, tag, FontProgram.Tag("rlig"));
            Stage(("ccmp", All), ("locl", All), ("rtlm", Rtlm), ("rlig", All), ("calt", All), ("rclt", All), ("liga", All), ("clig", All));
        }

        var gpos = new Dictionary<int, uint>();
        foreach (var f in new[] { "abvm", "blwm", "curs", "dist", "kern", "mark", "mkmk" })
            foreach (int l in ot.FeatureLookups(true, tag, FontProgram.Tag(f)))
                gpos[l] = All;
        plan.Gpos.AddRange(gpos.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)));
        plan.HasMarkPositioning = ot.HasFeature(true, tag, FontProgram.Tag("mark"));
        plan.HasGposKern = ot.HasFeature(true, tag, FontProgram.Tag("kern"));
        return plan;
    }

    [ThreadStatic] private static GlyphRun? t_buf;

    /// <summary>
    /// Shapes the text into glyphs in logical order, with advances and offsets in 1/1000 em for drawing it in the
    /// given direction.
    /// </summary>
    public static void Shape(FontProgram font, string s, int start, int length, bool rtl, List<ShapedGlyph> output)
    {
        output.Clear();
        var buf = t_buf ??= new GlyphRun();
        buf.Clear();
        var ot = font.Layout;

        // The script: that of the first character that belongs to one.
        var script = Script.Other;
        for (int i = start, end = start + length; i < end && script == Script.Other; i++) script = ScriptOf(s[i]);

        for (int i = start, end = start + length; i < end; i++)
        {
            int cp = s[i], n = 1;
            if (char.IsHighSurrogate(s[i]) && i + 1 < end && char.IsLowSurrogate(s[i + 1])) { cp = char.ConvertToUtf32(s[i], s[i + 1]); n = 2; }
            AddChar(font, ot, buf, cp, i, n, rtl, 0);
            i += n - 1;
        }

        ReorderMarks(buf, script);
        Compose(font, ot, buf);
        var plan = GetPlan(font, ot, script);
        if (Joins(script)) ArabicJoining(buf, script);
        // Joiners and other invisible controls have done their part; they are not drawn.
        buf.RemoveAll(g => UnicodeTables.IsDefaultIgnorable(g.CodePoint));
        if (Joins(script) && !plan.HasForms) PresentationForms(font, buf, ligate: !plan.HasRlig);

        // GSUB and GPOS share one work budget for the run, so a font cannot spend it twice over, and GSUB may make the
        // run only so much longer than the text.
        int nextLigId = 1, operations = OpenTypeLayout.OperationBudget(buf.Count), maxLength = OpenTypeLayout.MaxRunLength(buf.Count);
        if (ot != null)
        {
            try
            {
                foreach (var stage in plan.Gsub)
                    foreach (var (lookup, mask) in stage)
                        ot.ApplyLookup(buf, false, lookup, mask, rtl, ref nextLigId, ref operations, maxLength);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
            {
                // A malformed GSUB table: the glyphs stay as substituted so far.
            }
        }

        for (int i = 0; i < buf.Count; i++)
        {
            var g = buf[i];
            g.XAdvance = font.Advance(g.Gid);
            buf[i] = g;
        }

        if (ot != null && plan.Gpos.Count > 0)
        {
            try
            {
                foreach (var (lookup, mask) in plan.Gpos) ot.ApplyLookup(buf, true, lookup, mask, rtl, ref nextLigId, ref operations, maxLength);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
            {
            }
        }
        if (!plan.HasGposKern && font.Kerning is { } kerning)
            for (int i = 0; i + 1 < buf.Count; i++)
            {
                if ((buf[i].Props & OpenTypeLayout.Mark) != 0) continue;
                int j = i + 1;
                while (j < buf.Count && (buf[j].Props & OpenTypeLayout.Mark) != 0) j++;
                if (j == buf.Count) break;
                int k = kerning.Pair(buf[i].Gid, buf[j].Gid);
                if (k == 0) continue;
                var g = buf[i];
                g.XAdvance += k;
                buf[i] = g;
            }

        // Marks take no room (HarfBuzz zeroes them after GPOS). Marks that GPOS did not place are put on their base.
        for (int i = 0; i < buf.Count; i++)
        {
            var g = buf[i];
            if ((g.Props & OpenTypeLayout.Mark) == 0) continue;
            if (!rtl && ot is not { HasGpos: true }) g.XOffset -= g.XAdvance;
            g.XAdvance = 0;
            if (g.AttachChain == 0 && !plan.HasMarkPositioning && rtl)
            {
                // Without mark positioning, a mark is drawn where it would follow its base left to right, as fonts design
                // marks to sit on the glyph before them.
                int b = i - 1;
                while (b >= 0 && (buf[b].Props & OpenTypeLayout.Mark) != 0) b--;
                if (b >= 0) g.XOffset += buf[b].XAdvance;
            }
            buf[i] = g;
        }
        OpenTypeLayout.PropagateAttachments(buf, rtl);

        float scale = 1000f / font.UnitsPerEm;
        for (int i = 0; i < buf.Count; i++)
        {
            var g = buf[i];
            output.Add(new ShapedGlyph(g.Gid, g.Cluster, g.Length, g.XAdvance * scale, g.XOffset * scale, g.YOffset * scale));
        }
    }

    // Adds the glyph of a character, or of its decomposition when the font lacks the character but has its parts.
    private static void AddChar(FontProgram font, OpenTypeLayout? ot, GlyphRun buf, int cp, int cluster, int length, bool rtl, int depth)
    {
        int gid = font.GlyphId(cp);
        if (gid == 0 && depth < 4 && UnicodeTables.Decompose(cp, out int first, out int second) && (second == 0 || font.GlyphId(second) != 0))
        {
            int count = buf.Count;
            AddChar(font, ot, buf, first, cluster, length, rtl, depth + 1);
            if (buf[count].Gid != 0)
            {
                if (second != 0) AddChar(font, ot, buf, second, cluster, 0, rtl, depth + 1);
                return;
            }
            buf.RemoveRange(count, buf.Count - count);
        }
        var g = new GlyphInfo { CodePoint = cp, Cluster = cluster, Length = length, Mask = Global };
        if (rtl && UnicodeTables.Mirror(cp) is int m && m != cp)
        {
            // A mirrored character takes its mirror's glyph, or the font's rtlm form.
            if (font.GlyphId(m) is int mg && mg != 0) gid = mg;
            else g.Mask |= Rtlm;
        }
        g.Gid = gid;
        g.Props = ot is { HasGlyphClasses: true } ? ot.GlyphProps(gid) : UnicodeTables.IsMark(cp) ? OpenTypeLayout.Mark : OpenTypeLayout.Base;
        buf.Add(g);
    }

    // A letter and the mark right after it become the precomposed letter when the font has it, as HarfBuzz composes
    // them (waw and hamza above, alef and madda, ...).
    private static void Compose(FontProgram font, OpenTypeLayout? ot, GlyphRun buf)
    {
        for (int i = 0; i + 1 < buf.Count; i++)
        {
            var a = buf[i];
            if (a.CodePoint is < 0x0600 or > 0x08FF || (a.Props & OpenTypeLayout.Mark) != 0) continue;
            while (i + 1 < buf.Count && UnicodeTables.Compose(a.CodePoint, buf[i + 1].CodePoint) is int c && c != 0 && font.GlyphId(c) is int gid && gid != 0)
            {
                var m = buf[i + 1];
                a.CodePoint = c;
                a.Gid = gid;
                a.Props = ot is { HasGlyphClasses: true } ? ot.GlyphProps(gid) : OpenTypeLayout.Base;
                a.Length = Math.Max(a.Cluster + a.Length, m.Cluster + m.Length) - a.Cluster;
                buf[i] = a;
                buf.RemoveAt(i + 1);
            }
        }
    }

    // ------------------------------------------------------------------ mark order

    // Sorts each run of combining marks by (modified) combining class, as normalization does; in Arabic, modifier
    // combining marks such as hamza above then move to the front of their run, as HarfBuzz orders them.
    private static void ReorderMarks(GlyphRun buf, Script script)
    {
        int i = 0;
        while (i < buf.Count)
        {
            if (UnicodeTables.ModifiedCombiningClass(buf[i].CodePoint) == 0) { i++; continue; }
            int start = i;
            while (i < buf.Count && UnicodeTables.ModifiedCombiningClass(buf[i].CodePoint) != 0) i++;
            if (i - start < 2) continue;
            // A stable insertion sort: runs of marks are short.
            for (int k = start + 1; k < i; k++)
            {
                var g = buf[k];
                int c = UnicodeTables.ModifiedCombiningClass(g.CodePoint);
                int j = k - 1;
                while (j >= start && UnicodeTables.ModifiedCombiningClass(buf[j].CodePoint) > c) { buf[j + 1] = buf[j]; j--; }
                buf[j + 1] = g;
            }
            if (script == Script.Arabic) MoveModifierMarks(buf, start, i);
        }
    }

    private static bool IsModifierMark(int cp)
        => cp is 0x0654 or 0x0655 or 0x0658 or 0x06DC or 0x06E3 or 0x06E7 or 0x06E8 or 0x08CA or 0x08CB or 0x08CD or 0x08CE or 0x08CF or 0x08D3 or 0x08F3;

    // The modifier combining marks at the start of the marks of class 220, and then of class 230, move to the front of
    // the run (HarfBuzz's reorder_marks_arabic).
    private static void MoveModifierMarks(GlyphRun buf, int start, int end)
    {
        int i = start;
        for (int cc = 220; cc <= 230; cc += 10)
        {
            while (i < end && UnicodeTables.ModifiedCombiningClass(buf[i].CodePoint) < cc) i++;
            if (i == end) break;
            if (UnicodeTables.ModifiedCombiningClass(buf[i].CodePoint) > cc) continue;
            int j = i;
            while (j < end && UnicodeTables.ModifiedCombiningClass(buf[j].CodePoint) == cc && IsModifierMark(buf[j].CodePoint)) j++;
            if (i == j) continue;
            var moved = buf.GetRange(i, j - i);
            buf.RemoveRange(i, j - i);
            buf.InsertRange(start, moved);
            start += j - i;
            i = j;
        }
    }

    // ------------------------------------------------------------------ Arabic joining

    // HarfBuzz's joining state machine: per state and joining type, the action on the previous character, the action on
    // this one, and the next state. Columns: U, L, R, D, Alaph, Dalath/Rish.
    private static readonly (Form Prev, Form Curr, byte Next)[,] Machine =
    {
        // 0: the previous character is U and does not join.
        { (Form.None, Form.None, 0), (Form.None, Form.Isol, 2), (Form.None, Form.Isol, 1), (Form.None, Form.Isol, 2), (Form.None, Form.Isol, 1), (Form.None, Form.Isol, 6) },
        // 1: the previous character is R, or an isolated Alaph, and does not join.
        { (Form.None, Form.None, 0), (Form.None, Form.Isol, 2), (Form.None, Form.Isol, 1), (Form.None, Form.Isol, 2), (Form.None, Form.Fin2, 5), (Form.None, Form.Isol, 6) },
        // 2: the previous character is an isolated D or L, which joins.
        { (Form.None, Form.None, 0), (Form.None, Form.Isol, 2), (Form.Init, Form.Fina, 1), (Form.Init, Form.Fina, 3), (Form.Init, Form.Fina, 4), (Form.Init, Form.Fina, 6) },
        // 3: the previous character is a final D, which joins.
        { (Form.None, Form.None, 0), (Form.None, Form.Isol, 2), (Form.Medi, Form.Fina, 1), (Form.Medi, Form.Fina, 3), (Form.Medi, Form.Fina, 4), (Form.Medi, Form.Fina, 6) },
        // 4: the previous character is a final Alaph, which does not join.
        { (Form.None, Form.None, 0), (Form.None, Form.Isol, 2), (Form.Med2, Form.Isol, 1), (Form.Med2, Form.Isol, 2), (Form.Med2, Form.Fin2, 5), (Form.Med2, Form.Isol, 6) },
        // 5: the previous character is a Fin2 or Fin3 Alaph, which does not join.
        { (Form.None, Form.None, 0), (Form.None, Form.Isol, 2), (Form.Isol, Form.Isol, 1), (Form.Isol, Form.Isol, 2), (Form.Isol, Form.Fin2, 5), (Form.Isol, Form.Isol, 6) },
        // 6: the previous character is Dalath or Rish, which does not join.
        { (Form.None, Form.None, 0), (Form.None, Form.Isol, 2), (Form.None, Form.Isol, 1), (Form.None, Form.Isol, 2), (Form.None, Form.Fin3, 5), (Form.None, Form.Isol, 6) },
    };

    private static void ArabicJoining(GlyphRun buf, Script script)
    {
        var forms = new Form[buf.Count];
        int prev = -1, state = 0;
        for (int i = 0; i < buf.Count; i++)
        {
            var jt = UnicodeTables.Joining(buf[i].CodePoint);
            if (jt == JoiningType.T) continue;
            int col = jt switch
            {
                JoiningType.U => 0,
                JoiningType.L => 1,
                JoiningType.R => 2,
                JoiningType.D => 3,
                JoiningType.Alaph => script == Script.Syriac ? 4 : 2,
                JoiningType.DalathRish => script == Script.Syriac ? 5 : 2,
                _ => 0,
            };
            var (p, c, next) = Machine[state, col];
            if (p != Form.None && prev >= 0) forms[prev] = p;
            forms[i] = c;
            prev = i;
            state = next;
        }
        for (int i = 0; i < buf.Count; i++)
        {
            if (forms[i] == Form.None) continue;
            var g = buf[i];
            g.Mask |= FormMask[(int)forms[i]];
            buf[i] = g;
        }
    }

    // For fonts without OpenType Arabic forms: the presentation form characters, as HarfBuzz's fallback shaping uses
    // them, and lam-alef ligatures.
    private static void PresentationForms(FontProgram font, GlyphRun buf, bool ligate)
    {
        for (int i = 0; i < buf.Count; i++)
        {
            var g = buf[i];
            int form = (g.Mask & Isol) != 0 ? 0 : (g.Mask & Fina) != 0 ? 1 : (g.Mask & Init) != 0 ? 2 : (g.Mask & Medi) != 0 ? 3 : -1;
            if (form < 0) continue;
            if (ligate && g.CodePoint == 0x0644 && form is 2 or 3)
            {
                // Lam followed (past any marks) by an alef that joins it: one ligature glyph.
                int j = i + 1;
                while (j < buf.Count && (buf[j].Props & OpenTypeLayout.Mark) != 0) j++;
                if (j < buf.Count && (buf[j].Mask & Fina) != 0 && UnicodeTables.LamAlef(buf[j].CodePoint, form == 2 ? 0 : 1) is int la && la != 0
                    && font.GlyphId(la) is int lg && lg != 0)
                {
                    var alef = buf[j];
                    g.Gid = lg;
                    g.Props = OpenTypeLayout.Ligature;
                    g.Length = alef.Cluster + alef.Length - g.Cluster;
                    buf[i] = g;
                    buf.RemoveAt(j);
                    continue;
                }
            }
            if (UnicodeTables.PresentationForm(g.CodePoint, form) is int pf && pf != 0 && font.GlyphId(pf) is int pg && pg != 0)
            {
                g.Gid = pg;
                buf[i] = g;
            }
        }
    }
}
