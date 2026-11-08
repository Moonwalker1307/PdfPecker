using System.Buffers;
using System.Text;

namespace PdfPecker.Text;

/// <summary>
/// Unicode normalization (UAX #15) from the tables that tools/UnicodeTables generates. <see cref="string.Normalize()"/>
/// asks ICU or Windows, whose Unicode version differs from host to host, and changes nothing at all where globalization
/// is invariant, as it is in the command-line tool and in the Native AOT library of the Python, Node and Rust packages.
/// Text and passwords normalize the same on every host this way.
/// </summary>
internal static class UnicodeNormalization
{
    // Hangul syllables are made of jamo by arithmetic (The Unicode Standard, section 3.12) rather than listed.
    private const int SBase = 0xAC00, LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7;
    private const int LCount = 19, VCount = 21, TCount = 28, NCount = VCount * TCount, SCount = LCount * NCount;

    /// <summary>
    /// The most code points one code point decomposes to: 18 (U+FDFA) with compatibility mappings, 4 (U+1F82) with
    /// canonical ones alone.
    /// </summary>
    internal const int MaxDecomposition = 18, MaxCanonicalDecomposition = 4;

    // No code point below U+00A0 decomposes, none below U+00C0 has a canonical decomposition, and none below U+0300
    // changes under canonical composition (or composes with the one before it), so text below these is normalized.
    private const int FirstDecomposable = 0xA0, FirstCanonicalDecomposable = 0xC0, FirstCanonicalChange = 0x300;

    // Runs of marks up to this long are sorted by insertion; longer ones, which only hostile text has, with a sort that
    // stays O(n log n).
    private const int InsertionSortLimit = 16;

    // A combining class above every real one, for marks at the start of the text, which compose with nothing before them.
    private const int Blocked = 256;

