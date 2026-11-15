using System.Globalization;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>What an operator is, from the MathML operator dictionary (MathML Core §B).</summary>
[Flags]
internal enum MathOperatorFlags : ushort
{
    None = 0,
    Stretchy = 1,
    Symmetric = 2,
    LargeOp = 4,
    MovableLimits = 8,
    Accent = 16,
    Fence = 32,
    Separator = 64,
    /// <summary>Stretches across (an arrow or a bar over or under a base) rather than up and down.</summary>
    Horizontal = 128,
}

/// <summary>Where an operator stands in its row, which decides the dictionary entry it takes.</summary>
internal enum MathForm : byte { Infix, Prefix, Postfix }

/// <summary>An operator's spacing, in ems of its own font, and its properties.</summary>
internal readonly record struct MathOperator(float LSpace, float RSpace, MathOperatorFlags Flags)
{
    public bool Has(MathOperatorFlags f) => (Flags & f) != 0;
}

/// <summary>
/// MathML Core elements: the operator dictionary, the math-style and script level of each element, and the preparation
/// before styling that gives single-letter identifiers their italic and scripts their smaller font size.
/// </summary>
internal static class MathElements
{
    /// <summary>The attribute that makes an identifier italic: a single letter is a variable.</summary>
    public const string ItalicAttribute = "pdf-math-italic";

    /// <summary>The attribute that makes an element's font smaller than its parent's by its value in script levels.</summary>
    public const string ScriptAttribute = "pdf-math-script";

    /// <summary>The attribute an element in display math-style has, which keeps fractions and large operators large.</summary>
    public const string DisplayAttribute = "pdf-math-display";

    /// <summary>The token elements, whose content is text laid out as it is.</summary>
    public static bool IsToken(string name) => name is "mi" or "mn" or "mo" or "mtext" or "ms";

    /// <summary>Whether an element is a MathML element that MathML Core lays out (or treats as mrow).</summary>
    public static bool IsMathName(string name) => name.Length > 1 && name[0] == 'm' && name is "math" or "mi" or "mn" or "mo" or "mtext" or "ms"
        or "mspace" or "mrow" or "mstyle" or "mpadded" or "mphantom" or "merror" or "menclose" or "mfrac" or "msqrt" or "mroot"
        or "msub" or "msup" or "msubsup" or "munder" or "mover" or "munderover" or "mmultiscripts" or "mprescripts" or "mtable"
        or "mtr" or "mlabeledtr" or "mtd" or "maction" or "mfenced"
        || name is "semantics" or "annotation" or "annotation-xml" or "none";

    // Spacing in 18ths of an em, as the dictionary gives it.
    private const float Thin = 3 / 18f, Medium = 4 / 18f, Thick = 5 / 18f, VeryThin = 1 / 18f;

    private const string Relations = "=<>≤≥≠≈≡≢∼≃≅≪≫∝∈∉∋∌⊂⊃⊆⊇⊊⊋⊄⊅→←↑↓↔↕⇒⇐⇑⇓⇔⟶⟵⟷⟹⟸⟺↦⟼↪↩⊢⊣⊨⊩∣∤∥∦≺≻⪯⪰≼≽≐≔≕≜∶∷≍≙⋘⋙⊏⊐⊑⊒⇝⇀⇁↼↽⇌⇋";
    private const string HorizontalArrows = "→←↔⇒⇐⇔⟶⟵⟷⟹⟸⟺↦⟼↪↩⇀⇁↼↽⇌⇋";
    private const string Binary = "+-−±∓×÷⋅·∗∘∙∩∪⊕⊖⊗⊘⊙∧∨∖⋆★†‡⊎⊓⊔*⋄≀⊲⊳⊴⊵⋉⋊⋋⋌⊛⊚⊝⊞⊟⊠⊡⨿⨯";
    private const string PrefixSigns = "+-−±∓";
    private const string Sums = "∑∏∐⋀⋁⋂⋃⨀⨁⨂⨄⨅⨆⨉";
    private const string Integrals = "∫∬∭∮∯∰∱∲∳⨌⨍⨏⨑";
    private const string Fences = "()[]{}|‖⟨⟩⌈⌉⌊⌋⟦⟧⟮⟯〈〉⟪⟫⦃⦄⦅⦆";
    private const string Accents = "^~¯‾_ˆˇ˘˙¨˜˚´`¸→←↔⏞⏟⏜⏝⎴⎵‸⃗";
    private const string HorizontalBars = "¯‾_⏞⏟⏜⏝⎴⎵";

