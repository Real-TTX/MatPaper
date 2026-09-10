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
/// Accepts an uploaded/imported file: dedupes by content hash, writes it into
/// the target storage location, persists the <see cref="Document"/> row and
/// enqueues it for OCR / thumbnail processing.
/// </summary>
public sealed class DocumentIngestService(
    AppDbContext db,
    DocumentStorageService storage,
    DocumentProcessingQueue queue)
{
    public async Task<IngestResult> IngestAsync(
        Stream content,
        string originalFileName,
        long storageLocationId,
        long? correspondentId,
        long? documentTypeId,
        long? projectId,
        IReadOnlyCollection<long> tagIds,
        long? actingUserId,
        CancellationToken ct)
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

        var location = await db.StorageLocations
            .FirstOrDefaultAsync(s => s.Id == storageLocationId && s.UpdateState != UpdateState.Deleted, ct)
            .ConfigureAwait(false);

        if (location is null)
        {
            return IngestResult.NoStorage();
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

        var desiredRelativePath = storage.BuildRelativePath(
            location,
            title,
            now,
            correspondentName,
            documentTypeName,
            safeFileName);

        var actualRelativePath = await storage
            .SaveNewAsync(location, desiredRelativePath, buffer)
            .ConfigureAwait(false);

        var document = new Document
        {
            Token = Guid.NewGuid(),
            Title = title,
            DocumentDate = null,
            DocumentTypeId = documentTypeId,
            CorrespondentId = correspondentId,
            ProjectId = projectId,
            StorageLocationId = location.Id,
            RelativePath = actualRelativePath,
            OriginalFileName = safeFileName,
            FileSize = fileSize,
            ContentHash = contentHash,
            OwnerId = actingUserId,
            ReviewState = ReviewState.Pending,
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
