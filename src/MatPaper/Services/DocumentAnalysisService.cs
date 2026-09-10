using System.Text.RegularExpressions;
using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

public sealed class AnalysisOptions
{
    /// <summary>Overwrite fields that already have a value (default: only fill empty fields).</summary>
    public bool Overwrite { get; init; }

    /// <summary>Create a correspondent from a structured e-invoice seller when none matches.</summary>
    public bool CreateMissingCorrespondents { get; init; } = true;
}

public sealed record AnalysisResult(
    bool UsedInvoiceData,
    bool ChangedType,
    bool ChangedCorrespondent,
    bool ChangedDate,
    bool ChangedInvoiceNumber)
{
    public bool AnythingChanged => ChangedType || ChangedCorrespondent || ChangedDate || ChangedInvoiceNumber;
}

/// <summary>
/// Auto-assigns a document's type, correspondent and date. Structured e-invoice data
/// (XRechnung / ZUGFeRD) takes priority; otherwise falls back to keyword/regex matching
/// against the title + OCR text (Correspondent.MatchPattern and DocumentType.MatchPattern)
/// and a date heuristic. Mutates the tracked <see cref="Document"/>; the caller saves.
/// </summary>
public sealed class DocumentAnalysisService
{
    private static readonly Regex DateRegex = new(
        @"\b(\d{4})-(\d{2})-(\d{2})\b|\b(\d{1,2})\.(\d{1,2})\.(\d{4})\b",
        RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly DocumentStorageService _storage;
    private readonly InvoiceDataExtractor _invoices;
    private readonly ILogger<DocumentAnalysisService> _logger;

    public DocumentAnalysisService(
        AppDbContext db,
        DocumentStorageService storage,
        InvoiceDataExtractor invoices,
        ILogger<DocumentAnalysisService> logger)
    {
        _db = db;
        _storage = storage;
        _invoices = invoices;
        _logger = logger;
    }

    public async Task<AnalysisResult> AnalyzeAsync(
        Document document, AnalysisOptions options, long? actingUserId, CancellationToken ct)
    {
        var invoice = TryExtractInvoice(document);
        var matchText = ((document.Title ?? string.Empty) + "\n" + (document.OcrText ?? string.Empty)).Trim();

        bool changedType = false, changedCorr = false, changedDate = false, changedInvoiceNo = false;

        if (invoice is not null)
        {
            // 1) Type => the "Rechnung" document type.
            if (CanSet(document.DocumentTypeId, options))
            {
                var invoiceType = await _db.DocumentTypes
                    .FirstOrDefaultAsync(t => t.UpdateState != UpdateState.Deleted && t.Name.ToLower() == "rechnung", ct);
                if (invoiceType is not null && document.DocumentTypeId != invoiceType.Id)
                {
                    document.DocumentTypeId = invoiceType.Id;
                    changedType = true;
                }
            }

            // 2) Date => the invoice date.
            if (invoice.InvoiceDate.HasValue && CanSet(document.DocumentDate, options))
            {
                document.DocumentDate = DateTime.SpecifyKind(invoice.InvoiceDate.Value, DateTimeKind.Utc);
                changedDate = true;
            }

            // 3) Correspondent => match (or create) from the seller name.
            if (!string.IsNullOrWhiteSpace(invoice.SellerName) && CanSet(document.CorrespondentId, options))
            {
                changedCorr = await AssignCorrespondentFromSellerAsync(document, invoice.SellerName!, options, actingUserId, ct);
            }

            // 4) Invoice number.
            if (!string.IsNullOrWhiteSpace(invoice.InvoiceNumber) && CanSet(document.InvoiceNumber, options))
            {
                document.InvoiceNumber = invoice.InvoiceNumber;
                changedInvoiceNo = true;
            }

            AppendInvoiceSummary(document, invoice);
        }
        else
        {
            // Fallback: keyword/regex matching + date heuristic.
            if (CanSet(document.CorrespondentId, options))
            {
                changedCorr = await MatchByPatternAsync<Correspondent>(matchText,
                    () => _db.Correspondents.Where(c => c.UpdateState != UpdateState.Deleted && c.MatchPattern != null && c.MatchPattern != ""),
                    c => c.MatchPattern, id => { document.CorrespondentId = id; }, ct);
            }

            if (CanSet(document.DocumentTypeId, options))
            {
                changedType = await MatchByPatternAsync<DocumentType>(matchText,
                    () => _db.DocumentTypes.Where(t => t.UpdateState != UpdateState.Deleted && t.MatchPattern != null && t.MatchPattern != ""),
                    t => t.MatchPattern, id => { document.DocumentTypeId = id; }, ct);
            }

            if (CanSet(document.DocumentDate, options))
            {
                var date = FindFirstDate(matchText);
                if (date.HasValue)
                {
                    document.DocumentDate = date;
                    changedDate = true;
                }
            }
        }

        return new AnalysisResult(invoice is not null, changedType, changedCorr, changedDate, changedInvoiceNo);
    }

    private InvoiceData? TryExtractInvoice(Document document)
    {
        if (document.StorageLocation is null || string.IsNullOrEmpty(document.RelativePath))
        {
            return null;
        }

        try
        {
            var absolute = _storage.GetAbsolutePath(document.StorageLocation, document.RelativePath);
            if (!File.Exists(absolute))
            {
                return null;
            }

            var ext = Path.GetExtension(document.OriginalFileName);
            return _invoices.TryExtract(absolute, ext);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Invoice extraction skipped for document {Id}", document.Id);
            return null;
        }
    }

    private async Task<bool> AssignCorrespondentFromSellerAsync(
        Document document, string sellerName, AnalysisOptions options, long? actingUserId, CancellationToken ct)
    {
        var seller = sellerName.Trim();
        var sellerLower = seller.ToLowerInvariant();

        var candidates = await _db.Correspondents
            .Where(c => c.UpdateState != UpdateState.Deleted)
            .ToListAsync(ct);

        // Exact, then contains, then MatchPattern.
        var match = candidates.FirstOrDefault(c => c.Name.Equals(seller, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(c => sellerLower.Contains(c.Name.ToLowerInvariant()) || c.Name.ToLowerInvariant().Contains(sellerLower))
            ?? candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.MatchPattern) && IsMatch(seller, c.MatchPattern!));

        if (match is not null)
        {
            if (document.CorrespondentId == match.Id)
            {
                return false;
            }
            document.CorrespondentId = match.Id;
            return true;
        }

        if (!options.CreateMissingCorrespondents)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var created = new Correspondent
        {
            Name = seller.Length > 200 ? seller[..200] : seller,
            UpdateState = UpdateState.Created,
            CreateDate = now,
            UpdateDate = now,
            CreateUserId = actingUserId,
            UpdateUserId = actingUserId
        };
        _db.Correspondents.Add(created);
        document.Correspondent = created; // EF sets CorrespondentId on save
        return true;
    }

    private async Task<bool> MatchByPatternAsync<T>(
        string matchText,
        Func<IQueryable<T>> query,
        Func<T, string?> pattern,
        Action<long> assign,
        CancellationToken ct) where T : BaseEntity
    {
        if (string.IsNullOrWhiteSpace(matchText))
        {
            return false;
        }

        var candidates = await query().ToListAsync(ct);
        foreach (var candidate in candidates)
        {
            var p = pattern(candidate);
            if (!string.IsNullOrWhiteSpace(p) && IsMatch(matchText, p!))
            {
                assign(candidate.Id);
                return true;
            }
        }

        return false;
    }

    private static bool IsMatch(string text, string pattern)
    {
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        catch
        {
            return text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool CanSet<T>(T? current, AnalysisOptions options) where T : struct
        => options.Overwrite || current is null;

    private static bool CanSet(string? current, AnalysisOptions options)
        => options.Overwrite || string.IsNullOrWhiteSpace(current);

    private static DateTime? FindFirstDate(string text)
    {
        foreach (Match m in DateRegex.Matches(text))
        {
            try
            {
                if (m.Groups[1].Success)
                {
                    var y = int.Parse(m.Groups[1].Value);
                    var mo = int.Parse(m.Groups[2].Value);
                    var d = int.Parse(m.Groups[3].Value);
                    if (IsPlausible(y, mo, d)) return new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Utc);
                }
                else
                {
                    var d = int.Parse(m.Groups[4].Value);
                    var mo = int.Parse(m.Groups[5].Value);
                    var y = int.Parse(m.Groups[6].Value);
                    if (IsPlausible(y, mo, d)) return new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Utc);
                }
            }
            catch
            {
                // ignore malformed candidate
            }
        }
        return null;
    }

    private static bool IsPlausible(int year, int month, int day)
        => year is >= 1990 and <= 2100 && month is >= 1 and <= 12 && day is >= 1 and <= 31;

    private static void AppendInvoiceSummary(Document document, InvoiceData invoice)
    {
        var summary = invoice.ToSummary();
        var existing = document.OcrText ?? string.Empty;
        if (existing.Contains(summary, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        document.OcrText = string.IsNullOrWhiteSpace(existing) ? summary : existing + "\n" + summary;
    }
}
