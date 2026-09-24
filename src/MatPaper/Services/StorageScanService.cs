using Microsoft.EntityFrameworkCore;
using MatPaper.Data;

namespace MatPaper.Services;

/// <summary>
/// Discovers files in a <see cref="StorageLocation"/> (local folder or SMB share) and records
/// the unknown ones as <see cref="InboxItem"/>s, then turns selected inbox items into managed
/// <see cref="Document"/>s (or dismisses them). Listing, hashing and file moves are delegated
/// to <see cref="DocumentStorageService"/>; OCR/thumbnail work is triggered via
/// <see cref="DocumentProcessingQueue"/>.
/// </summary>
public sealed class StorageScanService
{
    private readonly AppDbContext _db;
    private readonly DocumentStorageService _storage;
    private readonly DocumentProcessingQueue _queue;
    private readonly ILogger<StorageScanService> _logger;

    public StorageScanService(
        AppDbContext db,
        DocumentStorageService storage,
        DocumentProcessingQueue queue,
        ILogger<StorageScanService> logger)
    {
        _db = db;
        _storage = storage;
        _queue = queue;
        _logger = logger;
    }

    public record ScanResult(int Scanned, int Added, int Skipped, bool LocationMissing);

    public record ImportSummary(int Imported, int Duplicates, int Errors);

    /// <summary>
    /// Walks the storage location recursively and adds an <see cref="InboxItem"/> for every
    /// file that is not already tracked as a non-deleted document or a known inbox item.
    /// Dot-files and any path containing a dot-prefixed segment are ignored.
    /// </summary>
    public async Task<ScanResult> ScanAsync(long storageLocationId, CancellationToken ct)
    {
        var loc = await _db.StorageLocations
            .AsNoTracking()
            .Include(s => s.Credential)
            .FirstOrDefaultAsync(s => s.Id == storageLocationId && s.UpdateState != UpdateState.Deleted, ct);

        if (loc is null)
        {
            return new ScanResult(0, 0, 0, true);
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
                "Storage location {LocationId} ('{Root}') could not be listed; scan skipped.",
                storageLocationId, loc.DisplayRoot);
            return new ScanResult(0, 0, 0, true);
        }

        var docPaths = await _db.Documents
            .AsNoTracking()
            .Where(d => d.StorageLocationId == storageLocationId && !d.IsStaged && d.UpdateState != UpdateState.Deleted)
            .Select(d => d.RelativePath)
            .ToListAsync(ct);

        var inboxPaths = await _db.InboxItems
            .AsNoTracking()
            .Where(i => i.StorageLocationId == storageLocationId)
            .Select(i => i.RelativePath)
            .ToListAsync(ct);

        var docSet = new HashSet<string>(docPaths, StringComparer.Ordinal);
        var inboxSet = new HashSet<string>(inboxPaths, StringComparer.Ordinal);

        int scanned = 0;
        int skipped = 0;
        var now = DateTime.UtcNow;

        var fresh = new List<StorageEntry>();
        foreach (var entry in entries)
        {
            if (HasHiddenSegment(entry.RelativePath))
            {
                continue;
            }

            scanned++;

            if (docSet.Contains(entry.RelativePath) || inboxSet.Contains(entry.RelativePath))
            {
                skipped++;
                continue;
            }

            fresh.Add(entry);
        }

        // Hash the new files in one batch so a remote location needs a single connection
        // for the whole scan instead of one handshake per file.
        var hashes = await _storage.ComputeHashesAsync(loc, fresh.Select(e => e.RelativePath).ToList(), ct);

        int added = 0;
        foreach (var entry in fresh)
        {
            ct.ThrowIfCancellationRequested();

            if (!hashes.TryGetValue(entry.RelativePath, out var hash))
            {
                _logger.LogWarning("Could not read '{File}' while scanning; skipping.", entry.RelativePath);
                continue;
            }

            _db.InboxItems.Add(new InboxItem
            {
                StorageLocationId = storageLocationId,
                RelativePath = entry.RelativePath,
                FileName = Path.GetFileName(entry.RelativePath),
                FileSize = entry.Size,
                ContentHash = hash,
                FileModifiedUtc = entry.ModifiedUtc,
                UpdateState = UpdateState.Created,
                CreateDate = now,
                UpdateDate = now,
                CreateUserId = null,
                UpdateUserId = null
            });

            added++;
        }

        if (added > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return new ScanResult(scanned, added, skipped, false);
    }