    private static readonly HashSet<string> LimitWords = new(StringComparer.Ordinal)
    {
        "lim", "max", "min", "sup", "inf", "liminf", "limsup", "det", "gcd", "lcm", "Pr", "argmax", "argmin", "projlim", "injlim",
    };

    /// <summary>
    /// The dictionary entry of an operator's text in a form, falling back to the other forms as MathML Core §3.2.5.7.3
    /// does; an operator not in it has thick spaces on both sides.
    /// </summary>
    public static MathOperator Lookup(string text, MathForm form)
    {
        if (text.Length == 0) return new MathOperator(0, 0, MathOperatorFlags.None);
        if (LimitWords.Contains(text)) return new MathOperator(0, Thin, MathOperatorFlags.MovableLimits);
        if (text is ":=" or "==" or "!=" or "<=" or ">=" or "->" or "<-" or "=>" or "::") return new MathOperator(Thick, Thick, MathOperatorFlags.None);
        if (text is "||") return new MathOperator(0, 0, MathOperatorFlags.Fence | MathOperatorFlags.Stretchy | MathOperatorFlags.Symmetric);
        if (text.Length > 1 && !(text.Length == 2 && char.IsSurrogatePair(text, 0))) return new MathOperator(Thick, Thick, MathOperatorFlags.None);
        char c = text[0];
        // Invisible operators: function application, invisible times, separator and plus.
        if (c is '⁡' or '⁢' or '⁣' or '⁤') return new MathOperator(0, 0, MathOperatorFlags.None);
        if (Fences.Contains(c)) return new MathOperator(0, 0, MathOperatorFlags.Fence | MathOperatorFlags.Stretchy | MathOperatorFlags.Symmetric);
        if (Sums.Contains(c)) return new MathOperator(VeryThin, Thin, MathOperatorFlags.LargeOp | MathOperatorFlags.MovableLimits | MathOperatorFlags.Symmetric);
        if (Integrals.Contains(c)) return new MathOperator(VeryThin, 2 / 18f, MathOperatorFlags.LargeOp | MathOperatorFlags.Symmetric);
        if (c is ',' or ';') return new MathOperator(0, Thin, MathOperatorFlags.Separator);
        if (c is '.' or '…' or '⋯' or '⋮' or '⋱' or '!' or '′' or '″' or '‴' or '\'' or '%') return new MathOperator(0, 0, MathOperatorFlags.None);
        if (c is '∀' or '∃' or '∄' or '¬') return new MathOperator(0, VeryThin, MathOperatorFlags.None);
        if (c is '∂' or '∇' or '/' or '\\' or '∠' or '△' or '√' or '∛' or '∜') return new MathOperator(0, 0, MathOperatorFlags.None);
        if (form == MathForm.Prefix && PrefixSigns.Contains(c)) return new MathOperator(0, 0, MathOperatorFlags.None);
        var accent = Accents.Contains(c) ? MathOperatorFlags.Accent : MathOperatorFlags.None;
        if (HorizontalBars.Contains(c)) return new MathOperator(0, 0, accent | MathOperatorFlags.Stretchy | MathOperatorFlags.Horizontal);
        if (Relations.Contains(c))
        {
            var arrow = HorizontalArrows.Contains(c) ? MathOperatorFlags.Stretchy | MathOperatorFlags.Horizontal : MathOperatorFlags.None;
            return new MathOperator(Thick, Thick, arrow | accent);
        }
        if (Binary.Contains(c)) return new MathOperator(Medium, Medium, MathOperatorFlags.None);
        if (accent != 0) return new MathOperator(0, 0, accent);
        return new MathOperator(Thick, Thick, MathOperatorFlags.None);
    }

    /// <summary>
    /// The text of a token element as MathML Core shows it: an operator's hyphen-minus is a minus sign. Whitespace at
    /// either end is not part of a token.
    /// </summary>
    public static string TokenText(Element e) => e.InnerText().Trim(' ', '\t', '\n', '\r', '\f');

    /// <summary>
    /// The children of a MathML element that are laid out: its element children that are shown, without the
    /// annotations of semantics, whitespace and comments.
    /// </summary>
    public static List<Element> Children(Element e)
    {
        var list = new List<Element>();
        foreach (var c in e.Children)
            if (c is Element el && el.Style is { Display: not Css.Display.None } && !el.Detached) list.Add(el);
        return list;
    }

