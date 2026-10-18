using PdfPecker.Text;

namespace PdfPecker.Fonts;

// Thai and Lao, as HarfBuzz's Thai shaper (hb-ot-shaper-thai.cc) handles them: Sara Am becomes Nikhahit and Sara Aa,
// with the Nikhahit moved before the marks above, and fonts without Thai lookups get their marks moved through the
// private-use glyphs that Windows and Mac Thai fonts have for them.
internal static partial class ComplexShaper
{
    // Sara Am, which Lao has 0x80 later; its Nikhahit and Sara Aa are as far from it in both scripts.
    private const int SaraAm = 0x0E33, LaoOffset = 0x0080, Nikhahit = 0x0E4D, SaraAa = 0x0E32;

    private static bool IsSaraAm(int cp) => (cp & ~LaoOffset) == SaraAm;

    // The marks above the base that a Nikhahit from Sara Am goes before (Lao's are the same, 0x80 later).
    private static bool IsAboveBaseMark(int cp)
        => (cp & ~LaoOffset) is (>= 0x0E34 and <= 0x0E37) or (>= 0x0E47 and <= 0x0E4E) or 0x0E31 or 0x0E3B;

    /// <summary>
    /// Replaces each Sara Am with Nikhahit and Sara Aa, and moves the Nikhahit back over the marks above the base before
    /// it, as Uniscribe and HarfBuzz do (it is drawn above them). Sara Aa keeps the character; the Nikhahit takes none.
    /// </summary>
    private static void DecomposeSaraAm(GlyphRun run, FontProgram font, OpenTypeLayout? layout)
    {
        for (int i = 0; i < run.Count; i++)
        {
            var aa = run[i];
            if (!IsSaraAm(aa.CodePoint)) continue;
            int nikhahitCp = aa.CodePoint - SaraAm + Nikhahit, aaCp = aa.CodePoint - SaraAm + SaraAa;
            int nikhahitGid = font.GlyphId(nikhahitCp), aaGid = font.GlyphId(aaCp);
            if (nikhahitGid == 0 || aaGid == 0) continue;
            var nikhahit = aa;
            nikhahit.CodePoint = nikhahitCp;
            nikhahit.Gid = nikhahitGid;
            nikhahit.Length = 0;
            nikhahit.Props = layout is { HasGlyphClasses: true } ? layout.GlyphProps(nikhahitGid) : OpenTypeLayout.Mark;
            aa.CodePoint = aaCp;
            aa.Gid = aaGid;
            aa.Props = layout is { HasGlyphClasses: true } ? layout.GlyphProps(aaGid) : OpenTypeLayout.Base;
            run[i] = aa;
            int at = i;
            while (at > 0 && IsAboveBaseMark(run[at - 1].CodePoint)) at--;
            run.Insert(at, nikhahit);
            i++;
        }
    }

    private enum ThaiConsonant : byte { Normal, Ascender, RemovableDescender, StrictDescender, NotConsonant }

    private enum ThaiMark : byte { Above, Below, Tone, NotMark }

    private enum ThaiAction : byte { None, ShiftDown, ShiftLeft, ShiftDownLeft, RemoveDescender }

    private static ThaiConsonant ThaiConsonantOf(int cp) => cp switch
    {
        0x0E1B or 0x0E1D or 0x0E1F => ThaiConsonant.Ascender,
        0x0E0D or 0x0E10 => ThaiConsonant.RemovableDescender,
        0x0E0E or 0x0E0F => ThaiConsonant.StrictDescender,
        >= 0x0E01 and <= 0x0E2E => ThaiConsonant.Normal,
        _ => ThaiConsonant.NotConsonant,
    };

    private static ThaiMark ThaiMarkOf(int cp) => cp switch
    {
        0x0E31 or (>= 0x0E34 and <= 0x0E37) or 0x0E47 or 0x0E4D or 0x0E4E => ThaiMark.Above,
        >= 0x0E38 and <= 0x0E3A => ThaiMark.Below,
        >= 0x0E48 and <= 0x0E4C => ThaiMark.Tone,
        _ => ThaiMark.NotMark,
    };

    // The states of the marks above and below a consonant, as HarfBuzz's two machines number them, and where each
    // kind of consonant starts them.
    private static ReadOnlySpan<byte> AboveStart => [0, 1, 0, 0, 3];
    private static ReadOnlySpan<byte> BelowStart => [0, 0, 1, 2, 2];

