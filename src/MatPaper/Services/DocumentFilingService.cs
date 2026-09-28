using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>What confirming a document should do with its file.</summary>
public enum FilingMode
{
    /// <summary>Move the file out of the local staging area into the storage location.</summary>
    FromStaging = 0,

    /// <summary>Leave the file exactly where it is. Used for files a storage search found.</summary>
    KeepInPlace = 1,

    /// <summary>Move the already-stored file to the path the location template produces.</summary>
    RefileByTemplate = 2
}

/// <summary>Outcome of filing a document. <see cref="Error"/> is a user-facing message.</summary>
public sealed record FilingResult(bool Success, string? Error)
{
    public static FilingResult Ok() => new(true, null);

    public static FilingResult Fail(string error) => new(false, error);
}

/// <summary>
/// Confirms a reviewed document: moves the file out of the inbox staging area (or re-files
/// an already stored one) into its storage location using the location's path template,
/// and marks the document as reviewed. Also hosts the tag-sync helper shared by the inbox
/// and the edit page so both apply metadata the same way.
/// </summary>
public sealed class DocumentFilingService(
    AppDbContext db,
    DocumentStorageService storage,
    Microsoft.Extensions.Localization.IStringLocalizer<SharedResource> l,
    ILogger<DocumentFilingService> logger)
{
    /// <summary>
    /// Confirms the (tracked) <paramref name="document"/> and marks it reviewed.
    /// <list type="bullet">
    /// <item><see cref="FilingMode.FromStaging"/> moves the file out of the staging area into
    /// <paramref name="storageLocationId"/> (falling back to the document target, then the default).</item>
    /// <item><see cref="FilingMode.KeepInPlace"/> touches no file at all — the document is adopted
    /// exactly where it lies.</item>
    /// <item><see cref="FilingMode.RefileByTemplate"/> moves the stored file to the path the
    /// location template produces.</item>
    /// </list>
    /// A staged document is always filed from staging, whatever the mode says. Saves on success.
    /// </summary>
    public async Task<FilingResult> FileAsync(
        Document document,
        long? storageLocationId,
        FilingMode mode,
        long? actingUserId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Adopting a found file must not consult a storage location at all: resolving one
        // would fall back to the default location and move the file off the user's own tree.
        if (!document.IsStaged && mode == FilingMode.KeepInPlace)
        {
            MarkReviewed(document, actingUserId);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return FilingResult.Ok();
        }

        var target = await ResolveTargetAsync(storageLocationId ?? document.StorageLocationId, ct).ConfigureAwait(false);
        if (target is null)
        {
            return FilingResult.Fail(l["No storage location is configured. Create one under System → Storage locations first."].Value);
        }

        string? correspondentName = null;
        if (document.CorrespondentId is long correspondentId)
        {
            correspondentName = await db.Correspondents.AsNoTracking()
                .Where(c => c.Id == correspondentId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        string? documentTypeName = null;
        if (document.DocumentTypeId is long documentTypeId)
        {
            documentTypeName = await db.DocumentTypes.AsNoTracking()
                .Where(t => t.Id == documentTypeId)
                .Select(t => t.Name)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        // A file found on a NAS carries its own age: without the file date a 2019 invoice
        // would be filed under this year's {Year} folder.
        var effectiveDate = document.DocumentDate ?? document.FileModifiedUtc ?? document.CreateDate;

        var desired = storage.BuildRelativePath(
            target,
            document.Title,
            effectiveDate,
            correspondentName,
            documentTypeName,
            document.OriginalFileName);

        // Files MatPaper never placed itself live in folders the user created. Moving one on
        // request is fine; tidying up the folders around it is not.
        var keepFolders = document.Origin == DocumentOrigin.StorageScan;

        try
        {
            if (document.IsStaged)
            {
                document.RelativePath = await storage
                    .FileFromStagingAsync(document.RelativePath, target, desired, ct)
                    .ConfigureAwait(false);
                document.IsStaged = false;
            }
            else if (!string.IsNullOrEmpty(document.RelativePath))
            {
                var current = document.StorageLocationId is long currentId
                    ? await db.StorageLocations.Include(s => s.Connection).FirstOrDefaultAsync(s => s.Id == currentId, ct).ConfigureAwait(false)
                    : null;

                if (current is null)
                {
                    // No source location on record — nothing to move, just record the target.
                }
                else if (current.Id == target.Id)
                {
                    document.RelativePath = await storage
                        .MoveAsync(target, document.RelativePath, desired, ct, cleanupEmptyDirectories: !keepFolders)
                        .ConfigureAwait(false);
                }
                else
                {
                    document.RelativePath = await storage
                        .RelocateAsync(current, document.RelativePath, target, desired, ct, cleanupEmptyDirectories: !keepFolders)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Filing document {DocumentId} into storage location {LocationId} failed.", document.Id, target.Id);
            return FilingResult.Fail(l["The file could not be stored in \"{0}\": {1}", target.Name, ex.Message].Value);
        }

        document.StorageLocationId = target.Id;
        MarkReviewed(document, actingUserId);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return FilingResult.Ok();
    }

    private static void MarkReviewed(Document document, long? actingUserId)
    {
        var now = DateTime.UtcNow;
        document.ReviewState = ReviewState.Reviewed;
        document.UpdateState = UpdateState.Updated;
        document.UpdateDate = now;
        document.UpdateUserId = actingUserId;
    }

    /// <summary>The active location with the given id, else the default (or first) active location, else null.</summary>
    public async Task<StorageLocation?> ResolveTargetAsync(long? preferredId, CancellationToken ct)
    {
        if (preferredId is long id)
        {
            var preferred = await db.StorageLocations
                .Include(s => s.Connection)
                .FirstOrDefaultAsync(s => s.Id == id && s.UpdateState != UpdateState.Deleted, ct)
                .ConfigureAwait(false);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return await db.StorageLocations
            .Include(s => s.Connection)
            .Where(s => s.UpdateState != UpdateState.Deleted)
            .OrderByDescending(s => s.IsDefault)
            .ThenBy(s => s.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Replaces the document's tag links with exactly <paramref name="desiredTagIds"/> (DocumentTags must be loaded).</summary>
    public void SyncTags(Document document, IEnumerable<long> desiredTagIds, DateTime now, long? actingUserId)
    {
        var desired = new HashSet<long>(desiredTagIds ?? Array.Empty<long>());
        var current = document.DocumentTags.ToList();

        foreach (var link in current.Where(dt => !desired.Contains(dt.TagId)))
        {
            document.DocumentTags.Remove(link);
            db.DocumentTags.Remove(link);
        }

        var existingIds = current.Select(dt => dt.TagId).ToHashSet();
        foreach (var tagId in desired.Where(id => !existingIds.Contains(id)))
        {
            document.DocumentTags.Add(new DocumentTag
            {
                DocumentId = document.Id,
                TagId = tagId,
                CreateDate = now,
                UpdateDate = now,
                CreateUserId = actingUserId,
                UpdateUserId = actingUserId
            });
        }
    }
}