    /// <summary>The text in the normalization form; the same string when it is in that form already.</summary>
    public static string Normalize(string s, NormalizationForm form)
    {
        bool compatibility = form is NormalizationForm.FormKC or NormalizationForm.FormKD;
        bool compose = form is NormalizationForm.FormC or NormalizationForm.FormKC;
        int unchanged = compatibility ? FirstDecomposable : compose ? FirstCanonicalChange : FirstCanonicalDecomposable;
        int first = 0;
        while (first < s.Length && s[first] < unchanged) first++;
        if (first == s.Length) return s;

        // The character before the first one that may change can compose with it; the ones before that stay as they are.
        int start = Math.Max(0, first - 1);
        int[] buffer = ArrayPool<int>.Shared.Rent(s.Length - start + MaxDecomposition);
        try
        {
            int count = 0;
            for (int i = start; i < s.Length;)
            {
                int cp = CodePointAt(s, ref i);
                if (buffer.Length - count < MaxDecomposition)
                {
                    var larger = ArrayPool<int>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, count).CopyTo(larger);
                    ArrayPool<int>.Shared.Return(buffer);
                    buffer = larger;
                }
                count = Decompose(cp, compatibility, buffer, count);
            }
            var points = buffer.AsSpan(0, count);
            SortMarks(points);
            if (compose) points = points[..Compose(points)];
            return ToString(s, start, points);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The character that two characters compose to under canonical normalization (NFC of the pair is one character),
    /// such as a letter and an accent; 0 when they stay apart.
    /// </summary>
    public static int ComposePair(int first, int second)
    {
        Span<int> points = stackalloc int[2 * MaxCanonicalDecomposition];
        int count = Decompose(first, compatibility: false, points, 0);
        count = Decompose(second, compatibility: false, points, count);
        SortMarks(points[..count]);
        return Compose(points[..count]) == 1 ? points[0] : 0;
    }

    // The code point at an index of the text, moving past it; an unpaired surrogate stands for itself.
    private static int CodePointAt(string s, ref int index)
    {
        char c = s[index++];
        if (char.IsHighSurrogate(c) && index < s.Length && char.IsLowSurrogate(s[index])) return char.ConvertToUtf32(c, s[index++]);
        return c;
    }

    /// <summary>
    /// Writes the full decomposition of a code point at <paramref name="count"/>, following the one-level mappings of
    /// the table down, and returns the new count. The buffer must have room for <see cref="MaxDecomposition"/> more.
    /// </summary>
    private static int Decompose(int cp, bool compatibility, Span<int> buffer, int count)
    {
        if ((uint)(cp - SBase) < SCount)
        {
            int index = cp - SBase;
            buffer[count++] = LBase + index / NCount;
            buffer[count++] = VBase + index % NCount / TCount;
            if (index % TCount != 0) buffer[count++] = TBase + index % TCount;
            return count;
        }
        int k = cp < FirstDecomposable ? -1 : UnicodeTables.FindDecomposition(cp, compatibility);
        if (k < 0)
        {
            buffer[count++] = cp;
            return count;
        }
        var (start, end) = UnicodeTables.DecompositionMapping(k);
        for (int i = start; i < end;) count = Decompose(UnicodeTables.MappingCodePoint(ref i), compatibility, buffer, count);
        return count;
    }

    // Canonical ordering: each run of characters with a combining class is sorted by class, keeping the order of the
    // ones with the same class.
    private static void SortMarks(Span<int> points)
    {
        for (int i = 0; i < points.Length;)
        {
            if (UnicodeTables.CombiningClass(points[i]) == 0)
            {
                i++;
                continue;
            }
            int start = i;
            while (i < points.Length && UnicodeTables.CombiningClass(points[i]) != 0) i++;
            if (i - start > 1) SortRun(points[start..i]);
        }
    }

    private static void SortRun(Span<int> run)
    {
        if (run.Length <= InsertionSortLimit)
        {
            for (int k = 1; k < run.Length; k++)
            {
                int cp = run[k], combiningClass = UnicodeTables.CombiningClass(cp), j = k - 1;
                for (; j >= 0 && UnicodeTables.CombiningClass(run[j]) > combiningClass; j--) run[j + 1] = run[j];
                run[j + 1] = cp;
            }
            return;
        }
        // The class, then the position, then the code point in one key: sorting the keys is a stable sort by class.
        var keys = new long[run.Length];
        for (int k = 0; k < run.Length; k++) keys[k] = (long)UnicodeTables.CombiningClass(run[k]) << 52 | (long)k << 21 | (uint)run[k];
        keys.AsSpan().Sort();
        for (int k = 0; k < run.Length; k++) run[k] = (int)(keys[k] & 0x1FFFFF);
    }

    /// <summary>
    /// Canonical composition: each character joins the last starter before it, when they have a primary composite and
    /// nothing between them blocks it (a starter, or a mark of the same or a higher class). Returns the new length.
    /// </summary>
    private static int Compose(Span<int> points)
    {
        if (points.Length < 2) return points.Length;
        int starter = 0, starterCp = points[0];
        int lastClass = UnicodeTables.CombiningClass(starterCp) == 0 ? 0 : Blocked;
        int length = 1;
        for (int i = 1; i < points.Length; i++)
        {
            int cp = points[i], combiningClass = UnicodeTables.CombiningClass(cp);
            if ((lastClass < combiningClass || lastClass == 0) && ComposePrimary(starterCp, cp) is var composite and not 0)
            {
                points[starter] = starterCp = composite;
                continue;
            }
            if (combiningClass == 0)
            {
                starter = length;
                starterCp = cp;
            }
            lastClass = combiningClass;
            points[length++] = cp;
        }
        return length;
    }

    // The primary composite of two characters: a Hangul syllable, or a canonical pair that is not excluded; or 0.
    private static int ComposePrimary(int first, int second)
    {
        if ((uint)(first - LBase) < LCount && (uint)(second - VBase) < VCount)
            return SBase + ((first - LBase) * VCount + second - VBase) * TCount;
        if ((uint)(first - SBase) < SCount && (first - SBase) % TCount == 0 && (uint)(second - TBase - 1) < TCount - 1)
            return first + second - TBase;
        return second < FirstCanonicalChange ? 0 : UnicodeTables.Composite(first, second);
    }

    // The code points as text after the first start characters of the original, which stay as they were.
    private static string ToString(string s, int start, ReadOnlySpan<int> points)
    {
        int length = start;
        foreach (int cp in points) length += cp >= 0x10000 ? 2 : 1;
        char[] chars = ArrayPool<char>.Shared.Rent(length);
        try
        {
            s.AsSpan(0, start).CopyTo(chars);
            int n = start;
            foreach (int cp in points)
            {
                if (cp >= 0x10000)
                {
                    chars[n++] = (char)(((cp - 0x10000) >> 10) + 0xD800);
                    chars[n++] = (char)(((cp - 0x10000) & 0x3FF) + 0xDC00);
                }
                else
                {
                    chars[n++] = (char)cp;
                }
            }
            var result = chars.AsSpan(0, length);
            return result.SequenceEqual(s) ? s : new string(result);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }
}
