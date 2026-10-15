using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>Merging tagged PDFs keeps their structure trees, so the result stays accessible.</summary>
public class MergeTaggingTests
{
    private static byte[] Tagged(string html) => HtmlToPdf.Convert(html, new PdfOptions { Accessibility = PdfAccessibility.Tagged });

    private const string Doc = "<html lang=\"en\"><head><title>Report</title></head><body><h1>Title</h1><p>Text with a <a href=\"https://example.com\">link</a>.</p></body></html>";

    private static List<PdfDict> Pages(PdfFile file)
        => file.GetArray(file.GetDict(file.Catalog, "Pages"), "Kids")!.Items.Select(k => (PdfDict)file.Resolve(k)!).ToList();

    private static Dictionary<int, PdfObject> ParentTree(PdfFile file)
    {
        var nums = file.GetArray(file.GetDict(file.GetDict(file.Catalog, "StructTreeRoot"), "ParentTree"), "Nums")!;
        var result = new Dictionary<int, PdfObject>();
        for (int i = 0; i + 1 < nums.Count; i += 2)
        {
            int key = (int)((PdfNumber)nums[i]).Value;
            if (result.Count > 0) Assert.True(key > result.Keys.Max(), "parent-tree keys must ascend");
            result.Add(key, nums[i + 1]);
        }
        return result;
    }

    [Fact]
    public void Merged_tagged_documents_stay_tagged()
    {
        var merged = PdfMerger.Merge(Tagged(Doc), Tagged(Doc));
        PdfTestHelpers.AssertStructurallyValid(merged);
        var file = PdfFile.Open(merged, null);
        var catalog = file.Catalog!;
        Assert.True(file.Resolve(file.GetDict(catalog, "MarkInfo")!["Marked"]) is PdfBool { Value: true });
        Assert.Equal("en", ((PdfString)file.Resolve(catalog["Lang"])!).Text);
        var root = file.GetDict(catalog, "StructTreeRoot")!;
        var kids = file.GetArray(root, "K")!;
        Assert.Equal(2, kids.Count); // one Document element per input
        var rootRef = catalog["StructTreeRoot"] as PdfRef;
        foreach (var k in kids.Items)
        {
            var elem = (PdfDict)file.Resolve(k)!;
            Assert.Equal("Document", file.GetName(elem, "S"));
            Assert.Equal(rootRef!.Num, ((PdfRef)elem["P"]!).Num);
        }

        // Each page and link annotation has its own key, and every key is in the parent tree.
        var tree = ParentTree(file);
        var keys = new List<int>();
        foreach (var page in Pages(file))
        {
            keys.Add(file.GetInt(page, "StructParents", -1));
            foreach (var a in file.GetArray(page, "Annots")?.Items ?? new List<PdfObject>())
                if (file.Resolve(a) is PdfDict ad && ad["StructParent"] != null) keys.Add(file.GetInt(ad, "StructParent", -1));
        }
        Assert.Equal(4, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.True(tree.ContainsKey(k), $"key {k} missing from the parent tree"));
        Assert.True(file.GetInt(root, "ParentTreeNextKey", 0) > keys.Max());

        // The second page's marked content belongs to elements under the second Document element.
        var second = (PdfArray)file.Resolve(tree[file.GetInt(Pages(file)[1], "StructParents", -1)])!;
        var elem2 = (PdfDict)file.Resolve(second.Items[0])!;
        for (int i = 0; i < 10 && file.GetName(elem2, "S") != "Document"; i++) elem2 = file.GetDict(elem2, "P")!;
        Assert.Same(file.Resolve(kids.Items[1]), elem2);
    }

    [Fact]
    public void A_second_language_moves_onto_its_document_element()
    {
        var merged = PdfMerger.Merge(Tagged(Doc), Tagged(Doc.Replace("lang=\"en\"", "lang=\"da\"")));
        var file = PdfFile.Open(merged, null);
        Assert.Equal("en", ((PdfString)file.Resolve(file.Catalog!["Lang"])!).Text);
        var kids = file.GetArray(file.GetDict(file.Catalog, "StructTreeRoot"), "K")!;
        Assert.Null(((PdfDict)file.Resolve(kids.Items[0])!)["Lang"]);
        Assert.Equal("da", ((PdfString)file.Resolve(((PdfDict)file.Resolve(kids.Items[1])!)["Lang"])!).Text);
    }

    [Fact]
    public void Untagged_inputs_lose_their_keys_and_tagged_ones_keep_theirs()
    {
        var merged = PdfMerger.Merge(HtmlToPdf.Convert(Doc), Tagged(Doc));
        PdfTestHelpers.AssertStructurallyValid(merged);
        var file = PdfFile.Open(merged, null);
        var pages = Pages(file);
        Assert.Null(pages[0]["StructParents"]);
        Assert.True(ParentTree(file).ContainsKey(file.GetInt(pages[1], "StructParents", -1)));
        Assert.Single(file.GetArray(file.GetDict(file.Catalog, "StructTreeRoot"), "K")!.Items);
    }

    [Fact]
    public void Untagged_merges_stay_untagged()
    {
        var merged = PdfTestHelpers.Latin1(PdfMerger.Merge(HtmlToPdf.Convert(Doc), HtmlToPdf.Convert(Doc)));
        Assert.DoesNotContain("/StructTreeRoot", merged);
        Assert.DoesNotContain("/MarkInfo", merged);
    }

    [Fact]
    public void Merged_PdfUA_documents_still_claim_PdfUA()
    {
        var ua = new PdfOptions { Accessibility = PdfAccessibility.PdfUA1 };
        var merged = PdfMerger.Merge(HtmlToPdf.Convert(Doc, ua), HtmlToPdf.Convert(Doc, ua));
        var file = PdfFile.Open(merged, null);
        var xmp = System.Text.Encoding.UTF8.GetString(file.DecodeStream((PdfStream)file.Resolve(file.Catalog!["Metadata"])!));
        Assert.Contains("<pdfuaid:part>1</pdfuaid:part>", xmp);
        Assert.Contains("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Report</rdf:li>", xmp);
        Assert.NotNull(file.GetDict(file.Catalog, "ViewerPreferences"));
    }

    [Fact]
    public void Merging_PdfUA_with_a_merely_tagged_document_claims_nothing()
    {
        var merged = PdfMerger.Merge(HtmlToPdf.Convert(Doc, new PdfOptions { Accessibility = PdfAccessibility.PdfUA1 }), Tagged(Doc));
        var file = PdfFile.Open(merged, null);
        Assert.Null(file.Catalog!["Metadata"]);
        Assert.NotNull(file.GetDict(file.Catalog, "StructTreeRoot"));
    }
}
