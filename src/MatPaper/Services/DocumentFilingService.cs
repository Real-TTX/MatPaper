using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

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
    ILogger<DocumentFilingService> logger)
{
    /// <summary>
    /// Files the (tracked) <paramref name="document"/> into <paramref name="storageLocationId"/>,
    /// falling back to the document's own target and then to the default location. The file is
    /// placed by the location's template using the document's current metadata. Saves on success.
    /// </summary>
    public async Task<FilingResult> FileAsync(Document document, long? storageLocationId, long? actingUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);

        var target = await ResolveTargetAsync(storageLocationId ?? document.StorageLocationId, ct).ConfigureAwait(false);
        if (target is null)
        {
            return FilingResult.Fail("No storage location is configured. Create one under System → Storage locations first.");
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

        var desired = storage.BuildRelativePath(
            target,
            document.Title,
            document.DocumentDate ?? document.CreateDate,
            correspondentName,
            documentTypeName,
            document.OriginalFileName);

        try
        {
            if (document.IsStaged)
            {
                document.RelativePath = await storage
                    .FileFromStagingAsync(document.RelativePath, target, desired, ct)
                    .ConfigureAwait(false);
                document.IsStaged = false;
            }
            else if (!string.IsNullOrEmpty(document.RelativePath) && document.StorageLocationId != target.Id)
            {
                // The file is already stored somewhere (storage-scan import or older data).
                // Only a deliberate change of location moves it — confirming must not
                // silently re-file documents that were adopted "keep in place".
                var current = document.StorageLocationId is long currentId
                    ? await db.StorageLocations.Include(s => s.Credential).FirstOrDefaultAsync(s => s.Id == currentId, ct).ConfigureAwait(false)
                    : null;

                if (current is not null)
                {
                    document.RelativePath = await storage
                        .RelocateAsync(current, document.RelativePath, target, desired, ct)
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
            return FilingResult.Fail($"The file could not be stored in \"{target.Name}\": {ex.Message}");
        }

        var now = DateTime.UtcNow;
        document.StorageLocationId = target.Id;
        document.ReviewState = ReviewState.Reviewed;
        document.UpdateState = UpdateState.Updated;
        document.UpdateDate = now;
        document.UpdateUserId = actingUserId;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return FilingResult.Ok();
    }

    /// <summary>The active location with the given id, else the default (or first) active location, else null.</summary>
    public async Task<StorageLocation?> ResolveTargetAsync(long? preferredId, CancellationToken ct)
    {
        if (preferredId is long id)
        {
            var preferred = await db.StorageLocations
                .Include(s => s.Credential)
                .FirstOrDefaultAsync(s => s.Id == id && s.UpdateState != UpdateState.Deleted, ct)
                .ConfigureAwait(false);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return await db.StorageLocations
            .Include(s => s.Credential)
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
