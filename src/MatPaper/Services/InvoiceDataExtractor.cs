using System.Text;
using s2industries.ZUGFeRD;
using UglyToad.PdfPig;

namespace MatPaper.Services;

/// <summary>Structured invoice data extracted from an XRechnung XML or a ZUGFeRD/Factur-X PDF.</summary>
public sealed record InvoiceData(
    string? SellerName,
    string? InvoiceNumber,
    DateTime? InvoiceDate,
    string? TypeCode,
    string? Currency)
{
    /// <summary>A short human-readable summary, appended to the OCR text so it is full-text searchable.</summary>
    public string ToSummary()
    {
        var parts = new List<string> { "E-Rechnung" };
        if (!string.IsNullOrWhiteSpace(SellerName)) parts.Add(SellerName!);
        if (!string.IsNullOrWhiteSpace(InvoiceNumber)) parts.Add("Rechnungsnummer " + InvoiceNumber);
        if (InvoiceDate.HasValue) parts.Add(InvoiceDate.Value.ToString("yyyy-MM-dd"));
        if (!string.IsNullOrWhiteSpace(Currency)) parts.Add(Currency!);
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
        var date = descriptor.InvoiceDate;
        var type = descriptor.Type.ToString();
        var currency = descriptor.Currency.ToString();

        // Require at least a seller or an invoice number to treat it as a real e-invoice.
        if (string.IsNullOrWhiteSpace(seller) && string.IsNullOrWhiteSpace(number))
        {
            return null;
        }

        return new InvoiceData(seller, number, date, type, currency);
    }
}
