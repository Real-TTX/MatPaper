using System.Text.RegularExpressions;

namespace MatPaper.Services.Barcodes;

/// <summary>
/// What the barcode features of MatPaper are set to: whether pages are searched for codes at all, how the number of
/// the paper original is written in a code (an "archive serial number", ASN: <c>ASN00123</c>) and what a separator
/// sheet says. Plain values, so the engine can be used - and tested - without the rest of the app.
/// </summary>
public sealed record BarcodeOptions
{
    /// <summary>Look for codes on incoming PDFs and images.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The letters in front of the number in a code (a label says <c>ASN00123</c>).</summary>
    public string AsnPrefix { get; init; } = "ASN";

    /// <summary>How many digits a number gets in a code and on a label (00123).</summary>
    public int AsnDigits { get; init; } = 5;

    /// <summary>A page with the separator code starts a new document: the scan of a whole stack is split there.</summary>
    public bool SplitAtSeparators { get; init; } = true;

    /// <summary>What the code on a separator sheet says (the value Paperless uses is the default).</summary>
    public string SeparatorText { get; init; } = "PATCHT";

    /// <summary>Also read small labels at a higher resolution (slower).</summary>
    public bool HighResolution { get; init; }

    /// <summary>At most this many pages of a PDF are searched for separators.</summary>
    public int MaxPages { get; init; } = 250;

    /// <summary>The number is looked for on this many pages from the start (the label is on the first sheet).</summary>
    public int AsnPages { get; init; } = 1;

    /// <summary>The text of a code for a number: prefix and the number with leading zeros.</summary>
    public string AsnText(long number)
    {
        var digits = Math.Clamp(AsnDigits, 1, 12);
        return Prefix + number.ToString().PadLeft(digits, '0');
    }

    /// <summary>The text of a code for a number, as a person reads it on a label (<c>ASN 00123</c>).</summary>
    public string AsnReadable(long number)
    {
        var digits = Math.Clamp(AsnDigits, 1, 12);
        return Prefix + " " + number.ToString().PadLeft(digits, '0');
    }

    /// <summary>The number in the text of a code (<c>ASN00123</c>, <c>asn 123</c> ...), or null when it is no number code.</summary>
    public long? ParseAsn(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var match = Regex.Match(code, "^\\s*" + Regex.Escape(Prefix) + "[\\s\\-_:]*(\\d{1,12})\\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        return long.TryParse(match.Groups[1].Value, out var number) && number > 0 ? number : null;
    }

    public bool IsSeparator(string? code)
        => !string.IsNullOrWhiteSpace(SeparatorText) && string.Equals(code?.Trim(), SeparatorText.Trim(), StringComparison.OrdinalIgnoreCase);

    private string Prefix => string.IsNullOrWhiteSpace(AsnPrefix) ? "ASN" : AsnPrefix.Trim();
}
