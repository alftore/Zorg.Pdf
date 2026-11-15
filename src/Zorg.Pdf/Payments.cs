using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Zorg.Pdf;

/// <summary>
/// An EPC QR code's payload (European Payments Council, "Quick Response Code: guidelines to enable data capture for the
/// initiation of a SEPA credit transfer", version 002), also called GiroCode: a banking app reads the payee, IBAN,
/// amount and reference from it. Put <see cref="ToString"/> in a <see cref="Barcode"/> of kind <see cref="BarcodeKind.Qr"/>
/// with error correction M.
/// </summary>
/// <example>
/// <code>
/// EpcPayment epc = EpcPayment.Create("ACME GmbH", "DE02120300000000202051", 1190.00m, "Invoice 2026-0042");
/// var qr = new Barcode(BarcodeKind.Qr, epc.ToString()) { Size = Length.Cm(3), Alt = "Payment QR code" };
/// </code>
/// </example>
public sealed record EpcPayment
{
    private EpcPayment()
    {
    }

    /// <summary>The payee's name, up to 70 characters.</summary>
    public required string Name { get; init; }

    /// <summary>The payee's IBAN, without spaces.</summary>
    public required string Iban { get; init; }

    /// <summary>The amount in euro, 0.01 to 999 999 999.99; null to let the payer fill it in.</summary>
    public decimal? Amount { get; init; }

    /// <summary>The bank's BIC, optional within the EEA.</summary>
    public string? Bic { get; init; }

    /// <summary>A structured creditor reference (ISO 11649, "RF…"); set this or <see cref="Text"/>.</summary>
    public string? Reference { get; init; }

    /// <summary>Text for the payee, up to 140 characters; set this or <see cref="Reference"/>.</summary>
    public string? Text { get; init; }

    /// <summary>A payment, checked: the IBAN's check digits, the name's and text's lengths, the amount's range.</summary>
    /// <param name="name">The payee's name.</param>
    /// <param name="iban">The payee's IBAN; spaces are removed.</param>
    /// <param name="amount">The amount in euro, or null.</param>
    /// <param name="text">Text for the payee (an invoice number), or an "RF" creditor reference.</param>
    /// <param name="bic">The BIC, or null.</param>
    /// <returns>The payment.</returns>
    /// <exception cref="ArgumentException">Something is out of the guidelines' bounds; the message says what.</exception>
    public static EpcPayment Create(string name, string iban, decimal? amount, string? text = null, string? bic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(iban);
        iban = iban.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (name.Length > 70)
        {
            throw new ArgumentException("The payee's name is at most 70 characters.", nameof(name));
        }

        if (!IbanValid(iban))
        {
            throw new ArgumentException($"\"{iban}\" isn't a valid IBAN: its check digits don't match.", nameof(iban));
        }

        if (amount is { } a && (a < 0.01m || a > 999_999_999.99m || decimal.Round(a, 2) != a))
        {
            throw new ArgumentException("The amount is 0.01 to 999 999 999.99 euro, with at most two decimals.", nameof(amount));
        }

        bool reference = text is not null && text.StartsWith("RF", StringComparison.Ordinal) && text.Length <= 25 && IbanStyleValid(text);
        if (!reference && text is { Length: > 140 })
        {
            throw new ArgumentException("The text for the payee is at most 140 characters.", nameof(text));
        }

        if (bic is { } b && b.Length is not (8 or 11))
        {
            throw new ArgumentException("A BIC has 8 or 11 characters.", nameof(bic));
        }

        return new EpcPayment { Name = name, Iban = iban, Amount = amount, Bic = bic, Reference = reference ? text : null, Text = reference ? null : text };
    }

    /// <summary>The payload: "BCD", version 002, UTF-8, "SCT", then the fields one per line.</summary>
    /// <returns>The text to put in the QR code.</returns>
    public override string ToString()
    {
        string[] lines =
        [
            "BCD", "002", "1", "SCT", Bic ?? "", Name, Iban,
            Amount is { } a ? "EUR" + a.ToString("0.00", CultureInfo.InvariantCulture) : "",
            "", Reference ?? "", Text ?? "",
        ];
        int last = lines.Length - 1;
        while (lines[last].Length == 0)
        {
            last--;
        }

        return string.Join('\n', lines, 0, last + 1);
    }

