namespace PdfPecker.Html;

/// <summary>
/// Reads a doctype for the document's mode, as the HTML standard's tree construction does: no doctype, or one of the
/// legacy ones it lists, leaves a browser in quirks mode, and anything else (such as <c>&lt;!DOCTYPE html&gt;</c>) puts
/// it in standards mode.
/// </summary>
internal static class Doctype
{
    private static readonly string[] QuirksPublicIds = { "-//W3O//DTD W3 HTML Strict 3.0//EN//", "-/W3C/DTD HTML 4.0 Transitional/EN", "HTML" };

    private static readonly string[] QuirksPublicPrefixes =
    {
        "+//Silmaril//dtd html Pro v0r11 19970101//", "-//AS//DTD HTML 3.0 asWedit + extensions//",
        "-//AdvaSoft Ltd//DTD HTML 3.0 asWedit + extensions//", "-//IETF//DTD HTML 2.0 Level 1//", "-//IETF//DTD HTML 2.0 Level 2//",
        "-//IETF//DTD HTML 2.0 Strict Level 1//", "-//IETF//DTD HTML 2.0 Strict Level 2//", "-//IETF//DTD HTML 2.0 Strict//",
        "-//IETF//DTD HTML 2.0//", "-//IETF//DTD HTML 2.1E//", "-//IETF//DTD HTML 3.0//", "-//IETF//DTD HTML 3.2 Final//",
        "-//IETF//DTD HTML 3.2//", "-//IETF//DTD HTML 3//", "-//IETF//DTD HTML Level 0//", "-//IETF//DTD HTML Level 1//",
        "-//IETF//DTD HTML Level 2//", "-//IETF//DTD HTML Level 3//", "-//IETF//DTD HTML Strict Level 0//",
        "-//IETF//DTD HTML Strict Level 1//", "-//IETF//DTD HTML Strict Level 2//", "-//IETF//DTD HTML Strict Level 3//",
        "-//IETF//DTD HTML Strict//", "-//IETF//DTD HTML//", "-//Metrius//DTD Metrius Presentational//",
        "-//Microsoft//DTD Internet Explorer 2.0 HTML Strict//", "-//Microsoft//DTD Internet Explorer 2.0 HTML//",
        "-//Microsoft//DTD Internet Explorer 2.0 Tables//", "-//Microsoft//DTD Internet Explorer 3.0 HTML Strict//",
        "-//Microsoft//DTD Internet Explorer 3.0 HTML//", "-//Microsoft//DTD Internet Explorer 3.0 Tables//",
        "-//Netscape Comm. Corp.//DTD HTML//", "-//Netscape Comm. Corp.//DTD Strict HTML//",
        "-//O'Reilly and Associates//DTD HTML 2.0//", "-//O'Reilly and Associates//DTD HTML Extended 1.0//",
        "-//O'Reilly and Associates//DTD HTML Extended Relaxed 1.0//", "-//SQ//DTD HTML 2.0 HoTMetaL + extensions//",
        "-//SoftQuad Software//DTD HoTMetaL PRO 6.0::19990601::extensions to HTML 4.0//",
        "-//SoftQuad//DTD HoTMetaL PRO 4.0::19971010::extensions to HTML 4.0//", "-//Spyglass//DTD HTML 2.0 Extended//",
        "-//Sun Microsystems Corp.//DTD HotJava HTML//", "-//Sun Microsystems Corp.//DTD HotJava Strict HTML//",
        "-//W3C//DTD HTML 3 1995-03-24//", "-//W3C//DTD HTML 3.2 Draft//", "-//W3C//DTD HTML 3.2 Final//", "-//W3C//DTD HTML 3.2//",
        "-//W3C//DTD HTML 3.2S Draft//", "-//W3C//DTD HTML 4.0 Frameset//", "-//W3C//DTD HTML 4.0 Transitional//",
        "-//W3C//DTD HTML Experimental 19960712//", "-//W3C//DTD HTML Experimental 970421//", "-//W3C//DTD W3 HTML//",
        "-//W3O//DTD W3 HTML 3.0//", "-//WebTechs//DTD Mozilla HTML 2.0//", "-//WebTechs//DTD Mozilla HTML//",
    };

    /// <summary>Whether the doctype, the text between "&lt;!doctype" and "&gt;", leaves the document in quirks mode.</summary>
    public static bool IsQuirks(ReadOnlySpan<char> doctype)
    {
        var rest = doctype.TrimStart();
        int nameEnd = 0;
        while (nameEnd < rest.Length && !IsSpace(rest[nameEnd])) nameEnd++;
        if (!rest.Slice(0, nameEnd).Equals("html", StringComparison.OrdinalIgnoreCase)) return true;
        rest = rest.Slice(nameEnd).TrimStart();
        if (rest.IsEmpty) return false;
        ReadOnlySpan<char> publicId = rest[..0], systemId = rest[..0];
        bool hasSystem = false;
        if (rest.StartsWith("public", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest.Slice(6).TrimStart();
            if (!Quoted(ref rest, out publicId)) return true;
            hasSystem = Quoted(ref rest, out systemId);
        }
        else if (rest.StartsWith("system", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest.Slice(6).TrimStart();
            if (!Quoted(ref rest, out systemId)) return true;
            hasSystem = true;
        }
        else return true; // anything else after the name is a bogus doctype, which forces quirks
        foreach (var id in QuirksPublicIds)
            if (publicId.Equals(id, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var prefix in QuirksPublicPrefixes)
            if (publicId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        if (systemId.Equals("http://www.ibm.com/data/dtd/v11/ibmxhtml1-transitional.dtd", StringComparison.OrdinalIgnoreCase)) return true;
        return !hasSystem && (publicId.StartsWith("-//W3C//DTD HTML 4.01 Frameset//", StringComparison.OrdinalIgnoreCase)
                              || publicId.StartsWith("-//W3C//DTD HTML 4.01 Transitional//", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads a quoted identifier at the start of the text and steps past it.</summary>
    private static bool Quoted(scoped ref ReadOnlySpan<char> text, out ReadOnlySpan<char> value)
    {
        value = default;
        if (text.IsEmpty || text[0] is not ('"' or '\'')) return false;
        int close = text.Slice(1).IndexOf(text[0]);
        if (close < 0) return false;
        value = text.Slice(1, close);
        text = text.Slice(close + 2).TrimStart();
        return true;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';
}
