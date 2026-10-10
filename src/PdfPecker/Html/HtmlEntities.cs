using System.Globalization;
using System.Text;

namespace PdfPecker.Html;

internal static class HtmlEntities
{
    private static readonly Dictionary<string, string> Named = Build();

    private static Dictionary<string, string> Build()
    {
        // The commonly used subset of the HTML5 named character references.
        const string table =
            "amp=26 lt=3C gt=3E quot=22 apos=27 nbsp=A0 iexcl=A1 cent=A2 pound=A3 curren=A4 yen=A5 brvbar=A6 sect=A7 " +
            "uml=A8 copy=A9 ordf=AA laquo=AB not=AC shy=AD reg=AE macr=AF deg=B0 plusmn=B1 sup2=B2 sup3=B3 acute=B4 " +
            "micro=B5 para=B6 middot=B7 cedil=B8 sup1=B9 ordm=BA raquo=BB frac14=BC frac12=BD frac34=BE iquest=BF " +
            "Agrave=C0 Aacute=C1 Acirc=C2 Atilde=C3 Auml=C4 Aring=C5 AElig=C6 Ccedil=C7 Egrave=C8 Eacute=C9 Ecirc=CA " +
            "Euml=CB Igrave=CC Iacute=CD Icirc=CE Iuml=CF ETH=D0 Ntilde=D1 Ograve=D2 Oacute=D3 Ocirc=D4 Otilde=D5 " +
            "Ouml=D6 times=D7 Oslash=D8 Ugrave=D9 Uacute=DA Ucirc=DB Uuml=DC Yacute=DD THORN=DE szlig=DF agrave=E0 " +
            "aacute=E1 acirc=E2 atilde=E3 auml=E4 aring=E5 aelig=E6 ccedil=E7 egrave=E8 eacute=E9 ecirc=EA euml=EB " +
            "igrave=EC iacute=ED icirc=EE iuml=EF eth=F0 ntilde=F1 ograve=F2 oacute=F3 ocirc=F4 otilde=F5 ouml=F6 " +
            "divide=F7 oslash=F8 ugrave=F9 uacute=FA ucirc=FB uuml=FC yacute=FD thorn=FE yuml=FF OElig=152 oelig=153 " +
            "Scaron=160 scaron=161 Yuml=178 fnof=192 circ=2C6 tilde=2DC ensp=2002 emsp=2003 thinsp=2009 zwnj=200C " +
            "zwj=200D lrm=200E rlm=200F ndash=2013 mdash=2014 lsquo=2018 rsquo=2019 sbquo=201A ldquo=201C rdquo=201D " +
            "bdquo=201E dagger=2020 Dagger=2021 bull=2022 hellip=2026 permil=2030 prime=2032 Prime=2033 lsaquo=2039 " +
            "rsaquo=203A euro=20AC trade=2122 larr=2190 uarr=2191 rarr=2192 darr=2193 harr=2194 minus=2212 le=2264 " +
            "ge=2265 ne=2260 infin=221E asymp=2248 check=2713 hearts=2665 Alpha=391 alpha=3B1 Beta=392 beta=3B2 " +
            "Gamma=393 gamma=3B3 Delta=394 delta=3B4 pi=3C0 Pi=3A0 sigma=3C3 Sigma=3A3 Omega=3A9 omega=3C9 mu=3BC";
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in table.Split(' '))
        {
            int eq = pair.IndexOf('=');
            int cp = int.Parse(pair.AsSpan(eq + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            d[pair.Substring(0, eq)] = char.ConvertFromUtf32(cp);
        }
        return d;
    }

    /// <summary>Decodes character references in <paramref name="s"/>. Returns the input when it has none.</summary>
    public static string Decode(string s)
    {
        int amp = s.IndexOf('&');
        if (amp < 0) return s;
        var sb = new StringBuilder(s.Length);
        sb.Append(s, 0, amp);
        int i = amp;
        while (i < s.Length)
        {
            char c = s[i];
            if (c != '&') { sb.Append(c); i++; continue; }
            int consumed = TryDecodeAt(s, i, sb);
            if (consumed == 0) { sb.Append('&'); i++; }
            else i += consumed;
        }
        return sb.ToString();
    }

    private static int TryDecodeAt(string s, int i, StringBuilder sb)
    {
        int start = i + 1;
        if (start >= s.Length) return 0;
        if (s[start] == '#')
        {
            int p = start + 1;
            bool hex = p < s.Length && (s[p] == 'x' || s[p] == 'X');
            if (hex) p++;
            int digitsStart = p;
            int value = 0;
            while (p < s.Length && p - digitsStart < 8)
            {
                int d = HexVal(s[p]);
                if (d < 0 || (!hex && d > 9)) break;
                value = value * (hex ? 16 : 10) + d;
                p++;
            }
            if (p == digitsStart) return 0;
            if (p < s.Length && s[p] == ';') p++;
            if (value == 0 || value > 0x10FFFF || (value >= 0xD800 && value <= 0xDFFF)) value = 0xFFFD;
            // Windows-1252 remapping of C1 controls, as browsers do.
            if (value >= 0x80 && value <= 0x9F) value = Fonts.WinAnsi.C1ToUnicode(value);
            sb.Append(char.ConvertFromUtf32(value));
            return p - i;
        }
        int end = start;
        while (end < s.Length && end - start < 10 && char.IsLetterOrDigit(s[end])) end++;
        if (end == start) return 0;
        // Longest prefix match, so "&copy2024" still decodes &copy.
        for (int len = end - start; len >= 2; len--)
        {
            if (Named.TryGetValue(s.Substring(start, len), out var v))
            {
                int after = start + len;
                if (after < s.Length && s[after] == ';') after++;
                sb.Append(v);
                return after - i;
            }
        }
        return 0;
    }

    private static int HexVal(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }
}
