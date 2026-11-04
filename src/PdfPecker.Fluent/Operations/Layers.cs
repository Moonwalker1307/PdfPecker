namespace PdfPecker.Fluent.Operations;

/// <summary>Draws the pages of a layer PDF over or under the pages of a document, as qpdf's overlay and underlay do.</summary>
internal static class Layers
{
    /// <summary>How often one page may get the same layer, since every round writes the whole document again.</summary>
    internal const int MaxPerPage = 100;

    /// <summary>
    /// Up to this many different layer pages are stamped one at a time straight from the layer PDF; more are first
    /// gathered into one stamp PDF with a page for each document page.
    /// </summary>
    private const int DirectStamps = 4;

    public static byte[] Apply(byte[] pdf, ref string? password, DocumentOperation.LayerConfiguration layer, byte[] source, bool underlay,
        CancellationToken cancellationToken)
    {
        string? current = password;
        var document = PdfRenderer.Open(pdf, current);
        var stamp = PdfRenderer.Open(source);
        int pages = document.PageCount, sourcePages = stamp.PageCount;
        var targets = layer.TargetPages == null ? All(pages) : PageSelector.Resolve(layer.TargetPages, pages, nameof(layer.TargetPages));
        var from = layer.SourcePages == null ? All(sourcePages) : PageSelector.Resolve(layer.SourcePages, sourcePages, nameof(layer.SourcePages));
        var repeat = PageSelector.Resolve(layer.RepeatSourcePages ?? "", sourcePages, nameof(layer.RepeatSourcePages));

        // The k-th target page gets the k-th source page, then the repeated pages round and round. A page named twice
        // gets a second round, drawn after the first.
        var rounds = new List<int[]>();
        var seen = new int[pages];
        for (int k = 0; k < targets.Count; k++)
        {
            int page = k < from.Count ? from[k] : repeat.Count > 0 ? repeat[(k - from.Count) % repeat.Count] : 0;
            if (page == 0) break;
            int target = targets[k] - 1, round = seen[target]++;
            if (round >= MaxPerPage)
                throw new ArgumentException($"Page {targets[k]} is named more than {MaxPerPage} times in TargetPages.", nameof(layer.TargetPages));
            while (rounds.Count <= round) rounds.Add(new int[pages]);
            rounds[round][target] = page;
        }

        foreach (var round in rounds)
        {
            // qpdf shrinks a layer page larger than its target page to fit and draws any other at its own size, centred.
            var fits = new bool[pages];
            for (int i = 0; i < pages; i++)
            {
                if (round[i] == 0) continue;
                var (w, h) = document.GetPageSize(i);
                var (sw, sh) = stamp.GetPageSize(round[i] - 1);
                fits[i] = sw <= w + 0.01 && sh <= h + 0.01;
            }
            foreach (bool fit in new[] { true, false })
            {
                var chosen = Enumerable.Range(0, pages).Where(i => round[i] != 0 && fits[i] == fit).ToHashSet();
                if (chosen.Count == 0) continue;
                float scale = fit ? 1 : 0; // 0 is PdfStamper's "as large as fits"
                var distinct = chosen.Select(i => round[i]).Distinct().ToList();
                if (distinct.Count <= DirectStamps)
                {
                    foreach (int page in distinct)
                    {
                        pdf = PdfStamper.Stamp(pdf, source, Options(i => chosen.Contains(i) && round[i] == page, page - 1, false), cancellationToken);
                        current = null;
                    }
                }
                else
                {
                    // Page i of the stamp PDF goes on page i; pages left unstamped repeat a chosen page, which is never drawn.
                    int filler = round[chosen.First()];
                    var order = Enumerable.Range(0, pages).Select(i => chosen.Contains(i) ? round[i] : filler);
                    byte[] matched = PdfMerger.Merge(new[] { new PdfMergeInput(source, PdfPageRange.FromPages(order)) }, null, cancellationToken);
                    pdf = PdfStamper.Stamp(pdf, matched, Options(chosen.Contains, 0, true), cancellationToken);
                    current = null;
                }

                PdfStampOptions Options(Func<int, bool> which, int stampPage, bool match) => new()
                {
                    Underlay = underlay,
                    Pages = which,
                    StampPage = stampPage,
                    MatchPages = match,
                    Scale = scale,
                    Password = current,
                };
            }
        }
        password = current;
        return pdf;
    }

    private static List<int> All(int count) => Enumerable.Range(1, count).ToList();
}
