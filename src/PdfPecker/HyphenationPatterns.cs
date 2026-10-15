using PdfPecker.Text;

namespace PdfPecker;

/// <summary>
/// Hyphenation patterns for one language, in the format of TeX (Liang's patterns, as published by the hyph-utf8 project),
/// for <c>hyphens: auto</c> in a language the library has no patterns for, or to replace its own. See
/// <see cref="PdfOptions.Hyphenation"/>.
/// </summary>
public sealed class HyphenationPatterns
{
    private readonly Lazy<Hyphenator> _hyphenator;

    /// <summary>
    /// Patterns from text: either a TeX patterns file (its <c>\patterns{...}</c> and, if present,
    /// <c>\hyphenation{...}</c> blocks are read, % comments are ignored) or the patterns separated by white space, such as
    /// the contents of a hyph-utf8 <c>hyph-xx.pat.txt</c> file.
    /// </summary>
    /// <param name="patterns">The patterns, such as <c>.ach4 a1b 2bc</c>, or a TeX file with them.</param>
    /// <param name="exceptions">Optional words hyphenated as given, separated by white space, such as <c>ta-ble project</c>.</param>
    /// <param name="leftMin">The fewest letters before a hyphen (TeX's \lefthyphenmin), at least 1.</param>
    /// <param name="rightMin">The fewest letters after a hyphen (TeX's \righthyphenmin), at least 1.</param>
    public HyphenationPatterns(string patterns, string? exceptions = null, int leftMin = 2, int rightMin = 2)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (leftMin < 1) throw new ArgumentOutOfRangeException(nameof(leftMin));
        if (rightMin < 1) throw new ArgumentOutOfRangeException(nameof(rightMin));
        LeftMin = leftMin;
        RightMin = rightMin;
        _hyphenator = new Lazy<Hyphenator>(() => Hyphenator.FromTeX(patterns, exceptions, leftMin, rightMin));
    }

    /// <summary>The fewest letters before a hyphen.</summary>
    public int LeftMin { get; }

    /// <summary>The fewest letters after a hyphen.</summary>
    public int RightMin { get; }

    internal Hyphenator Hyphenator => _hyphenator.Value;
}
