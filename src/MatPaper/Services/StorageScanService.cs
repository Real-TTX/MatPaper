using Microsoft.EntityFrameworkCore;
using MatPaper.Data;

namespace MatPaper.Services;

/// <summary>
/// Searches a <see cref="StorageLocation"/> (local folder or SMB share) for files MatPaper
/// does not know yet and records each one as a <see cref="Document"/> that waits in the
/// inbox. Discovery is deliberately cheap: it lists the location once and never reads file
/// contents, never copies anything into the staging area and never queues OCR. Hashing and
/// text recognition happen later, when the user asks for them.
/// </summary>
public sealed class StorageScanService
{
    /// <summary>Documents written per SaveChanges so a large archive does not build one giant transaction.</summary>
    private const int ChunkSize = 500;

    private readonly AppDbContext _db;
    private readonly DocumentStorageService _storage;
    private readonly DocumentSidecarService _sidecars;
    private readonly ILogger<StorageScanService> _logger;

    public StorageScanService(
        AppDbContext db,
        DocumentStorageService storage,
        DocumentSidecarService sidecars,
        ILogger<StorageScanService> logger)
    {
        _db = db;
        _storage = storage;
        _sidecars = sidecars;
        _logger = logger;
    }

    /// <summary>
    /// Outcome of a search. <paramref name="Failure"/> is a user-facing reason why nothing
    /// could be searched (share offline, root inside MatPaper's own data folder).
    /// </summary>
    public record ScanResult(int Scanned, int Added, int Skipped, bool LocationMissing, string? Failure)
    {
        public bool Ok => !LocationMissing && Failure is null;

        /// <summary>Files that were moved by hand and recognised again by their id: the document kept, only its path updated.</summary>
        public int Relinked { get; init; }
    }

    /// <summary>
    /// Walks the storage location recursively and adds a pending document for every file that
    /// is not known yet. Dot-files, paths with a dot-prefixed segment and extensions outside
    /// the location's <see cref="StorageLocation.ScanExtensions"/> are ignored.
    /// </summary>
    public async Task<ScanResult> ScanAsync(long storageLocationId, long? actingUserId, CancellationToken ct)
    {
        var loc = await _db.StorageLocations
            .AsNoTracking()
            .Include(s => s.Connection)
            .FirstOrDefaultAsync(s => s.Id == storageLocationId && s.UpdateState != UpdateState.Deleted, ct);

        if (loc is null)
        {
            return new ScanResult(0, 0, 0, true, null);
        }

        if (_storage.OverlapsInternalData(loc, out var conflict))
        {
            return await FinishAsync(loc, 0, $"The root overlaps MatPaper's own folder \"{conflict}\".", ct);
        }

        IReadOnlyList<StorageEntry> entries;
        try
        {
            entries = await _storage.ListFilesAsync(loc, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Storage location {LocationId} ('{Root}') could not be listed.",
                storageLocationId, loc.DisplayRoot);
            return await FinishAsync(loc, 0, ex.Message, ct);
        }

        // Known paths include soft-deleted and ignored documents on purpose: both are
        // tombstones that keep the next search from offering the same file again.
        var knownPaths = await _db.Documents
            .AsNoTracking()
            .Where(d => d.StorageLocationId == storageLocationId && !d.IsStaged)
            .Select(d => d.RelativePath)
            .ToListAsync(ct);

        var known = new HashSet<string>(knownPaths, StringComparer.Ordinal);
        var extensions = ParseExtensions(loc.ScanExtensions);
        var ownerId = await ResolveOwnerIdAsync(loc, actingUserId, ct);

        // A file that moved or was renamed outside MatPaper still carries its document's id (in the name
        // or in the metadata file). When the old path is gone, that is the same document, not a new one.
        var knownDocs = await _db.Documents.AsNoTracking()
            .Where(d => d.StorageLocationId == storageLocationId && !d.IsStaged)
            .Select(d => new { d.Id, d.Token, d.RelativePath })
            .ToListAsync(ct);
        var byShortId = knownDocs.GroupBy(d => DocumentStorageService.ShortId(d.Token)).ToDictionary(g => g.Key, g => g.First());
        var byToken = knownDocs.ToDictionary(d => d.Token, d => d);
        var relinks = new List<(long Id, string Path, DateTime? Modified)>();

        // Companion files (a metadata file, the XML of an imported XRechnung) belong to the file
        // they are named after and are never documents of their own.
        var allPaths = new HashSet<string>(entries.Select(e => e.RelativePath), StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        int scanned = 0, skipped = 0, added = 0;
        var batch = new List<Document>(ChunkSize);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (HasHiddenSegment(entry.RelativePath) || !Wanted(entry.RelativePath, extensions)
                || IsCompanion(entry.RelativePath, allPaths))
            {
                continue;
            }

            scanned++;

            if (!known.Add(entry.RelativePath))
            {
                skipped++;
                continue;
            }

            var fileName = Path.GetFileName(entry.RelativePath);

            // Same document, new place?
            DocumentSidecar? entrySidecar = null;
            var shortId = DocumentStorageService.ShortIdFromName(entry.RelativePath);
            var match = shortId is not null && byShortId.TryGetValue(shortId, out var byName) ? byName : null;
            if (match is null && allPaths.Contains(entry.RelativePath + DocumentStorageService.MetadataCompanion))
            {
                entrySidecar = await _sidecars.ReadAsync(loc, entry.RelativePath, ct);
                if (entrySidecar?.Token is Guid sidecarToken && byToken.TryGetValue(sidecarToken, out var byMeta))
                {
                    match = byMeta;
                }
            }

            if (match is not null && !allPaths.Contains(match.RelativePath))
            {
                relinks.Add((match.Id, entry.RelativePath, entry.ModifiedUtc));
                continue;
            }

            var title = Path.GetFileNameWithoutExtension(fileName);

            var found = new Document
            {
                Token = Guid.NewGuid(),
                Title = string.IsNullOrWhiteSpace(title) ? fileName : title,
                OriginalFileName = fileName,
                RelativePath = entry.RelativePath,
                StorageLocationId = storageLocationId,
                IsStaged = false,
                Origin = DocumentOrigin.StorageScan,
                FileSize = entry.Size,
                FileModifiedUtc = entry.ModifiedUtc,
                ContentHash = null,
                OwnerId = ownerId,
                IsCommon = loc.DefaultIsCommon,
                ReviewState = ReviewState.Pending,
                OcrState = OcrState.Deferred,
                UpdateState = UpdateState.Created,
                PageCount = 0,
                CreateDate = now,
                UpdateDate = now,
                CreateUserId = actingUserId,
                UpdateUserId = actingUserId
            };

            found.HasEInvoiceXml = allPaths.Contains(entry.RelativePath + DocumentStorageService.XmlCompanion);

            // A metadata file next to it carries the document's type, correspondent, tags ... from an
            // earlier life (another instance, a rebuilt database): use it instead of starting blank.
            if (allPaths.Contains(entry.RelativePath + DocumentStorageService.MetadataCompanion)
                && await _sidecars.ReadAsync(loc, entry.RelativePath, ct) is { } sidecar)
            {
                await _sidecars.ApplyAsync(found, sidecar, actingUserId, ct);
            }

            batch.Add(found);

            added++;

            if (batch.Count >= ChunkSize)
            {
                await FlushAsync(batch, ct);
            }
        }

        await FlushAsync(batch, ct);

        // Files that were moved by hand: the document keeps everything, only its path changes.
        foreach (var (id, path, modified) in relinks)
        {
            await _db.Documents.Where(d => d.Id == id)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(d => d.RelativePath, path)
                    .SetProperty(d => d.FileModifiedUtc, modified)
                    .SetProperty(d => d.UpdateDate, DateTime.UtcNow), ct);
        }

