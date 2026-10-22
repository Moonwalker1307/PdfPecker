using System.Text;
using PdfPecker.Pdf;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>
/// The shared writer of parsed objects keeps the two spellings the writers had before they shared it, so their output
/// stays byte for byte the same: the rewriting writers (merge, stamp, encrypt) and the incremental updates (sign,
/// validation data, attachments, form filling).
/// </summary>
public class PdfObjectWriterTests
{
    private static string Write(PdfObjectSyntax syntax, PdfObject o, IPdfObjectEncryption? encryption = null)
    {
        var b = new ByteBuffer();
        new PdfObjectWriter(syntax, encryption) { ObjectNumber = 7 }.WriteObject(b, o);
        return Encoding.Latin1.GetString(b.Span);
    }

    private static PdfDict Sample() => new(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
    {
        ["Type"] = PdfName.Get("A b#"),
        ["S"] = new PdfString(new byte[] { 0xAB, 0x01 }),
        ["N"] = new PdfArray(new List<PdfObject> { PdfNumber.Int(-3), PdfNumber.Real(1.25), PdfNumber.Real(-0.0), PdfNumber.Real(double.NaN) }),
        ["R"] = new PdfRef(12, 1),
        ["E"] = new PdfDict(),
        ["B"] = PdfBool.True,
    });

    [Fact]
    public void Rewrite_syntax_spaces_dictionaries_and_writes_lower_case_hex()
        => Assert.Equal("<< /Type /A#20b#23 /S <ab01> /N [-3 1.25 -0 0] /R 12 1 R /E << >> /B true >>", Write(PdfObjectSyntax.Rewrite, Sample()));

    [Fact]
    public void Update_syntax_is_compact_and_writes_upper_case_hex()
        => Assert.Equal("<</Type /A#20b#23 /S <AB01> /N [-3 1.25 0 0] /R 12 1 R /E <<>> /B true >>", Write(PdfObjectSyntax.Update, Sample()));

    [Fact]
    public void Names_beyond_Latin1_are_written_as_UTF8()
    {
        var name = PdfName.Get("éĀ");
        Assert.Equal("/#C3#A9#C4#80", Write(PdfObjectSyntax.Rewrite, name));
        Assert.Equal("/#C3#A9#C4#80", Write(PdfObjectSyntax.Stamp, name));
        Assert.Equal("/#C3#A9#C4#80", Write(PdfObjectSyntax.Update, name));
        Assert.Equal("/#E9", Write(PdfObjectSyntax.Update, PdfName.Get("é")));
        Assert.Equal("/#E9", Write(PdfObjectSyntax.Rewrite, PdfName.Get("é")));
    }

    private sealed class Reverse : IPdfObjectEncryption
    {
        public byte[] EncryptString(byte[] data, int num, int gen) => data.Reverse().Append((byte)num).ToArray();
        public byte[] EncryptStream(byte[] data, int num, int gen) => data;
    }

    [Fact]
    public void Strings_are_encrypted_for_the_current_object()
    {
        var s = new PdfString(new byte[] { 0xAB, 0x01 });
        Assert.Equal("<01ab07>", Write(PdfObjectSyntax.Rewrite, s, new Reverse()));
        Assert.Equal("<01AB07>", Write(PdfObjectSyntax.Stamp, s, new Reverse()));
        Assert.Equal("<ab01>", Write(PdfObjectSyntax.Stamp, s));
    }

    [Fact]
    public void Cross_reference_offsets_are_padded_to_ten_digits()
    {
        var b = new ByteBuffer();
        PdfXref.Padded(b, 0, 10).Sp();
        PdfXref.Padded(b, 1234567, 10).Sp();
        PdfXref.Padded(b, 12345678901, 10).Sp();
        PdfXref.Padded(b, 3, 5);
        Assert.Equal("0000000000 0001234567 12345678901 00003", Encoding.ASCII.GetString(b.Span));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Non_finite_reals_are_written_as_zero_by_every_syntax(double v)
    {
        Assert.Equal("0", Write(PdfObjectSyntax.Rewrite, PdfNumber.Real(v)));
        Assert.Equal("0", Write(PdfObjectSyntax.Stamp, PdfNumber.Real(v)));
        Assert.Equal("0", Write(PdfObjectSyntax.Update, PdfNumber.Real(v)));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(1e30f)]
    public void ByteBuffer_numbers_stay_valid_for_non_finite_and_huge_values(float v)
    {
        var b = new ByteBuffer();
        b.Num(v).Sp().Num6(v);
        Assert.Matches(@"^-?\d+(\.\d+)? -?\d+(\.\d+)?$", Encoding.ASCII.GetString(b.Span));
        if (!float.IsFinite(v)) Assert.Equal("0 0", Encoding.ASCII.GetString(b.Span));
    }

    [Fact]
    public void A_name_read_and_written_again_keeps_its_bytes()
    {
        // The parser keeps one char per byte, so names with raw high bytes, escapes and UTF-8 must come out the same.
        byte[] original = Encoding.Latin1.GetBytes("/A#20b#23#C3#A9#E9#FF");
        var name = (PdfName)new PdfLexer(original).ReadObject()!;
        foreach (var syntax in new[] { PdfObjectSyntax.Rewrite, PdfObjectSyntax.Stamp, PdfObjectSyntax.Update })
            Assert.Equal("/A#20b#23#C3#A9#E9#FF", Write(syntax, name));
    }

    [Fact]
    public void A_rented_buffer_grows_and_is_empty_after_it_is_returned()
    {
        var b = ByteBuffer.Rent(16);
        for (int i = 0; i < 100; i++) b.Int(i % 10);
        Assert.Equal(string.Concat(Enumerable.Repeat("0123456789", 10)), Encoding.ASCII.GetString(b.Span));
        b.Return();
        Assert.Equal(0, b.Length);
        b.Return();
        b.Ascii("still usable");
        Assert.Equal("still usable", Encoding.ASCII.GetString(b.Span));
    }

    [Fact]
    public void Object_ids_that_were_never_written_are_free_entries()
    {
        var b = new ByteBuffer();
        var xref = new PdfXref();
        b.Ascii("%PDF-1.7\n");
        xref.Begin(b, 1);
        xref.Begin(b, 3);
        Assert.Equal(2, xref.FirstMissing(4));
        Assert.Equal(-1, xref.FirstMissing(2));
        var t = new ByteBuffer();
        xref.WriteTable(t, 4);
        var lines = Encoding.ASCII.GetString(t.Span).Split('\n');
        Assert.EndsWith(" f ", lines[4]);
        Assert.EndsWith(" n ", lines[3]);
        Assert.EndsWith(" n ", lines[5]);
    }
}
