using System.Security.Cryptography;
using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

public enum IngestStatus
{
    Created,
    Duplicate,
    NoStorage
}

/// <summary>
/// Outcome of an ingest attempt. <see cref="DocumentId"/> holds the id of the
/// created document (<see cref="IngestStatus.Created"/>) or the existing
/// duplicate (<see cref="IngestStatus.Duplicate"/>); it is 0 for
/// <see cref="IngestStatus.NoStorage"/>.
/// </summary>
public sealed class IngestResult
{
    private IngestResult(IngestStatus status, long documentId)
    {
        Status = status;
        DocumentId = documentId;
    }

    public IngestStatus Status { get; }

    public long DocumentId { get; }

    public static IngestResult Created(long documentId) => new(IngestStatus.Created, documentId);

    public static IngestResult Duplicate(long existingDocumentId) => new(IngestStatus.Duplicate, existingDocumentId);

    public static IngestResult NoStorage() => new(IngestStatus.NoStorage, 0);
}

/// <summary>
/// Accepts an uploaded/imported file: dedupes by content hash, persists the
/// <see cref="Document"/> row and enqueues it for OCR / thumbnail processing.
/// Documents headed for the review inbox (<see cref="ReviewState.Pending"/>) are
/// kept in the local staging area and only remember their target location;
/// documents that skip the inbox are filed into a storage location right away.
/// </summary>
public sealed class DocumentIngestService(
    AppDbContext db,
    DocumentStorageService storage,
    DocumentProcessingQueue queue)
{
    public async Task<IngestResult> IngestAsync(
        Stream content,
        string originalFileName,
        long? storageLocationId,
        long? correspondentId,
        long? documentTypeId,
        long? projectId,
        IReadOnlyCollection<long> tagIds,
        long? actingUserId,
        CancellationToken ct,
        ReviewState reviewState = ReviewState.Pending)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Buffer the content so we can hash it and then hand a rewindable
        // stream to the storage service.
        var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        var fileSize = buffer.Length;
        var contentHash = await ComputeHashAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        // Dedupe within the owner's own documents (plus the common area) so two
        // users can each hold their own copy of the same file.
        var existing = await db.Documents
            .AsNoTracking()
            .Where(d => d.UpdateState != UpdateState.Deleted
                && d.ContentHash == contentHash
                && (d.OwnerId == actingUserId || d.IsCommon))
            .Select(d => new { d.Id })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return IngestResult.Duplicate(existing.Id);
        }

        // Inbox documents are staged locally; the location (if any) is just the target
        // that will be preselected when the user confirms. Skip-inbox documents need a
        // real location now: the requested one, else the default, else nothing works.
        var stage = reviewState == ReviewState.Pending;

        StorageLocation? location = null;
        if (storageLocationId is long requestedId)
        {
            location = await db.StorageLocations
                .Include(s => s.Credential)
                .FirstOrDefaultAsync(s => s.Id == requestedId && s.UpdateState != UpdateState.Deleted, ct)
                .ConfigureAwait(false);
        }

        if (location is null && !stage)
        {
            location = await db.StorageLocations
                .Include(s => s.Credential)
                .Where(s => s.UpdateState != UpdateState.Deleted)
                .OrderByDescending(s => s.IsDefault)
                .ThenBy(s => s.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (location is null)
            {
                return IngestResult.NoStorage();
            }
        }

        var safeFileName = string.IsNullOrWhiteSpace(originalFileName) ? "document" : originalFileName;
        var title = Path.GetFileNameWithoutExtension(safeFileName);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = safeFileName;
        }

        string? correspondentName = null;
        if (correspondentId is { } cid)
        {
            correspondentName = await db.Correspondents
                .Where(c => c.Id == cid)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        string? documentTypeName = null;
        if (documentTypeId is { } tid)
        {
            documentTypeName = await db.DocumentTypes
                .Where(t => t.Id == tid)
                .Select(t => t.Name)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        var now = DateTime.UtcNow;
        var token = Guid.NewGuid();

        string actualRelativePath;
        if (stage)
        {
            actualRelativePath = await storage
                .StageNewAsync(token, safeFileName, buffer, ct)
                .ConfigureAwait(false);
        }
        else
        {
            var desiredRelativePath = storage.BuildRelativePath(
                location!,
                title,
                now,
                correspondentName,
                documentTypeName,
                safeFileName);

            actualRelativePath = await storage
                .SaveNewAsync(location!, desiredRelativePath, buffer, ct)
                .ConfigureAwait(false);
        }

        var document = new Document
        {
            Token = token,
            Title = title,
            DocumentDate = null,
            DocumentTypeId = documentTypeId,
            CorrespondentId = correspondentId,
            ProjectId = projectId,
            StorageLocationId = location?.Id,
            IsStaged = stage,
            RelativePath = actualRelativePath,
            OriginalFileName = safeFileName,
            FileSize = fileSize,
            ContentHash = contentHash,
            OwnerId = actingUserId,
            ReviewState = reviewState,
            PageCount = 0,
            OcrState = OcrState.Pending,
            UpdateState = UpdateState.Created,
            CreateDate = now,
            CreateUserId = actingUserId,
            UpdateDate = now,
            UpdateUserId = actingUserId
        };

        if (tagIds is { Count: > 0 })
        {
            foreach (var tagId in tagIds.Distinct())
            {
                document.DocumentTags.Add(new DocumentTag
                {
                    TagId = tagId,
                    CreateDate = now,
                    CreateUserId = actingUserId,
                    UpdateDate = now,
                    UpdateUserId = actingUserId
                });
            }
        }

        db.Documents.Add(document);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        queue.Enqueue(document.Id);

        return IngestResult.Created(document.Id);
    }

    private static async Task<string> ComputeHashAsync(Stream stream, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