        _logger.LogInformation(
            "Storage search of location {LocationId} finished: {Scanned} scanned, {Added} new, {Skipped} known.",
            storageLocationId, scanned, added, skipped);

        await FinishAsync(loc, added, null, ct);
        return new ScanResult(scanned, added, skipped, false, null) { Relinked = relinks.Count };
    }

    internal static bool IsCompanion(string relativePath, HashSet<string> allPaths)
    {
        if (relativePath.EndsWith(DocumentStorageService.MetadataCompanion, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // "invoice.pdf.xml" next to "invoice.pdf"
        return relativePath.EndsWith(DocumentStorageService.XmlCompanion, StringComparison.OrdinalIgnoreCase)
            && allPaths.Contains(relativePath[..^DocumentStorageService.XmlCompanion.Length]);
    }

    private async Task FlushAsync(List<Document> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        _db.Documents.AddRange(batch);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        batch.Clear();
    }

    /// <summary>Records the outcome on the location so the storage list can show it.</summary>
    private async Task<ScanResult> FinishAsync(StorageLocation loc, int added, string? failure, CancellationToken ct)
    {
        var finishedAt = DateTime.UtcNow;

        await _db.StorageLocations
            .Where(s => s.Id == loc.Id)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.LastScanUtc, finishedAt)
                .SetProperty(s => s.LastScanFound, added)
                .SetProperty(s => s.LastScanError, failure), ct);

        return new ScanResult(0, added, 0, false, failure);
    }

    /// <summary>
    /// The location's configured owner (when still active), else the user who started the
    /// search, else the first active administrator.
    /// </summary>
    private async Task<long?> ResolveOwnerIdAsync(StorageLocation loc, long? actingUserId, CancellationToken ct)
    {
        if (loc.DefaultOwnerId is long configured)
        {
            var active = await _db.Users
                .AsNoTracking()
                .AnyAsync(u => u.Id == configured && u.IsActive, ct);

            if (active)
            {
                return configured;
            }
        }

        if (actingUserId is not null)
        {
            return actingUserId;
        }

        return await _db.Users
            .AsNoTracking()
            .Where(u => u.IsActive && u.Role != null && u.Role.Name == "Admin")
            .OrderBy(u => u.Id)
            .Select(u => (long?)u.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Extensions to pick up, lowercase and with a leading dot. Empty = every file.</summary>
    internal static HashSet<string> ParseExtensions(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return set;
        }

        foreach (var part in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(part.StartsWith('.') ? part : "." + part);
        }

        return set;
    }

    private static bool Wanted(string relativePath, HashSet<string> extensions)
        => extensions.Count == 0 || extensions.Contains(Path.GetExtension(relativePath));

    private static bool HasHiddenSegment(string relativePath)
    {
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith('.'))
            {
                return true;
            }
        }

        return false;
    }
}
