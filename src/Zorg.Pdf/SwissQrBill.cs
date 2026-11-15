using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace Zorg.Pdf;

/// <summary>
/// A Swiss QR-bill: the payment part and receipt (SIX, Swiss Implementation Guidelines for the QR-bill, v2.4), drawn
/// across the bottom 105 mm of an A4 page with the Swiss QR code. Every field is checked against the guidelines before
/// anything is written; content that follows goes on the next page, and that page's footer is left out.
/// </summary>
/// <example>
/// <code>
/// var bill = new SwissQrBill("CH44 3199 9123 0008 8901 2", new SwissQrAddress("Robert Schneider AG", "2501", "Biel/Bienne") { Street = "Rue du Lac", HouseNumber = "1268" })
/// {
///     Amount = 1949.75m,
///     Reference = "210000000003139471430009017",
///     Debtor = new SwissQrAddress("Pia-Maria Rutschmann-Schnyder", "9400", "Rorschach") { Street = "Grosse Marktgasse", HouseNumber = "28" },
///     Message = "Order of 15 June 2026",
/// };
/// </code>
/// </example>
public sealed record SwissQrBill : Block
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal SwissQrBill(string account, SwissQrAddress creditor, decimal? amount = null, string currency = "CHF", SwissQrAddress? debtor = null, string? reference = null, string? message = null, string? billInformation = null, string? language = null, string? bind = null, string? style = null, bool keepWithNext = false, string? id = null) =>
        (Account, Creditor, Amount, Currency, Debtor, Reference, Message, BillInformation, Language, Bind, Style, KeepWithNext, Id) =
        (account, creditor, amount, currency, debtor, reference, message, billInformation, language, bind, style, keepWithNext, id);

    /// <summary>A bill to an account.</summary>
    /// <param name="account">The creditor's IBAN or QR-IBAN, CH or LI; spaces are ignored.</param>
    /// <param name="creditor">Who is paid.</param>
    [SetsRequiredMembers]
    public SwissQrBill(string account, SwissQrAddress creditor) => (Account, Creditor) = (account, creditor);

    /// <summary>The creditor's IBAN or QR-IBAN: 21 characters starting CH or LI. Spaces are ignored.</summary>
    [JsonRequired]
    public required string Account { get; init; }

    /// <summary>Who is paid.</summary>
    [JsonRequired]
    public required SwissQrAddress Creditor { get; init; }

    /// <summary>0.01 to 999 999 999.99. Null: the payer fills it in, in a marked box.</summary>
    public decimal? Amount { get; init; }

    /// <summary>"CHF" (the default) or "EUR".</summary>
    public string Currency { get; init; } = "CHF";

    /// <summary>Who pays. Null: a marked box for the payer to write in.</summary>
    public SwissQrAddress? Debtor { get; init; }

    /// <summary>
    /// A QR reference (27 digits) with a QR-IBAN, or an ISO 11649 creditor reference ("RF…") or none with an IBAN.
    /// Spaces are ignored. <see cref="SwissQrReferences"/> makes either.
    /// </summary>
    public string? Reference { get; init; }

    /// <summary>A message for the creditor, such as the invoice number; up to 140 characters with <see cref="BillInformation"/>.</summary>
    public string? Message { get; init; }

    /// <summary>Structured billing information for the creditor's software, starting with "//" (such as Swico's "//S1/…").</summary>
    public string? BillInformation { get; init; }

    /// <summary>"de", "fr", "it", "en" or "rm" for the headings. Null: the document's language if it's one of them, else "en".</summary>
    public string? Language { get; init; }

    /// <summary>
    /// A path in the template model whose value is an object with the fields that change from bill to bill, such as
    /// <c>{ "amount": 793.00, "reference": "…", "debtor": { … } }</c>. Its fields fill in or replace the bill's own;
    /// the bill is checked once they're in.
    /// </summary>
    public string? Bind { get; init; }

    /// <summary>The Swiss QR code's payload, checked against the guidelines.</summary>
    /// <returns>The payload: one element per line, separated by LF, ending with the trailer "EPD" or the billing information.</returns>
    /// <exception cref="ArgumentException">A field breaks the guidelines; the message says which and why.</exception>
    public string ToPayload()
    {
        if (SwissQrRules.Check(this).FirstOrDefault() is { Message: { } problem })
        {
            throw new ArgumentException(problem);
        }

        return SwissQrRules.Build(this);
    }
}

