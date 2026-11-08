using PdfPecker.Text;

namespace PdfPecker.Tests;

/// <summary>
/// SASLprep (RFC 4013), which prepares AES-256 passwords. CI runs this class with invariant globalization too, because
/// the normalization step must not depend on ICU.
/// </summary>
public class SaslPrepTests
{
    private static string? Stored(string s, out SaslPrep.Refusal refusal)
    {
        refusal = SaslPrep.Prepare(s, stored: true, out var prepared);
        return prepared;
    }

    [Theory]
    // The examples of RFC 4013 section 3.
    [InlineData("I\u00ADX", "IX")] // soft hyphen mapped to nothing
    [InlineData("user", "user")] // no transformation
    [InlineData("USER", "USER")] // case preserved
    [InlineData("\u00AA", "a")] // output is NFKC
    [InlineData("\u2168", "IX")] // output is NFKC, matches the first
    // Non-ASCII spaces become spaces, the other characters mapped to nothing go, and compatibility characters fold.
    [InlineData("a\u00A0b\u3000c\u2003d", "a b c d")]
    [InlineData("pass\u200Bword\uFE0F\u2060", "password")]
    [InlineData("\uFB01x\uFF21\u2460", "fixA1")]
    [InlineData("Cafe\u0301", "Caf\u00E9")]
    // A string with right-to-left letters that begins and ends with one (RFC 3454 section 6).
    [InlineData("\u0627\u0031\u0628", "\u0627\u0031\u0628")]
    [InlineData("\u05E9\u05DC\u05D5\u05DD", "\u05E9\u05DC\u05D5\u05DD")]
    [InlineData("", "")]
    public void Prepares_passwords_as_rfc_4013_shows(string input, string expected)
    {
        Assert.Equal(expected, Stored(input, out var refusal));
        Assert.Equal(SaslPrep.Refusal.None, refusal);
    }

    [Theory]
    [InlineData("\u0007", "Prohibited")] // RFC 4013 section 3: prohibited character
    [InlineData("\u0627\u0031", "Bidirectional")] // RFC 4013 section 3: bidirectional check
    [InlineData("a\tb", "Prohibited")] // C.2.1, ASCII control
    [InlineData("a\u0085", "Prohibited")] // C.2.2, non-ASCII control
    [InlineData("\uE000x", "Prohibited")] // C.3, private use
    [InlineData("x\U000F0000", "Prohibited")] // C.3, supplementary private use
    [InlineData("x\uFDD0", "Prohibited")] // C.4, non-character
    [InlineData("x\uFFFD", "Prohibited")] // C.6, replacement character
    [InlineData("x\u2FF0", "Prohibited")] // C.7, ideographic description
    [InlineData("x\u202Ey", "Prohibited")] // C.8, right-to-left override
    [InlineData("x\U000E0041", "Prohibited")] // C.9, tag
    [InlineData("\u0627b\u0628", "Bidirectional")] // RandALCat with LCat
    [InlineData("1\u0627", "Bidirectional")] // RandALCat not first
    [InlineData("x\U0001F600", "Unassigned")] // an emoji, which Unicode 3.2 did not have
    [InlineData("x\u0221", "Unassigned")] // the first entry of table A.1
    public void Refuses_what_saslprep_prohibits(string input, string expected)
    {
        Assert.Null(Stored(input, out var refusal));
        Assert.Equal(expected, refusal.ToString());
    }

    [Fact]
    public void Queries_keep_unassigned_code_points_as_unicode_3_2_normalized_them()
    {
        // A query may contain code points that Unicode 3.2 did not assign; they pass through, and its NFKC left them as
        // they are. U+1F100 (digit zero full stop, Unicode 5.2) decomposes to "0." in later versions.
        Assert.Equal(SaslPrep.Refusal.None, SaslPrep.Prepare("x\U0001F600\uFB01", stored: false, out var emoji));
        Assert.Equal("x\U0001F600fi", emoji);
        Assert.Equal(SaslPrep.Refusal.None, SaslPrep.Prepare("\U0001F100\u00AA", stored: false, out var digit));
        Assert.Equal("\U0001F100a", digit);
        Assert.Equal(SaslPrep.Refusal.Unassigned, SaslPrep.Prepare("\U0001F100", stored: true, out _));
        // The prohibited characters and the bidi rule apply to queries too.
        Assert.Equal(SaslPrep.Refusal.Prohibited, SaslPrep.Prepare("\u0007", stored: false, out _));
        Assert.Equal(SaslPrep.Refusal.Bidirectional, SaslPrep.Prepare("\u0627\u0031", stored: false, out _));
    }

    [Fact]
    public void Matches_the_tables_of_rfc_3454()
    {
        // Entries at the edges of the generated tables, as RFC 3454's appendices list them.
        Assert.Equal("xy", Stored("x\u180B\u180D\uFE00\uFEFFy", out _)); // B.1
        Assert.Equal("x x", Stored("x\u205Fx", out _)); // C.1.2
        Assert.Null(Stored("x\U0001D173", out _)); // C.2.2, a musical control character
        Assert.Null(Stored("x\U0001D17A", out _));
        Assert.Null(Stored("x\U0010FFFD", out _)); // C.3
        Assert.Null(Stored("x\U0010FFFF", out _)); // C.4
        Assert.Equal(SaslPrep.Refusal.Prohibited, SaslPrep.Prepare("x" + (char)0xD800 + "y", stored: false, out _)); // C.5, an unpaired surrogate
        Assert.Null(Stored("x\u200E", out _)); // C.8, left-to-right mark
        Assert.Equal("x\u0300", Stored("x\u0340", out _)); // C.8 too, but NFKC makes it U+0300 first
        Assert.Equal("\u07B1", Stored("\u07B1", out _)); // D.1, the last Thaana letter
        Assert.Equal(SaslPrep.Refusal.Bidirectional, SaslPrep.Prepare("\u07B1\u0D85", stored: false, out _)); // D.2, a Sinhala letter
    }
}
