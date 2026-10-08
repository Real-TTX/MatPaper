using System.Security.Cryptography;
using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

public enum IngestStatus
{
    Created,
    Duplicate,
    NoStorage,

    /// <summary>The content is on the blocklist (the user deleted it and asked not to get it back).</summary>
    Blocked
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

    public static IngestResult Blocked() => new(IngestStatus.Blocked, 0);
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
    DocumentProcessingQueue queue,
    InvoiceDataExtractor invoices,
    InvoicePdfRenderer invoiceRenderer,
    DocumentSidecarService sidecars,
    MatPaper.Configuration.AppConfig config)
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
        ReviewState reviewState = ReviewState.Pending,
        bool isCommon = false,
        DocumentOrigin origin = DocumentOrigin.Upload,
        long? importTaskId = null,
        DocumentSidecar? sidecar = null)
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

        // A bare XRechnung (XML without a PDF) becomes a readable PDF so it fits the archive; the XML
        // is kept next to it as a companion file. The hash stays that of the XML, so importing the
        // same invoice again is still recognised (the generated PDF differs on every rendering).
        byte[]? invoiceXml = null;
        if (string.Equals(Path.GetExtension(originalFileName ?? string.Empty), ".xml", StringComparison.OrdinalIgnoreCase))
        {
            var xmlBytes = buffer.ToArray();
            var parsed = invoices.TryExtractFromXml(xmlBytes);
            if (parsed is not null)
            {
                var german = !string.Equals(config.Display?.Culture, "en-US", StringComparison.OrdinalIgnoreCase);
                invoiceXml = xmlBytes;
                buffer = new MemoryStream(invoiceRenderer.Render(parsed, german));
                fileSize = buffer.Length;
                originalFileName = Path.GetFileNameWithoutExtension(originalFileName) + ".pdf";
            }
        }

        // Dedupe within the owner's own documents (plus the common area) so two
        // users can each hold their own copy of the same file.
        //
        // For anything a machine brings in repeatedly - a mailbox that keeps the message,
        // a watched folder that keeps the file - a deleted document counts as a tombstone,
        // exactly like a known path does for the storage search. Otherwise the next run
        // would hand back what the user just threw away. A person uploading the same file
        // again means it, so there the deleted copy is ignored.
        //
        // The tombstone only counts while the file still exists. A staged document that was
        // deleted lost its file, so treating it as known would make the import task delete
        // the source as a duplicate and leave no copy anywhere.
        var automated = origin is DocumentOrigin.Mail or DocumentOrigin.ImportFolder or DocumentOrigin.StorageScan;

        // The blocklist: what a user threw away on purpose never comes back through an import or a search.
        if (automated
            && await db.BlockedDocuments.AsNoTracking()
                .AnyAsync(b => b.ContentHash == contentHash && (b.OwnerId == actingUserId || b.OwnerId == null), ct)
                .ConfigureAwait(false))
        {
            return IngestResult.Blocked();
        }

        var existing = await db.Documents
            .AsNoTracking()
            .Where(d => (d.UpdateState != UpdateState.Deleted || (automated && d.RelativePath != ""))
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
                .Include(s => s.Connection)
                .FirstOrDefaultAsync(s => s.Id == requestedId && s.UpdateState != UpdateState.Deleted, ct)
                .ConfigureAwait(false);
        }

        if (location is null && !stage)
        {
            location = await db.StorageLocations
                .Include(s => s.Connection)
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
                safeFileName,
                token);

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
            IsEInvoice = invoiceXml is not null,
            HasEInvoiceXml = invoiceXml is not null,
            OwnerId = actingUserId,
            IsCommon = isCommon,
            Origin = origin,
            ImportTaskId = importTaskId,
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

        // A metadata file that came with the file (another MatPaper's archive) restores what was known.
        if (sidecar is not null)
        {
            await sidecars.ApplyAsync(document, sidecar, actingUserId, ct).ConfigureAwait(false);
        }

        if (invoiceXml is not null)
        {
            try
            {
                if (stage)
                {
                    await storage.StageCompanionAsync(actualRelativePath, DocumentStorageService.XmlCompanion, invoiceXml, ct).ConfigureAwait(false);
                }
                else
                {
                    await storage.WriteCompanionAsync(location!, actualRelativePath, DocumentStorageService.XmlCompanion, invoiceXml, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                document.HasEInvoiceXml = false; // the PDF alone is still a usable document
            }
        }

        db.Documents.Add(document);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        queue.Enqueue(document.Id);

        return IngestResult.Created(document.Id);
    }

    /// <summary>
    /// Ingests a file that may be an archive: a .zip is opened and each document in it is ingested like a file of its
    /// own (PDFs, images and XML, or what <paramref name="archiveExtensions"/> lists; folders, hidden files and other
    /// types are left out). Anything else is ingested as it is. An entry that cannot be read or stored does not stop
    /// the others - it is reported in the batch - while a plain file that fails throws, as <see cref="IngestAsync"/> does.
    /// The archive itself is not kept, only what was in it.
    /// </summary>
    public async Task<IngestBatch> IngestFileAsync(
        Stream content,
        string originalFileName,
        long? storageLocationId,
        long? correspondentId,
        long? documentTypeId,
        long? projectId,
        IReadOnlyCollection<long> tagIds,
        long? actingUserId,
        CancellationToken ct,
        ReviewState reviewState = ReviewState.Pending,
        bool isCommon = false,
        DocumentOrigin origin = DocumentOrigin.Upload,
        long? importTaskId = null,
        DocumentSidecar? sidecar = null,
        IReadOnlyCollection<string>? archiveExtensions = null)
    {
        if (!DocumentArchive.IsArchive(originalFileName))
        {
            var single = new IngestBatch(fromArchive: false);
            var result = await IngestAsync(
                content, originalFileName, storageLocationId, correspondentId, documentTypeId, projectId, tagIds,
                actingUserId, ct, reviewState, isCommon, origin, importTaskId, sidecar).ConfigureAwait(false);
            single.Add(originalFileName, result);
            return single;
        }

        var batch = new IngestBatch(fromArchive: true);
        await foreach (var item in DocumentArchive.ReadAsync(content, originalFileName, archiveExtensions, ct).ConfigureAwait(false))
        {
            switch (item.Kind)
            {
                case ArchiveItemKind.Skipped:
                    batch.Skip(item.Label, item.Reason ?? string.Empty);
                    break;
                case ArchiveItemKind.Problem:
                    batch.Fail(item.Label, item.Reason ?? DocumentArchive.Unreadable);
                    break;
                default:
                    try
                    {
                        await using var stream = new MemoryStream(item.Content!, writable: false);
                        var result = await IngestAsync(
                            stream, item.Name, storageLocationId, correspondentId, documentTypeId, projectId, tagIds,
                            actingUserId, ct, reviewState, isCommon, origin, importTaskId, item.Sidecar).ConfigureAwait(false);
                        batch.Add(item.Label, result);
                        if (result.Status == IngestStatus.NoStorage)
                        {
                            return batch; // there is nowhere to put any of the others either
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        batch.Fail(item.Label, ex.Message);
                    }

                    break;
            }
        }

        return batch;
    }

    private static async Task<string> ComputeHashAsync(Stream stream, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
