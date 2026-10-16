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
        => Assert.Equal("<</Type /A#20b#23 /S <AB01> /N [-3 1.25 0 NaN] /R 12 1 R /E <<>> /B true >>", Write(PdfObjectSyntax.Update, Sample()));

    [Fact]
    public void Names_beyond_Latin1_are_UTF8_only_in_updates()
    {
        var name = PdfName.Get("éĀ");
        Assert.Equal("/#E9#00", Write(PdfObjectSyntax.Rewrite, name));
        Assert.Equal("/#C3#A9#C4#80", Write(PdfObjectSyntax.Update, name));
        Assert.Equal("/#E9", Write(PdfObjectSyntax.Update, PdfName.Get("é")));
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
}
