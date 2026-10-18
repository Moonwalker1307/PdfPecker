using PdfPecker.Text;

namespace PdfPecker.Fonts;

// The joining scripts: the form each letter takes from its neighbours, and the presentation form characters that stand
// in for fonts without OpenType forms.
internal static partial class ComplexShaper
{
    private enum Form : byte { None, Isol, Fina, Fin2, Fin3, Medi, Med2, Init }

    private static uint MaskOf(Form form) => form switch
    {
        Form.Isol => Isol,
        Form.Fina => Fina,
        Form.Fin2 => Fin2,
        Form.Fin3 => Fin3,
        Form.Medi => Medi,
        Form.Med2 => Med2,
        Form.Init => Init,
        _ => 0,
    };

    // The columns of the joining state machine: how a character joins. Alaph and Dalath/Rish have columns of their own
    // in Syriac only, and join on the right elsewhere.
    private enum JoiningColumn { NonJoining, Left, Right, Dual, Alaph, DalathRish }

    private static JoiningColumn ColumnOf(JoiningType type, Script script) => type switch
    {
        JoiningType.L => JoiningColumn.Left,
        JoiningType.R => JoiningColumn.Right,
        JoiningType.D => JoiningColumn.Dual,
        JoiningType.Alaph => script == Script.Syriac ? JoiningColumn.Alaph : JoiningColumn.Right,
        JoiningType.DalathRish => script == Script.Syriac ? JoiningColumn.DalathRish : JoiningColumn.Right,
        _ => JoiningColumn.NonJoining,
    };

    // HarfBuzz's joining state machine: per state and joining column, the form of the previous character (None keeps
    // the one it has), the form of this one, and the next state.
    private static readonly (Form Previous, Form Current, byte Next)[,] Machine =
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

    /// <summary>
    /// Gives each joining character the mask of its form. Transparent characters (marks) are skipped: they neither
    /// join nor break a join. A character's form is settled once the next joining character is seen, which may still
    /// change it.
    /// </summary>
    private static void ArabicJoining(GlyphRun run, Script script)
    {
        int previous = -1, state = 0;
        var previousForm = Form.None;
        for (int i = 0; i < run.Count; i++)
        {
            var type = UnicodeTables.Joining(run[i].CodePoint);
            if (type == JoiningType.T) continue;
            var (changed, current, next) = Machine[state, (int)ColumnOf(type, script)];
            if (previous >= 0)
                AddForm(run, previous, changed != Form.None ? changed : previousForm);
            previous = i;
            previousForm = current;
            state = next;
        }
        if (previous >= 0) AddForm(run, previous, previousForm);
    }

    private static void AddForm(GlyphRun run, int index, Form form)
    {
        if (form == Form.None) return;
        var glyph = run[index];
        glyph.Mask |= MaskOf(form);
        run[index] = glyph;
    }

    // The forms of a letter among the presentation form characters, numbered as UnicodeTables numbers them.
    private enum PresentationForm { None = -1, Isolated, Final, Initial, Medial }

    private static PresentationForm PresentationFormOf(uint mask)
        => (mask & Isol) != 0 ? PresentationForm.Isolated
            : (mask & Fina) != 0 ? PresentationForm.Final
            : (mask & Init) != 0 ? PresentationForm.Initial
            : (mask & Medi) != 0 ? PresentationForm.Medial
            : PresentationForm.None;

    private const int Lam = 0x0644;

    // For fonts without OpenType Arabic forms: the presentation form characters, as HarfBuzz's fallback shaping uses
    // them, and lam-alef ligatures.
    private static void PresentationForms(FontProgram font, GlyphRun run, bool ligate)
    {
        for (int i = 0; i < run.Count; i++)
        {
            var glyph = run[i];
            var form = PresentationFormOf(glyph.Mask);
            if (form == PresentationForm.None) continue;
            if (ligate && glyph.CodePoint == Lam && form is PresentationForm.Initial or PresentationForm.Medial
                && TryLigateLamAlef(font, run, i, form))
                continue;
            if (UnicodeTables.PresentationForm(glyph.CodePoint, (int)form) is var character and not 0
                && font.GlyphId(character) is var gid and not 0)
            {
                glyph.Gid = gid;
                run[i] = glyph;
            }
        }
    }

    /// <summary>
    /// Makes the lam at the index and an alef that follows it (past any marks) and joins it one ligature glyph, when the
    /// font has the ligature's presentation form: isolated after an initial lam, final after a medial one.
    /// </summary>
    private static bool TryLigateLamAlef(FontProgram font, GlyphRun run, int lam, PresentationForm lamForm)
    {
        int alef = lam + 1;
        while (alef < run.Count && IsMark(run[alef].Props)) alef++;
        if (alef == run.Count || (run[alef].Mask & Fina) == 0) return false;
        var ligatureForm = lamForm == PresentationForm.Initial ? PresentationForm.Isolated : PresentationForm.Final;
        int character = UnicodeTables.LamAlef(run[alef].CodePoint, (int)ligatureForm);
        if (character == 0) return false;
        int gid = font.GlyphId(character);
        if (gid == 0) return false;
        var glyph = run[lam];
        var alefGlyph = run[alef];
        glyph.Gid = gid;
        glyph.Props = OpenTypeLayout.Ligature;
        glyph.Length = alefGlyph.Cluster + alefGlyph.Length - glyph.Cluster;
        run[lam] = glyph;
        run.RemoveAt(alef);
        return true;
    }
}
