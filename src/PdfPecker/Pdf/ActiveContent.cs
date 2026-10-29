using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// Finds and removes what makes a PDF act on its own when a viewer opens it or the reader clicks: JavaScript, actions
/// that launch programs, open other files, submit or import form data or play media, additional actions (/AA),
/// XFA forms, media and 3D annotations, and links with script or file schemes. The page content, bookmarks that jump
/// within the document, web links and form fields stay.
/// </summary>
/// <remarks>
/// The parsed objects are changed in place, so writing the file afterwards (see <see cref="PdfRewriter"/>) leaves the
/// removed parts out: nothing points at them any more.
/// </remarks>
internal static class ActiveContent
{
    // Actions that run script, start programs, reach other files or the network, or play media that can carry script.
    private static readonly HashSet<string> ActiveActions = new(StringComparer.Ordinal)
    {
        "JavaScript", "Launch", "SubmitForm", "ImportData", "GoToR", "GoToE", "Rendition", "RichMediaExecute", "Movie", "Sound",
    };

    // Annotations that exist to play media or 3D content, which can carry script of its own.
    private static readonly HashSet<string> ActiveAnnotations = new(StringComparer.Ordinal) { "RichMedia", "Screen", "Movie", "Sound", "3D" };

    // Link schemes a viewer may run as script or resolve to a local or network file.
    private static readonly string[] ActiveSchemes = { "javascript:", "vbscript:", "data:", "file:" };

    /// <summary>
    /// Walks everything reachable from the catalog and counts the active parts; with <paramref name="remove"/>, takes them
    /// out of the parsed objects as well. With <paramref name="attachments"/>, embedded files count as active too.
    /// </summary>
    public static int Process(PdfFile file, bool remove, bool attachments, CancellationToken cancellationToken)
    {
        int found = 0;
        if (file.Trailer["Root"] is not { } root) return 0;
        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfObject>();
        stack.Push(root);
        int visited = 0;
        while (stack.Count > 0)
        {
            if ((++visited & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var o = file.Resolve(stack.Pop());
            if (o == null || !seen.Add(o)) continue;
            if (o is PdfArray array)
            {
                foreach (var item in array.Items) stack.Push(item);
                continue;
            }
            if (o is not PdfDict dict) continue;
            found += Clean(file, dict, remove, attachments, ReferenceEquals(o, file.Catalog));
            foreach (var value in dict.Entries.Values) stack.Push(value);
        }
        return found;
    }

    /// <summary>Counts, and with <paramref name="remove"/> removes, the active entries of one dictionary.</summary>
    private static int Clean(PdfFile file, PdfDict dict, bool remove, bool attachments, bool isCatalog)
    {
        var drop = new List<string>();
        int found = 0;
        foreach (var (key, value) in dict.Entries)
        {
            bool active = key switch
            {
                "AA" or "XFA" or "JavaScript" => true,
                "A" or "OpenAction" => IsActiveAction(file, value),
                "Next" => file.Resolve(value) is PdfDict && IsActiveAction(file, value),
                "EmbeddedFiles" => attachments,
                "AF" => attachments && isCatalog,
                _ => false,
            };
            if (active) drop.Add(key);
            else if (key == "Next") found += Filter(file, value, remove, a => IsActiveAction(file, a));
            else if (key == "Annots") found += Filter(file, value, remove, a => IsActiveAnnotation(file, a, attachments));
        }
        found += drop.Count;
        if (remove)
            foreach (var key in drop) dict.Entries.Remove(key);
        return found;
    }

    /// <summary>
    /// Counts the items of an array (or the single dictionary, for /Next) that <paramref name="active"/> picks, and with
    /// <paramref name="remove"/> removes them from the array.
    /// </summary>
    private static int Filter(PdfFile file, PdfObject value, bool remove, Func<PdfObject, bool> active)
    {
        if (file.Resolve(value) is not PdfArray array) return 0; // a single /Next action is a key of its own
        int count = 0;
        for (int i = array.Items.Count - 1; i >= 0; i--)
        {
            if (!active(array.Items[i])) continue;
            count++;
            if (remove) array.Items.RemoveAt(i);
        }
        return count;
    }

    private static bool IsActiveAction(PdfFile file, PdfObject? value)
    {
        if (file.Resolve(value) is not PdfDict action || file.GetName(action, "S") is not { } type) return false;
        if (ActiveActions.Contains(type)) return true;
        if (type != "URI" || file.Resolve(action["URI"]) is not PdfString uri) return false;
        var text = uri.Text.TrimStart();
        foreach (var scheme in ActiveSchemes)
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsActiveAnnotation(PdfFile file, PdfObject value, bool attachments)
        => file.Resolve(value) is PdfDict annotation && file.GetName(annotation, "Subtype") is { } subtype
           && (ActiveAnnotations.Contains(subtype) || attachments && subtype == "FileAttachment");
}
