using System.Text.RegularExpressions;

namespace PdfPecker.Tests;

public class StreamingMergeTests
{
    private static string Doc(string name) => $"<html><body><h1>{name} 1</h1><h1 style='page-break-before:always'>{name} 2</h1></body></html>";

    private static readonly byte[] A = HtmlToPdf.Convert(Doc("A"));
    private static readonly byte[] B = HtmlToPdf.Convert(Doc("B"));
    private static readonly byte[] Plain = HtmlToPdf.Convert("<p>No headings</p>");

    /// <summary>The file without what differs from one merge to the next: the document id and the dates.</summary>
    private static string Stable(byte[] pdf) =>
        Regex.Replace(PdfTestHelpers.Latin1(pdf), @"/ID \[<[0-9a-f]+><[0-9a-f]+>\]|/(CreationDate|ModDate) \([^)]*\)", "", RegexOptions.IgnoreCase);

    [Fact]
    public void Streaming_writes_the_same_pdf_as_merging_in_memory()
    {
        var output = new WriteOnlyStream();
        var merger = new StreamingPdfMerger(output);
        foreach (var pdf in new[] { A, Plain, B }) merger.Add(pdf);
        merger.Complete();

        PdfTestHelpers.AssertStructurallyValid(output.Data);
        Assert.Equal(5, merger.PageCount);
        Assert.Equal(3, merger.InputCount);
        Assert.Equal(Stable(PdfMerger.Merge(A, Plain, B)), Stable(output.Data));
    }

    [Fact]
    public void Each_pdf_is_written_out_when_it_is_added()
    {
        // With embedded fonts, so that most of each file is fonts and content.
        var a = HtmlToPdf.Convert(Doc("Første"), new PdfOptions { EmbedStandardFonts = true });
        var b = HtmlToPdf.Convert(Doc("Anden"), new PdfOptions { EmbedStandardFonts = true });
        var output = new WriteOnlyStream();
        var merger = new StreamingPdfMerger(output);

        merger.Add(a);
        long afterFirst = output.Data.Length;
        merger.Add(b);
        long afterSecond = output.Data.Length;
        merger.Complete();

        // Each input's pages, fonts and content are out before the next one is added; only the end waits.
        Assert.True(afterFirst > a.Length * 3 / 4, $"{afterFirst} bytes after the first PDF of {a.Length}");
        Assert.True(afterSecond - afterFirst > b.Length / 2, $"{afterSecond - afterFirst} bytes after the second PDF of {b.Length}");
        Assert.True(output.Data.Length - afterSecond < 2048, $"{output.Data.Length - afterSecond} bytes written on completion");
    }

    [Fact]
    public void Bookmarks_are_chained_across_an_input_without_bookmarks()
    {
        var output = new WriteOnlyStream();
        var merger = new StreamingPdfMerger(output);
        foreach (var pdf in new[] { A, Plain, B }) merger.Add(pdf);
        merger.Complete();

        var text = PdfTestHelpers.Latin1(output.Data);
        var root = Regex.Match(text, @"<< /Type /Outlines /First (\d+) 0 R /Last (\d+) 0 R /Count 4 >>");
        Assert.True(root.Success, "merged outline root");
        var ids = new List<int>();
        int id = int.Parse(root.Groups[1].Value), prev = 0;
        for (int guard = 0; id != 0 && guard < 10; guard++)
        {
            var item = Regex.Match(text, $@"\n{id} 0 obj\n(.*?)\nendobj", RegexOptions.Singleline).Groups[1].Value;
            var prevMatch = Regex.Match(item, @"/Prev (\d+) 0 R");
            Assert.Equal(prev, prevMatch.Success ? int.Parse(prevMatch.Groups[1].Value) : 0);
            ids.Add(id);
            prev = id;
            var next = Regex.Match(item, @"/Next (\d+) 0 R");
            id = next.Success ? int.Parse(next.Groups[1].Value) : 0;
        }
        Assert.Equal(4, ids.Count);
        Assert.Equal(int.Parse(root.Groups[2].Value), ids[^1]);
    }

    [Fact]
    public async Task MergeAsync_merges_pdfs_as_they_arrive()
    {
        var output = new WriteOnlyStream();

        var pages = await PdfMerger.MergeAsync(Arrive(A, B), output);

        Assert.Equal(4, pages);
        Assert.Equal(4, PdfRenderer.Open(output.Data).PageCount);
        PdfTestHelpers.AssertStructurallyValid(output.Data);
    }

    [Fact]
    public async Task MergeAsync_needs_at_least_one_pdf()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => PdfMerger.MergeAsync(Arrive(), new MemoryStream()));
    }

    [Fact]
    public void The_page_limit_counts_every_input()
    {
        var merger = new StreamingPdfMerger(new MemoryStream(), new PdfMergeOptions { MaxPages = 3 });
        merger.Add(A);

        Assert.Throws<PdfLimitException>(() => merger.Add(B));
    }

    [Fact]
    public void Nothing_can_be_added_after_completing()
    {
        var merger = new StreamingPdfMerger(new MemoryStream());
        Assert.Throws<InvalidDataException>(() => merger.Complete()); // no pages

        var completed = new StreamingPdfMerger(new MemoryStream());
        completed.Add(A);
        completed.Complete();
        Assert.Throws<InvalidOperationException>(() => completed.Add(B));
        Assert.Throws<InvalidOperationException>(() => completed.Complete());
    }

    [Fact]
    public void Adding_is_cancellable()
    {
        var merger = new StreamingPdfMerger(new MemoryStream());

        Assert.Throws<OperationCanceledException>(() => merger.Add(A, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Adding_and_completing_asynchronously_writes_the_same_pdf()
    {
        var output = new WriteOnlyStream();
        var merger = new StreamingPdfMerger(output, new PdfMergeOptions { Title = "Run 42" });
        await merger.AddAsync(A);
        Assert.Equal(1, merger.InputCount);
        Assert.Equal(2, merger.PageCount);
        await merger.AddAsync(B);
        await merger.CompleteAsync();

        PdfTestHelpers.AssertStructurallyValid(output.Data);
        Assert.Equal(Stable(PdfMerger.Merge(new[] { A, B }, new PdfMergeOptions { Title = "Run 42" })), Stable(output.Data));
    }

    [Fact]
    public void After_a_pdf_fails_nothing_more_can_be_added()
    {
        var merger = new StreamingPdfMerger(new MemoryStream());
        merger.Add(A);

        Assert.Throws<InvalidDataException>(() => merger.Add("not a pdf"u8.ToArray()));
        Assert.Throws<InvalidOperationException>(() => merger.Add(B));
        Assert.Throws<InvalidOperationException>(() => merger.Complete());
    }

    [Fact]
    public void A_null_pdf_does_not_spoil_the_merge()
    {
        var output = new WriteOnlyStream();
        var merger = new StreamingPdfMerger(output);
        merger.Add(A);

        Assert.Throws<ArgumentNullException>(() => merger.Add(null!));
        merger.Add(B);
        merger.Complete();
        Assert.Equal(4, PdfRenderer.Open(output.Data).PageCount);
    }

    private static async IAsyncEnumerable<byte[]> Arrive(params byte[][] pdfs)
    {
        foreach (var pdf in pdfs)
        {
            await Task.Yield();
            yield return pdf;
        }
    }

    /// <summary>A stream that can only be written forwards, like an HTTP response body.</summary>
    private sealed class WriteOnlyStream : Stream
    {
        private readonly MemoryStream _data = new();

        public byte[] Data => _data.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _data.Write(buffer, offset, count);
    }
}
