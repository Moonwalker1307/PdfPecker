using System.Text;

namespace PdfPecker.Text;

/// <summary>
/// SASLprep (RFC 4013), the stringprep profile (RFC 3454) that ISO 32000-2 section 7.6.4.3.3 prescribes for the
/// passwords of the AES-256 security handler: non-ASCII spaces become spaces, characters such as the soft hyphen and
/// variation selectors are dropped, the result is normalized to NFKC, and control, private-use, non-character, surrogate,
/// bidi control and tag characters are refused, as are right-to-left letters mixed with left-to-right ones. The tables
/// are RFC 3454's (Unicode 3.2) and the normalization the library's own, so a password gives the same key on every host.
/// </summary>
internal static partial class SaslPrep
{
    /// <summary>Why SASLprep refuses a string.</summary>
    internal enum Refusal
    {
        /// <summary>It does not: the string is prepared.</summary>
        None,
        /// <summary>A prohibited character remains after mapping and normalization (RFC 3454 tables C.1.2 to C.9).</summary>
        Prohibited,
        /// <summary>Right-to-left letters are mixed with left-to-right ones, or do not begin and end the string.</summary>
        Bidirectional,
        /// <summary>A stored string has a code point that Unicode 3.2 did not assign (RFC 3454 table A.1).</summary>
        Unassigned,
    }

    /// <summary>
    /// Prepares <paramref name="s"/>. A stored string, such as a password being set, must not contain code points that
    /// Unicode 3.2 did not assign (RFC 3454 section 7), because a later version of the profile could prepare them
    /// differently; a query, such as a password being tried, may, and they pass through unchanged, as in Unicode 3.2's
    /// normalization. <paramref name="prepared"/> is null when the string is refused.
    /// </summary>
    public static Refusal Prepare(string s, bool stored, out string? prepared)
    {
        prepared = null;
        // Printable ASCII is prepared as it is, and almost every password is printable ASCII.
        if (IsPrintableAscii(s))
        {
            prepared = s;
            return Refusal.None;
        }
        string normalized = Normalize(Map(s));
        // Steps 3 and 4: prohibited output, and the bidi rule (RFC 3454 section 6): a string with a right-to-left letter
        // has no left-to-right one, and begins and ends with a right-to-left letter.
        bool randAL = false, l = false, firstRandAL = false, lastRandAL = false;
        for (int i = 0; i < normalized.Length;)
        {
            bool first = i == 0;
            int cp = CodePointAt(normalized, ref i);
            if (In(ProhibitedRanges, cp)) return Refusal.Prohibited;
            if (stored && In(UnassignedRanges, cp)) return Refusal.Unassigned;
            lastRandAL = In(RandALCatRanges, cp);
            if (first) firstRandAL = lastRandAL;
            randAL |= lastRandAL;
            l |= !lastRandAL && In(LCatRanges, cp);
        }
        if (randAL && (l || !firstRandAL || !lastRandAL)) return Refusal.Bidirectional;
        prepared = normalized;
        return Refusal.None;
    }

    private static bool IsPrintableAscii(string s)
    {
        foreach (char c in s)
            if (c is < ' ' or > '~') return false;
        return true;
    }

    // Step 1 (RFC 4013 section 2.1): the characters commonly mapped to nothing go and non-ASCII spaces become U+0020.
    // The zero width space is in both tables; it goes, as in pypdf and in stringprep's other profiles, which map B.1 to
    // nothing first (libidn and PDFBox make it a space, which opening a file also tries).
    private static string Map(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length;)
        {
            int start = i, cp = CodePointAt(s, ref i);
            if (In(MappedToNothing, cp)) continue;
            if (In(NonAsciiSpaces, cp)) sb.Append(' ');
            else sb.Append(s, start, i - start);
        }
        return sb.ToString();
    }

    // Step 2: NFKC. Unicode 3.2 gave the code points it had not assigned no decomposition, no combining class and no
    // composition, so they stay as they are and nothing composes or reorders across them: the text between them is
    // normalized piece by piece. Later Unicode versions change nothing else that SASLprep allows.
    private static string Normalize(string s)
    {
        StringBuilder? sb = null;
        int piece = 0;
        for (int i = 0; i < s.Length;)
        {
            int start = i, cp = CodePointAt(s, ref i);
            if (!In(UnassignedRanges, cp)) continue;
            (sb ??= new StringBuilder(s.Length)).Append(UnicodeNormalization.Normalize(s[piece..start], NormalizationForm.FormKC)).Append(s, start, i - start);
            piece = i;
        }
        if (sb == null) return UnicodeNormalization.Normalize(s, NormalizationForm.FormKC);
        return sb.Append(UnicodeNormalization.Normalize(s[piece..], NormalizationForm.FormKC)).ToString();
    }

    // The code point at an index, moving past it; an unpaired surrogate stands for itself (and is prohibited).
    private static int CodePointAt(string s, ref int index)
    {
        char c = s[index++];
        if (char.IsHighSurrogate(c) && index < s.Length && char.IsLowSurrogate(s[index])) return char.ConvertToUtf32(c, s[index++]);
        return c;
    }

    // Whether a code point lies in one of a table's ranges (first, last, sorted).
    private static bool In(ReadOnlySpan<int> ranges, int cp)
    {
        int lo = 0, hi = ranges.Length / 2 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (cp < ranges[mid * 2]) hi = mid - 1;
            else if (cp > ranges[mid * 2 + 1]) lo = mid + 1;
            else return true;
        }
        return false;
    }
}