/// <summary>A structured address on a Swiss QR-bill (address type S, the only one the guidelines allow).</summary>
/// <param name="Name">The name or company, up to 70 characters.</param>
/// <param name="PostalCode">The postal code without a country prefix, up to 16 characters.</param>
/// <param name="Town">The town, up to 35 characters.</param>
public sealed record SwissQrAddress(string Name, string PostalCode, string Town)
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    internal SwissQrAddress(string name, string postalCode, string town, string? street = null, string? houseNumber = null, string country = "CH")
        : this(name, postalCode, town) => (Street, HouseNumber, Country) = (street, houseNumber, country);

    /// <summary>The street or P.O. box, up to 70 characters.</summary>
    public string? Street { get; init; }

    /// <summary>The building number, up to 16 characters.</summary>
    public string? HouseNumber { get; init; }

    /// <summary>The ISO 3166-1 country code. "CH" by default.</summary>
    public string Country { get; init; } = "CH";
}

/// <summary>References for Swiss QR-bills: QR references with their check digit, and ISO 11649 creditor references.</summary>
/// <example>
/// <code>
/// string qr = SwissQrReferences.QrReference("21000000000313947143000901");   // "210000000003139471430009017"
/// string rf = SwissQrReferences.CreditorReference("539007547034");          // "RF18539007547034"
/// </code>
/// </example>
public static class SwissQrReferences
{
    private static readonly int[] Table = [0, 9, 4, 6, 8, 2, 7, 1, 3, 5];

    /// <summary>A QR reference: the digits, left-padded with zeros to 26, and the recursive modulo 10 check digit.</summary>
    /// <param name="digits">Up to 26 digits; spaces are ignored.</param>
    /// <returns>27 digits.</returns>
    public static string QrReference(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);
        digits = SwissQrRules.Compact(digits);
        if (digits.Length is 0 or > 26 || !digits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("A QR reference is made from 1 to 26 digits.", nameof(digits));
        }

        digits = digits.PadLeft(26, '0');
        return digits + CheckDigit(digits).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>True for 27 digits, not all zeros, whose last is the check digit of the others.</summary>
    /// <param name="reference">The reference; spaces are ignored.</param>
    public static bool IsValidQrReference(string? reference)
    {
        string value = SwissQrRules.Compact(reference ?? "");
        return value.Length == 27 && value.All(char.IsAsciiDigit) && value.Any(c => c != '0') && CheckDigit(value[..26]) == value[26] - '0';
    }

    /// <summary>An ISO 11649 creditor reference: "RF", two check digits (modulo 97-10), then the reference.</summary>
    /// <param name="reference">1 to 21 letters and digits; spaces are ignored.</param>
    /// <returns>The reference, upper case and without spaces.</returns>
    public static string CreditorReference(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        reference = SwissQrRules.Compact(reference).ToUpperInvariant();
        if (reference.Length is 0 or > 21 || !reference.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("A creditor reference is made from 1 to 21 letters and digits.", nameof(reference));
        }

        int remainder = SwissQrRules.Mod97(reference + "RF00");
        return string.Create(CultureInfo.InvariantCulture, $"RF{98 - remainder:00}{reference}");
    }

    /// <summary>True for "RF", two check digits and 1 to 21 letters and digits that pass the modulo 97 check.</summary>
    /// <param name="reference">The reference; spaces are ignored, case doesn't matter.</param>
    public static bool IsValidCreditorReference(string? reference)
    {
        string value = SwissQrRules.Compact(reference ?? "").ToUpperInvariant();
        return value.Length is >= 5 and <= 25 && value.StartsWith("RF", StringComparison.Ordinal) && value.All(char.IsAsciiLetterOrDigit)
            && char.IsAsciiDigit(value[2]) && char.IsAsciiDigit(value[3]) && SwissQrRules.Mod97(value[4..] + value[..4]) == 1;
    }