    /// <summary>
    /// Imports the given inbox items as documents applying the shared metadata. Content-hash
    /// duplicates of existing documents are dismissed instead of re-imported. Each import is
    /// isolated; a failure counts as an error and does not abort the batch. The files are
    /// already inside the location, so the documents are created as filed (not staged) but
    /// still land in the owner's review inbox.
    /// </summary>
    public async Task<ImportSummary> ImportAsync(
        IReadOnlyCollection<long> inboxItemIds,
        long? correspondentId,
        long? documentTypeId,
        long? projectId,
        IReadOnlyCollection<long> tagIds,
        bool refile,
        long? actingUserId,
        CancellationToken ct)
    {
        int imported = 0;
        int duplicates = 0;
        int errors = 0;

        if (inboxItemIds is null || inboxItemIds.Count == 0)
        {
            return new ImportSummary(0, 0, 0);
        }

        string? correspondentName = null;
        string? documentTypeName = null;
        if (refile)
        {
            if (correspondentId.HasValue)
            {
                correspondentName = await _db.Correspondents
                    .AsNoTracking()
                    .Where(c => c.Id == correspondentId.Value)
                    .Select(c => c.Name)
                    .FirstOrDefaultAsync(ct);
            }

            if (documentTypeId.HasValue)
            {
                documentTypeName = await _db.DocumentTypes
                    .AsNoTracking()
                    .Where(t => t.Id == documentTypeId.Value)
                    .Select(t => t.Name)
                    .FirstOrDefaultAsync(ct);
            }
        }

        var tagIdList = tagIds is null ? new List<long>() : tagIds.Distinct().ToList();

        foreach (var id in inboxItemIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var item = await _db.InboxItems
                    .Include(i => i.StorageLocation).ThenInclude(s => s!.Credential)
                    .FirstOrDefaultAsync(i => i.Id == id && i.UpdateState != UpdateState.Deleted, ct);

                if (item is null || item.StorageLocation is null)
                {
                    errors++;
                    continue;
                }

                var now = DateTime.UtcNow;

                if (!string.IsNullOrEmpty(item.ContentHash))
                {
                    var isDuplicate = await _db.Documents
                        .AsNoTracking()
                        .AnyAsync(d => d.ContentHash == item.ContentHash
                            && d.UpdateState != UpdateState.Deleted
                            && (d.OwnerId == actingUserId || d.IsCommon), ct);

                    if (isDuplicate)
                    {
                        item.UpdateState = UpdateState.Deleted;
                        item.UpdateDate = now;
                        item.UpdateUserId = actingUserId;
                        await _db.SaveChangesAsync(ct);
                        duplicates++;
                        continue;
                    }
                }

                var title = Path.GetFileNameWithoutExtension(item.FileName);
                var loc = item.StorageLocation;

                string actualRel;
                if (refile)
                {
                    var desired = _storage.BuildRelativePath(
                        loc, title, item.FileModifiedUtc, correspondentName, documentTypeName, item.FileName);
                    actualRel = await _storage.MoveAsync(loc, item.RelativePath, desired, ct);
                }
                else
                {
                    actualRel = item.RelativePath;
                }

                var doc = new Document
                {
                    Token = Guid.NewGuid(),
                    Title = title,
                    DocumentDate = null,
                    DocumentTypeId = documentTypeId,
                    CorrespondentId = correspondentId,
                    ProjectId = projectId,
                    StorageLocationId = item.StorageLocationId,
                    IsStaged = false,
                    RelativePath = actualRel,
                    OriginalFileName = item.FileName,
                    FileSize = item.FileSize,
                    ContentHash = item.ContentHash,
                    OwnerId = actingUserId,
                    ReviewState = ReviewState.Pending,
                    PageCount = 0,
                    OcrState = OcrState.Pending,
                    UpdateState = UpdateState.Created,
                    CreateDate = now,
                    UpdateDate = now,
                    CreateUserId = actingUserId,
                    UpdateUserId = actingUserId
                };

                foreach (var tagId in tagIdList)
                {
                    doc.DocumentTags.Add(new DocumentTag
                    {
                        TagId = tagId,
                        CreateDate = now,
                        UpdateDate = now,
                        CreateUserId = actingUserId,
                        UpdateUserId = actingUserId
                    });
                }

                _db.Documents.Add(doc);

                item.UpdateState = UpdateState.Deleted;
                item.UpdateDate = now;
                item.UpdateUserId = actingUserId;

                await _db.SaveChangesAsync(ct);
                _queue.Enqueue(doc.Id);
                imported++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import inbox item {InboxItemId}.", id);
                errors++;
            }
        }

        return new ImportSummary(imported, duplicates, errors);
    }

    /// <summary>Marks the given non-deleted inbox items as dismissed. Returns the number changed.</summary>
    public async Task<int> DismissAsync(
        IReadOnlyCollection<long> inboxItemIds,
        long? actingUserId,
        CancellationToken ct)
    {
        if (inboxItemIds is null || inboxItemIds.Count == 0)
        {
            return 0;
        }

        var ids = inboxItemIds.Distinct().ToList();

        var items = await _db.InboxItems
            .Where(i => ids.Contains(i.Id) && i.UpdateState != UpdateState.Deleted)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            item.UpdateState = UpdateState.Deleted;
            item.UpdateDate = now;
            item.UpdateUserId = actingUserId;
        }

        if (items.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return items.Count;
    }

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
