using System.Globalization;
using System.Text;
using s2industries.ZUGFeRD;
using UglyToad.PdfPig;

namespace MatPaper.Services;

/// <summary>One invoice position (for the generated PDF).</summary>
public sealed record InvoiceLine(string Name, decimal Quantity, string? Unit, decimal UnitPrice, decimal? Total, decimal TaxPercent);

/// <summary>One VAT breakdown row.</summary>
public sealed record InvoiceTax(decimal Percent, decimal Basis, decimal Amount);

/// <summary>Structured invoice data extracted from an XRechnung XML or a ZUGFeRD/Factur-X PDF.</summary>
public sealed record InvoiceData(
    string? SellerName,
    string? InvoiceNumber,
    DateTime? InvoiceDate,
    string? TypeCode,
    string? Currency)
{
    public decimal? NetAmount { get; init; }
    public decimal? TaxAmount { get; init; }
    public decimal? GrossAmount { get; init; }

    /// <summary>What is still to be paid (gross minus prepayments).</summary>
    public decimal? DueAmount { get; init; }
    public DateTime? DueDate { get; init; }
    public string? SellerVatId { get; init; }
    public string? SellerIban { get; init; }
    public string? SellerAddress { get; init; }
    public string? BuyerName { get; init; }
    public string? BuyerAddress { get; init; }

    /// <summary>The buyer's reference; for German public bodies the Leitweg-ID.</summary>
    public string? BuyerReference { get; init; }
    public string? PaymentTerms { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<InvoiceLine> Lines { get; init; } = Array.Empty<InvoiceLine>();
    public IReadOnlyList<InvoiceTax> Taxes { get; init; } = Array.Empty<InvoiceTax>();

    /// <summary>A short human-readable summary, appended to the OCR text so it is full-text searchable.</summary>
    public string ToSummary()
    {
        var parts = new List<string> { "E-Rechnung" };
        if (!string.IsNullOrWhiteSpace(SellerName)) parts.Add(SellerName!);
        if (!string.IsNullOrWhiteSpace(InvoiceNumber)) parts.Add("Rechnungsnummer " + InvoiceNumber);
        if (InvoiceDate.HasValue) parts.Add(InvoiceDate.Value.ToString("yyyy-MM-dd"));
        if (GrossAmount.HasValue) parts.Add(GrossAmount.Value.ToString("0.00", CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(Currency) ? "" : " " + Currency));
        else if (!string.IsNullOrWhiteSpace(Currency)) parts.Add(Currency!);
        if (!string.IsNullOrWhiteSpace(SellerVatId)) parts.Add("USt-IdNr. " + SellerVatId);
        if (!string.IsNullOrWhiteSpace(SellerIban)) parts.Add("IBAN " + SellerIban);
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Reads structured e-invoice data (XRechnung / ZUGFeRD / Factur-X) from a file:
/// standalone <c>.xml</c> is parsed directly; for <c>.pdf</c> the embedded invoice XML
/// attachment (factur-x.xml / zugferd-invoice.xml / xrechnung.xml) is extracted first.
/// Best-effort: returns <c>null</c> when the file is not a structured e-invoice.
/// </summary>
public sealed class InvoiceDataExtractor
{
    private readonly ILogger<InvoiceDataExtractor> _logger;

    public InvoiceDataExtractor(ILogger<InvoiceDataExtractor> logger) => _logger = logger;

    public InvoiceData? TryExtract(string absolutePath, string extension)
    {
        try
        {
            var ext = (extension ?? string.Empty).ToLowerInvariant();

            if (ext == ".xml")
            {
                using var stream = File.OpenRead(absolutePath);
                return FromDescriptor(InvoiceDescriptor.Load(stream));
            }

            if (ext == ".pdf")
            {
                return FromPdf(absolutePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No structured invoice data extracted from {Path}", absolutePath);
        }

        return null;
    }

    /// <summary>Parses invoice XML held in memory (an uploaded or imported XRechnung); null when it is not an e-invoice.</summary>
    public InvoiceData? TryExtractFromXml(byte[] xml)
    {
        try
        {
            using var ms = new MemoryStream(xml);
            return FromDescriptor(InvoiceDescriptor.Load(ms));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The XML is not a structured e-invoice.");
            return null;
        }
    }

    private InvoiceData? FromPdf(string absolutePath)
    {
        var bytes = File.ReadAllBytes(absolutePath);
        using var pdf = PdfDocument.Open(bytes);

        if (!pdf.Advanced.TryGetEmbeddedFiles(out var files) || files is null)
        {
            return null;
        }

        foreach (var file in files)
        {
            var name = (file.Name ?? string.Empty).ToLowerInvariant();
            if (!name.EndsWith(".xml"))
            {
                continue;
            }

            byte[] content;
            try
            {
                content = file.Bytes.ToArray();
            }
            catch
            {
                continue;
            }

            var head = Encoding.UTF8.GetString(content, 0, Math.Min(content.Length, 4000));
            var looksLikeInvoice = name.Contains("factur") || name.Contains("zugferd") || name.Contains("xrechnung")
                || head.Contains("CrossIndustryInvoice") || head.Contains(":Invoice");
            if (!looksLikeInvoice)
            {
                continue;
            }

            try
            {
                using var ms = new MemoryStream(content);
                var data = FromDescriptor(InvoiceDescriptor.Load(ms));
                if (data is not null)
                {
                    return data;
                }
            }
            catch
            {
                // try the next embedded file
            }
        }

        return null;
    }

    private static InvoiceData? FromDescriptor(InvoiceDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            return null;
        }

        var seller = descriptor.Seller?.Name?.Trim();
        var number = descriptor.InvoiceNo?.Trim();

        // Require at least a seller or an invoice number to treat it as a real e-invoice.
        if (string.IsNullOrWhiteSpace(seller) && string.IsNullOrWhiteSpace(number))
        {
            return null;
        }

        var vat = descriptor.SellerTaxRegistration?
            .Where(r => !string.IsNullOrWhiteSpace(r.No))
            .OrderBy(r => r.SchemeID == TaxRegistrationSchemeID.VA ? 0 : 1)
            .Select(r => r.No!.Trim())
            .FirstOrDefault();

        var iban = descriptor.CreditorBankAccounts?
            .Select(a => a.IBAN?.Replace(" ", string.Empty).Trim())
            .FirstOrDefault(i => !string.IsNullOrWhiteSpace(i));

        var terms = descriptor.PaymentTerms?.FirstOrDefault();

        return new InvoiceData(seller, number, descriptor.InvoiceDate, descriptor.Type.ToString(), descriptor.Currency.ToString())
        {
            NetAmount = descriptor.TaxBasisAmount ?? descriptor.LineTotalAmount,
            TaxAmount = descriptor.TaxTotalAmount,
            GrossAmount = descriptor.GrandTotalAmount,
            DueAmount = descriptor.DuePayableAmount,
            DueDate = terms?.DueDate,
            SellerVatId = vat,
            SellerIban = iban,
            SellerAddress = Address(descriptor.Seller),
            BuyerName = descriptor.Buyer?.Name?.Trim(),
            BuyerAddress = Address(descriptor.Buyer),
            BuyerReference = descriptor.ReferenceOrderNo?.Trim(),
            PaymentTerms = terms?.Description?.Trim(),
            Notes = descriptor.Notes?
                .Select(n => n.Content?.Trim())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c!)
                .ToList() ?? new List<string>(),
            Lines = descriptor.TradeLineItems?
                .Select(l => new InvoiceLine(
                    string.IsNullOrWhiteSpace(l.Name) ? (l.Description ?? string.Empty) : l.Name,
                    l.BilledQuantity,
                    l.UnitCode?.ToString(),
                    l.NetUnitPrice,
                    l.LineTotalAmount,
                    l.TaxPercent))
                .ToList() ?? new List<InvoiceLine>(),
            Taxes = descriptor.Taxes?
                .Select(t => new InvoiceTax(t.Percent, t.BasisAmount, t.TaxAmount))
                .ToList() ?? new List<InvoiceTax>()
        };
    }

    private static string? Address(Party? party)
    {
        if (party is null)
        {
            return null;
        }

        var street = string.Join(" ", new[] { party.Street, party.Street2 }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var city = string.Join(" ", new[] { party.Postcode, party.City }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var text = string.Join(", ", new[] { street, city }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return text.Length == 0 ? null : text;
    }
}