    private static int CheckDigit(string digits)
    {
        int carry = 0;
        foreach (char c in digits)
        {
            carry = Table[(carry + (c - '0')) % 10];
        }

        return (10 - carry) % 10;
    }
}

/// <summary>The guidelines' rules for a bill, shared by validation and the payload.</summary>
internal static class SwissQrRules
{
    public static string Compact(string value) => value.Replace(" ", "", StringComparison.Ordinal);

    /// <summary>ISO 7064 modulo 97 of letters and digits, letters counting A = 10 to Z = 35.</summary>
    public static int Mod97(string value)
    {
        int remainder = 0;
        foreach (char c in value)
        {
            int n = char.IsAsciiDigit(c) ? c - '0' : char.ToUpperInvariant(c) - 'A' + 10;
            remainder = ((remainder * (n < 10 ? 10 : 100)) + n) % 97;
        }

        return remainder;
    }

    /// <summary>A QR-IBAN: its institution identifier (characters 5 to 9) is in the QR-IID range 30000 to 31999.</summary>
    public static bool IsQrIban(string account) =>
        account.Length == 21 && int.TryParse(account.AsSpan(4, 5), NumberStyles.None, CultureInfo.InvariantCulture, out int iid) && iid is >= 30000 and <= 31999;

    public static string ReferenceType(string account, string reference) =>
        IsQrIban(account) ? "QRR" : reference.Length > 0 ? "SCOR" : "NON";

    public static IEnumerable<string> AddressLines(SwissQrAddress address) =>
        ["S", address.Name, address.Street ?? "", address.HouseNumber ?? "", address.PostalCode, address.Town, address.Country];

    /// <summary>What breaks the guidelines, each with the path of the field under the bill (such as "/account").</summary>
    public static IEnumerable<(string Message, string Path)> Check(SwissQrBill bill)
    {
        string account = Compact(bill.Account ?? "").ToUpperInvariant();
        if (account.Length != 21 || !(account.StartsWith("CH", StringComparison.Ordinal) || account.StartsWith("LI", StringComparison.Ordinal)) || !EpcPayment.IbanValid(account))
        {
            yield return ("The account must be a valid Swiss or Liechtenstein IBAN or QR-IBAN: 21 characters starting CH or LI, with correct check digits.", "/account");
        }

        if (bill.Creditor is null)
        {
            yield return ("A Swiss QR-bill needs a creditor.", "/creditor");
        }
        else
        {
            foreach ((string, string) problem in Address(bill.Creditor, "/creditor"))
            {
                yield return problem;
            }
        }

        if (bill.Debtor is { } debtor)
        {
            foreach ((string, string) problem in Address(debtor, "/debtor"))
            {
                yield return problem;
            }
        }

        if (bill.Amount is { } amount && (amount < 0.01m || amount > 999_999_999.99m || decimal.Round(amount, 2) != amount))
        {
            yield return ("The amount must be 0.01 to 999 999 999.99, with at most two decimals.", "/amount");
        }

        if (bill.Currency is not ("CHF" or "EUR"))
        {
            yield return ("The currency must be \"CHF\" or \"EUR\".", "/currency");
        }

        string reference = Compact(bill.Reference ?? "");
        if (IsQrIban(account))
        {
            if (!SwissQrReferences.IsValidQrReference(reference))
            {
                yield return ("A QR-IBAN needs a QR reference: 27 digits, not all zeros, ending in its check digit. SwissQrReferences.QrReference makes one.", "/reference");
            }

            if (bill.Currency == "EUR")
            {
                yield return ("A QR-IBAN with a QR reference is for CHF only. Bill in EUR to an ordinary IBAN, with a creditor reference or none.", "/currency");
            }
        }
        else if (reference.Length > 0 && !SwissQrReferences.IsValidCreditorReference(reference))
        {
            yield return ("With an ordinary IBAN the reference must be an ISO 11649 creditor reference (\"RF\" and its check digits) or none. QR references need a QR-IBAN.", "/reference");
        }

        if ((bill.Message?.Length ?? 0) + (bill.BillInformation?.Length ?? 0) > 140)
        {
            yield return ("The message and billing information are at most 140 characters together.", "/message");
        }

        if (bill.BillInformation is { Length: > 0 } information && !information.StartsWith("//", StringComparison.Ordinal))
        {
            yield return ("Billing information starts with \"//\" and its syntax's code, such as \"//S1/10/…\".", "/billInformation");
        }

        if (bill.Language is not (null or "de" or "fr" or "it" or "en" or "rm"))
        {
            yield return ("The language must be \"de\", \"fr\", \"it\", \"en\" or \"rm\".", "/language");
        }

        foreach ((string? text, string path) in (IEnumerable<(string?, string)>)[(bill.Message, "/message"), (bill.BillInformation, "/billInformation")])
        {
            if (text is not null && BadCharacter(text) is { } bad)
            {
                yield return ($"The character U+{(int)bad:X4} isn't in the QR-bill's character set (Latin, with Ș, ș, Ț, ț and €).", path);
            }
        }

        if (bill.Creditor is not null && Build(bill).Length is > 997 and var length)
        {
            yield return ($"The QR code's data is {length} characters, more than the 997 the guidelines allow. Shorten the message.", "");
        }
    }

