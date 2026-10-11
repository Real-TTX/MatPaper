using System.Security.Cryptography;
using MatPaper.Data;
using MatPaper.Services.Barcodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
    private IngestResult(IngestStatus status, long documentId, long? archiveNumber = null, long? refusedNumber = null)
    {
        Status = status;
        DocumentId = documentId;
        ArchiveSerialNumber = archiveNumber;
        RefusedNumber = refusedNumber;
    }

    public IngestStatus Status { get; }

    public long DocumentId { get; }

    /// <summary>The archive number the new document got (read from a sticker on the paper, or sent along).</summary>
    public long? ArchiveSerialNumber { get; }

    /// <summary>
    /// A number was on the paper but another document has it: the new document was filed without a number
    /// (a number is the number of one document).
    /// </summary>
    public long? RefusedNumber { get; }

    public static IngestResult Created(long documentId, long? archiveNumber = null, long? refusedNumber = null)
        => new(IngestStatus.Created, documentId, archiveNumber, refusedNumber);

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
    MatPaper.Configuration.AppConfig config,
    ILogger<DocumentIngestService> logger)
{
    /// <summary>
    /// Ingests one file as one document. <paramref name="archiveSerialNumber"/> is the number on the paper original
    /// (read from a sticker by <see cref="IngestFileAsync"/>); a number that another document has is not given twice -
    /// the document is filed without it and the result says so. <paramref name="knownContentHash"/> stands in for the
    /// hash of the content when the file is a part cut out of a bigger one (see <see cref="IngestFileAsync"/>).
    /// </summary>
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
        DocumentSidecar? sidecar = null,
        long? archiveSerialNumber = null,
        string? knownContentHash = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Buffer the content so we can hash it and then hand a rewindable stream to the storage service. Content
        // that is in memory already (a file that was read for its barcodes) is used as it is, not copied again.
        MemoryStream buffer;
        if (content is MemoryStream inMemory && !inMemory.CanWrite && inMemory.Position == 0
            && inMemory.TryGetBuffer(out var segment) && segment.Offset == 0 && segment.Count == inMemory.Length)
        {
            buffer = inMemory;
        }
        else
        {
            buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        }

        buffer.Position = 0;

        var fileSize = buffer.Length;
        var contentHash = knownContentHash ?? await ComputeHashAsync(buffer, ct).ConfigureAwait(false);
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

        // The number on the paper is the number of one document: when another one has it, this one is filed
        // without (and the result tells, so the person can look into it).
        long? assignedNumber = null;
        long? refusedNumber = null;
        if (archiveSerialNumber is long wantedNumber and > 0)
        {
            if (await db.Documents.AsNoTracking()
                    .AnyAsync(d => d.ArchiveSerialNumber == wantedNumber && d.UpdateState != UpdateState.Deleted, ct)
                    .ConfigureAwait(false))
            {
                refusedNumber = wantedNumber;
            }
            else
            {
                assignedNumber = wantedNumber;
            }
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
            ArchiveSerialNumber = assignedNumber,
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
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (document.ArchiveSerialNumber is not null && IsNumberTaken(ex))
        {
            // somebody else got the number between the check above and now: the document goes in without it
            refusedNumber = document.ArchiveSerialNumber;
            document.ArchiveSerialNumber = null;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        queue.Enqueue(document.Id);

        return IngestResult.Created(document.Id, document.ArchiveSerialNumber, refusedNumber);
    }

    /// <summary>True when a save failed because of the unique index on the archive number.</summary>
    internal static bool IsNumberTaken(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
           && (pg.ConstraintName?.Contains("ArchiveSerialNumber", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Where and for whom a file is ingested: what stays the same for every document of a file or an archive.</summary>
    private sealed record Target(
        long? StorageLocationId, long? CorrespondentId, long? DocumentTypeId, long? ProjectId, IReadOnlyCollection<long> TagIds,
        long? ActingUserId, ReviewState ReviewState, bool IsCommon, DocumentOrigin Origin, long? ImportTaskId);

    /// <summary>
    /// Ingests a file that may be an archive: a .zip is opened and each document in it is ingested like a file of its
    /// own (PDFs, images and XML, or what <paramref name="archiveExtensions"/> lists; folders, hidden files and other
    /// types are left out). Anything else is ingested as it is. An entry that cannot be read or stored does not stop
    /// the others - it is reported in the batch - while a plain file that fails throws, as <see cref="IngestAsync"/> does.
    /// The archive itself is not kept, only what was in it.
    /// <para>
    /// A PDF or an image is searched for barcodes first (unless a metadata file came with it, which already says what
    /// the document is): a sticker with an archive number gives the document that number, and a PDF with separator
    /// sheets in it - a stack of papers scanned in one go - is cut at them into one document per stretch, the separator
    /// sheets themselves are dropped. Such a part carries a hash derived from the file and its pages, so the same stack
    /// imported again is recognised as what it is.
    /// </para>
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
        var target = new Target(
            storageLocationId, correspondentId, documentTypeId, projectId, tagIds, actingUserId, reviewState, isCommon, origin, importTaskId);
        var options = config.ResolveBarcodeOptions();

        if (!DocumentArchive.IsArchive(originalFileName))
        {
            var single = new IngestBatch(fromArchive: false);
            if (sidecar is null && options.Enabled && BarcodeReader.Supports(Path.GetExtension(originalFileName)))
            {
                // the barcode reader works on the bytes of the file
                byte[] bytes;
                if (content.CanSeek && content.Length - content.Position <= Array.MaxLength)
                {
                    bytes = new byte[content.Length - content.Position];
                    await content.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
                }
                else
                {
                    using var copy = new MemoryStream();
                    await content.CopyToAsync(copy, ct).ConfigureAwait(false);
                    bytes = copy.ToArray();
                }

                await IngestScannedAsync(single, originalFileName, originalFileName, bytes, target, options, ct).ConfigureAwait(false);
                return single;
            }

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
                        bool storageLeft;
                        if (item.Sidecar is null && options.Enabled && BarcodeReader.Supports(Path.GetExtension(item.Name)))
                        {
                            storageLeft = await IngestScannedAsync(batch, item.Label, item.Name, item.Content!, target, options, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await using var stream = new MemoryStream(item.Content!, writable: false);
                            var result = await IngestAsync(
                                stream, item.Name, storageLocationId, correspondentId, documentTypeId, projectId, tagIds,
                                actingUserId, ct, reviewState, isCommon, origin, importTaskId, item.Sidecar).ConfigureAwait(false);
                            batch.Add(item.Label, result);
                            storageLeft = result.Status != IngestStatus.NoStorage;
                        }

                        if (!storageLeft)
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

    /// <summary>
    /// Ingests the content of one PDF or image after looking at its barcodes. Returns false when there is no storage
    /// to put documents in (nothing else will fit anywhere either).
    /// </summary>
    private async Task<bool> IngestScannedAsync(
        IngestBatch batch, string label, string name, byte[] bytes, Target target, BarcodeOptions options, CancellationToken ct)
    {
        var extension = Path.GetExtension(name);
        var scan = await Task.Run(() => BarcodeReader.Scan(bytes, extension, options, ct), ct).ConfigureAwait(false);
        var parts = scan?.Parts();

        if (scan is null || parts is null || !scan.HasSeparators)
        {
            // one document, the file as it is - with the number of the sticker on its first sheet, if there is one
            var whole = await IngestPartAsync(bytes, name, target, parts is { Count: 1 } ? parts[0].ArchiveNumber : null, null, ct).ConfigureAwait(false);
            batch.Add(label, whole);
            return whole.Status != IngestStatus.NoStorage;
        }

        if (parts.Count == 0)
        {
            batch.Skip(label, IngestBatch.OnlySeparatorSheets);
            return true;
        }

        // cut all parts out before anything is stored: a file that cannot be cut goes in whole instead
        var cut = new List<byte[]>();
        try
        {
            foreach (var part in parts)
            {
                cut.Add(await Task.Run(() => PdfStamper.Extract(bytes, part.Pages), ct).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The documents between the separator sheets of {File} could not be cut out; it is taken in as one document.", label);
            var whole = await IngestPartAsync(bytes, name, target, parts[0].ArchiveNumber, null, ct).ConfigureAwait(false);
            batch.Add(label, whole);
            return whole.Status != IngestStatus.NoStorage;
        }

        var originalHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var baseName = Path.GetFileNameWithoutExtension(name);
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var partName = parts.Count == 1 ? name : $"{baseName} - {i + 1}.pdf";
            var partLabel = parts.Count == 1 ? label : $"{label} [{PageRange(part.Pages)}]";
            var partHash = Convert.ToHexStringLower(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{originalHash}:{string.Join(',', part.Pages)}")));
            try
            {
                var result = await IngestPartAsync(cut[i], partName, target, part.ArchiveNumber, partHash, ct).ConfigureAwait(false);
                batch.Add(partLabel, result);
                if (result.Status == IngestStatus.NoStorage)
                {
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (parts.Count > 1 || batch.FromArchive)
            {
                logger.LogWarning(ex, "Could not store {Part}.", partLabel);
                batch.Fail(partLabel, "The document could not be stored.");
            }
        }

        return true;
    }

    private async Task<IngestResult> IngestPartAsync(
        byte[] bytes, string name, Target target, long? archiveNumber, string? contentHash, CancellationToken ct)
    {
        // exposed, so that the ingest can use the bytes without copying them
        await using var stream = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
        return await IngestAsync(
            stream, name, target.StorageLocationId, target.CorrespondentId, target.DocumentTypeId, target.ProjectId, target.TagIds,
            target.ActingUserId, ct, target.ReviewState, target.IsCommon, target.Origin, target.ImportTaskId,
            sidecar: null, archiveSerialNumber: archiveNumber, knownContentHash: contentHash).ConfigureAwait(false);
    }

    /// <summary>"3-5" or "7": the pages of a run as a person writes them.</summary>
    private static string PageRange(IReadOnlyList<int> pages)
        => pages.Count == 1 ? pages[0].ToString() : $"{pages[0]}-{pages[^1]}";

    private static async Task<string> ComputeHashAsync(Stream stream, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