    /// <summary>
    /// Whether the children of <paramref name="e"/> are in display math-style, and by how many script levels each
    /// one's font is smaller than <paramref name="e"/>'s, as MathML Core's user agent style sheet sets math-style and
    /// math-depth (§A).
    /// </summary>
    public static (bool Display, int Levels) ChildStyle(Element e, int index, bool display)
    {
        switch (e.Name)
        {
            case "mfrac":
                // A fraction's parts are compact, and smaller than the fraction when it is compact itself.
                return (false, display ? 0 : 1);
            case "mroot":
                return index == 0 ? (false, 0) : (false, 2);
            case "msqrt":
                return (false, 0);
            case "msub" or "msup" or "msubsup" or "mmultiscripts":
                return index == 0 ? (display, 0) : (false, 1);
            case "munder" or "mover" or "munderover":
            {
                if (index == 0) return (display, 0);
                // An accent keeps the base's size; a limit is a script. The under script is the second child.
                bool under = index == 1 && e.Name != "mover";
                bool accent = IsAccent(e, under, e.Children.OfType<Element>().ElementAtOrDefault(index));
                return (false, accent ? 0 : 1);
            }
            case "mstyle":
            {
                bool d = e.GetAttribute("displaystyle") is { } ds ? ds.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) : display;
                return (d, 0);
            }
            case "mtable":
                return (false, 0);
            default:
                return (display, 0);
        }
    }

    /// <summary>
    /// Whether the under or over script of an munder, mover or munderover is an accent: its accentunder or accent
    /// attribute, or else whether the script is an operator the dictionary calls an accent.
    /// </summary>
    public static bool IsAccent(Element e, bool under, Element? script)
    {
        if (e.GetAttribute(under ? "accentunder" : "accent") is { } a) return a.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        return script is { Name: "mo" } && Lookup(TokenText(script), MathForm.Postfix).Has(MathOperatorFlags.Accent);
    }

    /// <summary>
    /// Marks single-letter identifiers italic, the elements whose font is smaller (scripts, fraction parts in compact
    /// style, root indices) and the elements in display style, before the styles are resolved. A mstyle's scriptlevel
    /// moves the level too, and so does the math element's own.
    /// </summary>
    public static void Prepare(Element root)
    {
        foreach (var c in root.Children)
            if (c is Element e)
            {
                if (e.Name == "math") PrepareMath(e, e.GetAttribute("display") is { } d && d.Trim().Equals("block", StringComparison.OrdinalIgnoreCase), 0, 0);
                else if (e.Name is not ("svg" or "template")) Prepare(e);
            }
    }

    private static void PrepareMath(Element e, bool display, int depth, int level)
    {
        // Math nested deeper than this is laid out without changing its size any further.
        if (depth > 200) return;
        if (display) SetAttribute(e, DisplayAttribute);
        if (e.Name == "mi" && e.GetAttribute("mathvariant") == null && SingleLetter(TokenText(e))) SetAttribute(e, ItalicAttribute);
        if (e.Name == "mo")
        {
            // MathML Core shows an operator's hyphen-minus as a minus sign.
            foreach (var t in e.Children)
                if (t is TextNode text && text.Text.Contains('-')) text.Text = text.Text.Replace('-', '−');
        }
        if (IsToken(e.Name)) return;
        int index = 0;
        foreach (var c in e.Children)
        {
            if (c is not Element child) continue;
            var (d, levels) = ChildStyle(e, index++, display);
            if (child.Name == "mstyle" && child.GetAttribute("scriptlevel") is { } sl) levels = ScriptLevel(sl, level + levels) - level;
            levels = Math.Clamp(levels, -2, 4);
            if (levels != 0) SetAttribute(child, ScriptAttribute, levels.ToString(CultureInfo.InvariantCulture));
            if (IsMathName(child.Name)) PrepareMath(child, d, depth + 1, level + levels);
        }
    }

    /// <summary>The script level a mstyle's scriptlevel sets: "+1" and "-1" change the level it would have, a bare number replaces it.</summary>
    private static int ScriptLevel(string value, int current)
    {
        var v = value.Trim();
        if (v.Length == 0 || v.Length > 4) return current;
        bool relative = v[0] is '+' or '-';
        if (!int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n)) return current;
        return Math.Max(0, relative ? current + n : n);
    }

    private static bool SingleLetter(string text)
    {
        if (text.Length == 1) return char.IsLetter(text[0]);
        return text.Length == 2 && char.IsSurrogatePair(text, 0) && char.IsLetter(text, 0);
    }

    private static void SetAttribute(Element e, string name, string value = "")
    {
        if (e.GetAttribute(name) == null) e.AddAttribute(name, value);
    }
}