    /// <summary>ISO 13616: the IBAN, with its first four characters moved to the end and letters as numbers, is 1 mod 97.</summary>
    internal static bool IbanValid(string iban) =>
        iban.Length is >= 15 and <= 34 && char.IsAsciiLetterUpper(iban[0]) && char.IsAsciiLetterUpper(iban[1]) && IbanStyleValid(iban);

    internal static bool IbanStyleValid(string value)
    {
        var digits = new StringBuilder();
        foreach (char c in value[4..] + value[..4])
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
            else if (char.IsAsciiLetterUpper(c))
            {
                digits.Append((c - 'A' + 10).ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                return false;
            }
        }

        return BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture) % 97 == 1;
    }
}

/// <summary>
/// Norwegian KID numbers (kundeidentifikasjon), the reference on an invoice that lets the payee match a payment:
/// digits followed by a check digit, by the MOD10 (Luhn) or MOD11 method the payee's agreement with its bank says.
/// </summary>
/// <example>
/// <code>
/// string kid = Kid.Create("2026004217", KidMethod.Mod10);   // "20260042177"
/// bool ok = Kid.IsValid(kid, KidMethod.Mod10);
/// </code>
/// </example>
public static class Kid
{
    /// <summary>The digits with their check digit; MOD11 can give '-', which banks accept, when the remainder is 1.</summary>
    /// <param name="digits">The customer or invoice number, digits only, 1 to 24.</param>
    /// <param name="method">MOD10 or MOD11, as agreed with the bank.</param>
    /// <returns>The KID number.</returns>
    public static string Create(string digits, KidMethod method)
    {
        ArgumentNullException.ThrowIfNull(digits);
        if (digits.Length is < 1 or > 24 || digits.AsSpan().IndexOfAnyExceptInRange('0', '9') >= 0)
        {
            throw new ArgumentException("A KID is built from 1 to 24 digits.", nameof(digits));
        }

        return digits + CheckDigit(digits, method);
    }

    /// <summary>Whether a KID's last character is the right check digit for the method.</summary>
    /// <param name="kid">The KID number, 2 to 25 characters.</param>
    /// <param name="method">MOD10 or MOD11.</param>
    /// <returns>True when it checks out.</returns>
    public static bool IsValid(string kid, KidMethod method) =>
        kid is { Length: >= 2 and <= 25 } && kid.AsSpan(0, kid.Length - 1).IndexOfAnyExceptInRange('0', '9') < 0 && CheckDigit(kid[..^1], method) == kid[^1];

    private static char CheckDigit(string digits, KidMethod method)
    {
        int sum = 0;
        for (int i = digits.Length - 1, position = 0; i >= 0; i--, position++)
        {
            int d = digits[i] - '0';
            if (method == KidMethod.Mod10)
            {
                int product = d * (position % 2 == 0 ? 2 : 1); // Weights 2, 1, 2, 1 … from the right.
                sum += product > 9 ? product - 9 : product;
            }
            else
            {
                sum += d * (2 + (position % 6)); // Weights 2 to 7, repeating from the right.
            }
        }

        if (method == KidMethod.Mod10)
        {
            return (char)('0' + ((10 - (sum % 10)) % 10));
        }

        int remainder = sum % 11;
        return remainder switch { 0 => '0', 1 => '-', _ => (char)('0' + (11 - remainder)) };
    }
}

/// <summary>The check-digit method of a <see cref="Kid"/> number.</summary>
public enum KidMethod
{
    /// <summary>MOD10 (Luhn): weights 2 and 1 from the right, digits of products summed.</summary>
    Mod10,

    /// <summary>MOD11: weights 2 to 7 from the right; a remainder of 1 gives '-'.</summary>
    Mod11,
}
