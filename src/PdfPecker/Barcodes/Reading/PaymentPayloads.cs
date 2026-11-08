using System.Globalization;

namespace PdfPecker.Barcodes.Reading;

/// <summary>
/// Reads the payment a QR code holds: an EPC credit transfer (EPC069-12) or a Swiss QR-bill. The fields are taken as
/// the code states them, without the checks <see cref="EpcPayment.ToPayload"/> and <see cref="SwissQrBill.ToPayload"/>
/// make, so a caller sees what a banking app would be given; the header and the number of lines must match the format.
/// </summary>
internal static class PaymentPayloads
{
    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    public static EpcPayment? Epc(string text)
    {
        if (!text.StartsWith("BCD", StringComparison.Ordinal)) return null;
        var lines = Lines(text);
        // Service tag, version, character set, identification, BIC, name, IBAN, then the optional fields.
        if (lines.Length < 7 || lines.Length > 12 || lines[0] != "BCD" || lines[1] is not ("001" or "002")
            || lines[2].Length != 1 || lines[2][0] is < '1' or > '8' || lines[3] is not ("SCT" or "INST")) return null;
        string Field(int i) => i < lines.Length ? lines[i].Trim() : "";
        var payment = new EpcPayment
        {
            Bic = NullIfEmpty(Field(4)),
            Name = Field(5),
            Iban = Field(6),
            Purpose = NullIfEmpty(Field(8)),
            Reference = NullIfEmpty(Field(9)),
            Text = NullIfEmpty(Field(10)),
            Information = NullIfEmpty(Field(11)),
        };
        if (payment.Name.Length == 0 || payment.Iban.Length == 0) return null;
        var amount = Field(7);
        if (amount.Length != 0)
        {
            if (!amount.StartsWith("EUR", StringComparison.Ordinal)
                || !decimal.TryParse(amount.AsSpan(3), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                return null;
            payment.Amount = value;
        }
        return payment;
    }

    public static SwissQrBill? Swiss(string text)
    {
        if (!text.StartsWith("SPC", StringComparison.Ordinal)) return null;
        var lines = Lines(text);
        // Header (3), account, creditor (7), ultimate creditor (7), amount, currency, debtor (7), reference type and
        // reference, message, trailer "EPD", then optional bill information and alternative schemes.
        if (lines.Length < 31 || lines[0] != "SPC" || !lines[1].StartsWith("02", StringComparison.Ordinal) || lines[2] != "1"
            || lines[30].Trim() != "EPD") return null;
        string Field(int i) => i < lines.Length ? lines[i].Trim() : "";
        var bill = new SwissQrBill
        {
            Account = Field(3),
            Creditor = Address(lines, 4),
            Currency = Field(19),
            Debtor = Address(lines, 20),
            Reference = NullIfEmpty(Field(28)),
            Message = NullIfEmpty(Field(29)),
            BillInformation = lines.Length > 31 ? NullIfEmpty(Field(31)) : null,
        };
        if (bill.Account.Length == 0 || bill.Creditor == null) return null;
        var amount = Field(18);
        if (amount.Length != 0)
        {
            if (!decimal.TryParse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
            bill.Amount = value;
        }
        return bill;
    }

    /// <summary>
    /// The seven address lines from <paramref name="start"/>. A structured address ("S") maps field by field; a combined
    /// one ("K"), from guideline versions before 2.3, keeps its two free address lines in the street and the town.
    /// </summary>
    private static SwissQrAddress? Address(string[] lines, int start)
    {
        string Field(int i) => lines[start + i].Trim();
        var type = Field(0);
        if (type.Length == 0 && Field(1).Length == 0) return null;
        if (type == "K")
        {
            return new SwissQrAddress { Name = Field(1), Street = NullIfEmpty(Field(2)), Town = Field(3), PostalCode = "", Country = Field(6) };
        }
        return new SwissQrAddress
        {
            Name = Field(1),
            Street = NullIfEmpty(Field(2)),
            HouseNumber = NullIfEmpty(Field(3)),
            PostalCode = Field(4),
            Town = Field(5),
            Country = Field(6),
        };
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
