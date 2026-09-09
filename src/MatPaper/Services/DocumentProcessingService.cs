using System.Globalization;
using System.Text.RegularExpressions;
using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>
/// Background worker that processes queued documents: text extraction / OCR, thumbnail
/// generation, correspondent auto-assignment and a document-date heuristic. On startup it
/// re-enqueues every pending document so processing survives restarts. A single document
/// failure never kills the loop.
/// </summary>
public sealed class DocumentProcessingService : BackgroundService
{
    private static readonly TimeSpan DateMatchTimeout = TimeSpan.FromSeconds(1);

    // dd.MM.yyyy or d.M.yyyy, and yyyy-MM-dd.
    private static readonly Regex DateRegex = new(
        @"\b(\d{1,2}\.\d{1,2}\.\d{4}|\d{4}-\d{2}-\d{2})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        DateMatchTimeout);

    private static readonly string[] DateFormats =
    {
        "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd"
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DocumentProcessingQueue _queue;
    private readonly ILogger<DocumentProcessingService> _logger;

    public DocumentProcessingService(
        IServiceScopeFactory scopeFactory,
        DocumentProcessingQueue queue,
        ILogger<DocumentProcessingService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverPendingAsync(stoppingToken);

        await foreach (var documentId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(documentId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error while processing document {DocumentId}", documentId);
                await TryMarkFailedAsync(documentId, stoppingToken);
            }
        }
    }

    private async Task RecoverPendingAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var pendingIds = await db.Documents
                .AsNoTracking()
                .Where(d => d.OcrState == OcrState.Pending && d.UpdateState != UpdateState.Deleted)
                .Select(d => d.Id)
                .ToListAsync(ct);

            foreach (var id in pendingIds)
            {
                _queue.Enqueue(id);
            }

            if (pendingIds.Count > 0)
            {
                _logger.LogInformation("Re-enqueued {Count} pending document(s) after startup", pendingIds.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover pending documents on startup");
        }
    }

    private async Task ProcessAsync(long documentId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var storage = sp.GetRequiredService<DocumentStorageService>();
        var extractor = sp.GetRequiredService<DocumentTextExtractor>();
        var thumbnails = sp.GetRequiredService<ThumbnailService>();

        var document = await db.Documents
            .Include(d => d.StorageLocation)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct);

        if (document is null || document.UpdateState == UpdateState.Deleted)
        {
            return;
        }

        try
        {
            if (document.StorageLocation is null)
            {
                _logger.LogWarning("Document {DocumentId} has no storage location; marking failed", documentId);
                document.OcrState = OcrState.Failed;
                document.UpdateDate = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            var absolutePath = storage.GetAbsolutePath(document.StorageLocation, document.RelativePath);
            var extension = Path.GetExtension(document.OriginalFileName);

            var extraction = await extractor.ExtractAsync(absolutePath, extension, ct);
            var thumbnail = await thumbnails.GenerateAsync(document.Token, absolutePath, extension, ct);

            document.OcrText = extraction.Text;
            document.PageCount = Math.Max(extraction.PageCount, IsImage(extension) ? 1 : extraction.PageCount);
            if (document.PageCount < 1 && IsImage(extension))
            {
                document.PageCount = 1;
            }

            document.ThumbnailPath = thumbnail;
            document.OcrState = OcrState.Done;

            await AutoAssignCorrespondentAsync(db, document, ct);
            ApplyDateHeuristic(document);

            document.UpdateDate = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processing failed for document {DocumentId}", documentId);
            try
            {
                document.OcrState = OcrState.Failed;
                document.UpdateDate = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "Failed to persist failure state for document {DocumentId}", documentId);
            }
        }
    }

    private async Task AutoAssignCorrespondentAsync(AppDbContext db, Document document, CancellationToken ct)
    {
        if (document.CorrespondentId is not null)
        {
            return;
        }

        var candidates = await db.Correspondents
            .AsNoTracking()
            .Where(c => c.UpdateState != UpdateState.Deleted && c.MatchPattern != null && c.MatchPattern != "")
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.MatchPattern })
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return;
        }

        var haystack = (document.Title ?? string.Empty) + "\n" + (document.OcrText ?? string.Empty);

        foreach (var candidate in candidates)
        {
            var pattern = candidate.MatchPattern!;
            if (Matches(haystack, pattern))
            {
                document.CorrespondentId = candidate.Id;
                return;
            }
        }
    }

    private static bool Matches(string haystack, string pattern)
    {
        try
        {
            return Regex.IsMatch(
                haystack,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            // Invalid regex -> fall back to a case-insensitive substring test.
            return haystack.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static void ApplyDateHeuristic(Document document)
    {
        if (document.DocumentDate is not null)
        {
            return;
        }

        var text = (document.Title ?? string.Empty) + "\n" + (document.OcrText ?? string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        MatchCollection matches;
        try
        {
            matches = DateRegex.Matches(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return;
        }

        foreach (Match match in matches)
        {
            if (DateTime.TryParseExact(
                    match.Value,
                    DateFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                if (parsed.Year is >= 1900 and <= 2100)
                {
                    document.DocumentDate = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
                    return;
                }
            }
        }
    }

    private async Task TryMarkFailedAsync(long documentId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
            if (document is not null && document.UpdateState != UpdateState.Deleted)
            {
                document.OcrState = OcrState.Failed;
                document.UpdateDate = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark document {DocumentId} as failed", documentId);
        }
    }

    private static bool IsImage(string extension)
    {
        var ext = (extension ?? string.Empty).Trim().ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp" or ".webp";
    }
}