    /// <summary>The payload, without checks: one element per line, LF between them, no separator after the last.</summary>
    public static string Build(SwissQrBill bill)
    {
        string account = Compact(bill.Account ?? "").ToUpperInvariant();
        string reference = Compact(bill.Reference ?? "").ToUpperInvariant();
        var lines = new List<string> { "SPC", "0200", "1", account };
        lines.AddRange(AddressLines(bill.Creditor));
        lines.AddRange(Enumerable.Repeat("", 7)); // The ultimate creditor: reserved, always empty.
        lines.Add(bill.Amount is { } amount ? amount.ToString("0.00", CultureInfo.InvariantCulture) : "");
        lines.Add(bill.Currency);
        lines.AddRange(bill.Debtor is { } debtor ? AddressLines(debtor) : Enumerable.Repeat("", 7));
        lines.Add(ReferenceType(account, reference));
        lines.Add(reference);
        lines.Add(bill.Message ?? "");
        lines.Add("EPD");
        if (!string.IsNullOrEmpty(bill.BillInformation))
        {
            lines.Add(bill.BillInformation);
        }

        return string.Join('\n', lines);
    }

    private static IEnumerable<(string Message, string Path)> Address(SwissQrAddress address, string path)
    {
        foreach ((string? value, string name, int max, bool required) in (IEnumerable<(string?, string, int, bool)>)[(address.Name, "name", 70, true), (address.Street, "street", 70, false), (address.HouseNumber, "houseNumber", 16, false), (address.PostalCode, "postalCode", 16, true), (address.Town, "town", 35, true)])
        {
            if (string.IsNullOrWhiteSpace(value) ? required : value.Length > max)
            {
                yield return (required ? $"The {name} is required, up to {max} characters." : $"The {name} is at most {max} characters.", $"{path}/{name}");
            }
            else if (value is not null && BadCharacter(value) is { } bad)
            {
                yield return ($"The character U+{(int)bad:X4} isn't in the QR-bill's character set (Latin, with Ș, ș, Ț, ț and €).", $"{path}/{name}");
            }
        }

        if (address.Country is not { Length: 2 } country || !country.All(char.IsAsciiLetterUpper))
        {
            yield return ("The country is a two-letter ISO 3166-1 code in capitals, such as \"CH\".", $"{path}/country");
        }
    }

    /// <summary>The first character outside the guidelines' set (4.1.1), or null.</summary>
    private static char? BadCharacter(string text)
    {
        foreach (char c in text)
        {
            if (!(c is >= ' ' and <= '~' or >= ' ' and <= 'ſ' or >= 'Ș' and <= 'ț' or '€'))
            {
                return c;
            }
        }

        return null;
    }
}