    // Per state and kind of mark: the action and the next state.
    private static readonly (ThaiAction Action, byte Next)[,] AboveMachine =
    {
        { (ThaiAction.None, 3), (ThaiAction.None, 0), (ThaiAction.ShiftDown, 3) },
        { (ThaiAction.ShiftLeft, 2), (ThaiAction.None, 1), (ThaiAction.ShiftDownLeft, 2) },
        { (ThaiAction.None, 3), (ThaiAction.None, 2), (ThaiAction.ShiftLeft, 3) },
        { (ThaiAction.None, 3), (ThaiAction.None, 3), (ThaiAction.None, 3) },
    };
    private static readonly (ThaiAction Action, byte Next)[,] BelowMachine =
    {
        { (ThaiAction.None, 0), (ThaiAction.None, 2), (ThaiAction.None, 0) },
        { (ThaiAction.None, 1), (ThaiAction.RemoveDescender, 2), (ThaiAction.None, 1) },
        { (ThaiAction.None, 2), (ThaiAction.ShiftDown, 2), (ThaiAction.None, 2) },
    };

    /// <summary>
    /// For Thai fonts without Thai GSUB: tone marks and vowels above move down or left over short and ascending
    /// consonants, vowels below move down under descending ones, and Yo Ying and Tho Than lose their descender, using
    /// the private-use glyphs of Windows or, failing those, Mac Thai fonts.
    /// </summary>
    private static void ThaiPrivateUseForms(FontProgram font, OpenTypeLayout? layout, GlyphRun run)
    {
        int above = AboveStart[(int)ThaiConsonant.NotConsonant], below = BelowStart[(int)ThaiConsonant.NotConsonant];
        int baseIndex = 0;
        for (int i = 0; i < run.Count; i++)
        {
            var mark = ThaiMarkOf(run[i].CodePoint);
            if (mark == ThaiMark.NotMark)
            {
                var consonant = ThaiConsonantOf(run[i].CodePoint);
                above = AboveStart[(int)consonant];
                below = BelowStart[(int)consonant];
                baseIndex = i;
                continue;
            }
            var aboveEdge = AboveMachine[above, (int)mark];
            var belowEdge = BelowMachine[below, (int)mark];
            above = aboveEdge.Next;
            below = belowEdge.Next;
            // At least one of the two actions is none.
            var action = aboveEdge.Action != ThaiAction.None ? aboveEdge.Action : belowEdge.Action;
            int target = action == ThaiAction.RemoveDescender ? baseIndex : i;
            if (action == ThaiAction.None || ThaiPrivateUseGlyph(font, run[target].CodePoint, action) is not (var gid and not 0)) continue;
            var glyph = run[target];
            glyph.Gid = gid;
            if (layout is { HasGlyphClasses: true }) glyph.Props = layout.GlyphProps(gid);
            run[target] = glyph;
        }
    }

    // The Windows private-use character for a Thai character and action, then the Mac one.
    private static int ThaiPrivateUseGlyph(FontProgram font, int cp, ThaiAction action)
    {
        var (windows, mac) = (action, cp) switch
        {
            (ThaiAction.ShiftDown, >= 0x0E48 and <= 0x0E4C) => (0xF70A + cp - 0x0E48, 0xF88B + (cp - 0x0E48) * 3),
            (ThaiAction.ShiftDown, >= 0x0E38 and <= 0x0E3A) => (0xF718 + cp - 0x0E38, 0xF89B + cp - 0x0E38),
            (ThaiAction.ShiftDownLeft, >= 0x0E48 and <= 0x0E4C) => (0xF705 + cp - 0x0E48, 0xF88C + (cp - 0x0E48) * 3),
            (ThaiAction.ShiftLeft, >= 0x0E48 and <= 0x0E4C) => (0xF713 + cp - 0x0E48, 0xF88A + (cp - 0x0E48) * 3),
            (ThaiAction.ShiftLeft, 0x0E31) => (0xF710, 0xF884),
            (ThaiAction.ShiftLeft, >= 0x0E34 and <= 0x0E37) => (0xF701 + cp - 0x0E34, 0xF885 + cp - 0x0E34),
            (ThaiAction.ShiftLeft, 0x0E47) => (0xF712, 0xF889),
            (ThaiAction.ShiftLeft, 0x0E4D) => (0xF711, 0xF899),
            (ThaiAction.RemoveDescender, 0x0E0D) => (0xF70F, 0xF89A),
            (ThaiAction.RemoveDescender, 0x0E10) => (0xF700, 0xF89E),
            _ => (0, 0),
        };
        if (windows == 0) return 0;
        int gid = font.GlyphId(windows);
        return gid != 0 ? gid : font.GlyphId(mac);
    }
}
