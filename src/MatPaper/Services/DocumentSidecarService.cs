using System.Text.Json;
using System.Text.Json.Serialization;
using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>
/// The metadata of a document as it is stored in <c>{file}.matpaper.json</c> next to the file.
/// Types, correspondents, projects and tags are written by NAME, never by id, so the file means the
/// same in any MatPaper instance and survives a rebuilt database.
/// </summary>
public sealed class DocumentSidecar
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public Guid? Token { get; set; }
    public string? Title { get; set; }
    public DateTime? DocumentDate { get; set; }
    public string? DocumentType { get; set; }
    public string? Correspondent { get; set; }
    public string? Project { get; set; }
    public List<string> Tags { get; set; } = new();

    /// <summary>True once someone took the document over (it is no longer waiting in the inbox).</summary>
    public bool Reviewed { get; set; }
    public string? OriginalFileName { get; set; }
    public string? ContentHash { get; set; }

    public string? InvoiceNumber { get; set; }
    public bool IsEInvoice { get; set; }
    public decimal? NetAmount { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? GrossAmount { get; set; }
    public string? Currency { get; set; }
    public DateTime? DueDate { get; set; }
    public string? SellerVatId { get; set; }
    public string? SellerIban { get; set; }
    public string? BuyerName { get; set; }
    public string? BuyerReference { get; set; }

    public DateTime WrittenUtc { get; set; }
    public string Generator { get; set; } = "MatPaper";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, Options);

    /// <summary>Parses a sidecar; null when the bytes are not valid JSON of this shape.</summary>
    public static DocumentSidecar? TryParse(byte[]? json)
    {
        if (json is null || json.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DocumentSidecar>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Writes, reads and applies <see cref="DocumentSidecar"/> files. Writing is opt-in per storage
/// location (<see cref="StorageLocation.WriteMetadataFiles"/>); reading happens wherever a file is
/// taken in - an import task or a storage search - and uses a sidecar whenever one is there.
/// </summary>
public sealed class DocumentSidecarService
{
    private readonly AppDbContext _db;
    private readonly DocumentStorageService _storage;
    private readonly ILogger<DocumentSidecarService> _logger;

    public DocumentSidecarService(AppDbContext db, DocumentStorageService storage, ILogger<DocumentSidecarService> logger)
    {
        _db = db;
        _storage = storage;
        _logger = logger;
    }

    /// <summary>The sidecar for a document as it is stored right now.</summary>
    public async Task<DocumentSidecar> BuildAsync(Document document, CancellationToken ct)
    {
        var typeName = document.DocumentTypeId is long tid
            ? await _db.DocumentTypes.AsNoTracking().Where(t => t.Id == tid).Select(t => t.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        var correspondentName = document.CorrespondentId is long cid
            ? await _db.Correspondents.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        var projectName = document.ProjectId is long pid
            ? await _db.Projects.AsNoTracking().Where(p => p.Id == pid).Select(p => p.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        var tags = await _db.DocumentTags.AsNoTracking()
            .Where(dt => dt.DocumentId == document.Id && dt.Tag!.UpdateState != UpdateState.Deleted)
            .Select(dt => dt.Tag!.Name)
            .OrderBy(n => n)
            .ToListAsync(ct).ConfigureAwait(false);

        return new DocumentSidecar
        {
            Token = document.Token,
            Title = document.Title,
            DocumentDate = document.DocumentDate,
            DocumentType = typeName,
            Correspondent = correspondentName,
            Project = projectName,
            Tags = tags,
            Reviewed = document.ReviewState == ReviewState.Reviewed,
            OriginalFileName = document.OriginalFileName,
            ContentHash = document.ContentHash,
            InvoiceNumber = document.InvoiceNumber,
            IsEInvoice = document.IsEInvoice,
            NetAmount = document.NetAmount,
            TaxAmount = document.TaxAmount,
            GrossAmount = document.GrossAmount,
            Currency = document.Currency,
            DueDate = document.DueDate,
            SellerVatId = document.SellerVatId,
            SellerIban = document.SellerIban,
            BuyerName = document.BuyerName,
            BuyerReference = document.BuyerReference,
            WrittenUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Writes the sidecar next to a filed document when its location asks for it. Best effort:
    /// a sidecar that cannot be written is logged and never fails the operation that triggered it.
    /// Returns true when a file was written.
    /// </summary>
    public async Task<bool> WriteAsync(Document document, CancellationToken ct)
    {
        if (document.IsStaged || string.IsNullOrEmpty(document.RelativePath) || document.StorageLocationId is not long locationId)
        {
            return false;
        }

        try
        {
            var location = await _db.StorageLocations.AsNoTracking().Include(s => s.Connection)
                .FirstOrDefaultAsync(s => s.Id == locationId && s.UpdateState != UpdateState.Deleted, ct).ConfigureAwait(false);
            if (location is null || !location.WriteMetadataFiles)
            {
                return false;
            }

            var sidecar = await BuildAsync(document, ct).ConfigureAwait(false);
            await _storage.WriteCompanionAsync(location, document.RelativePath, DocumentStorageService.MetadataCompanion, sidecar.ToJson(), ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the metadata file of document {DocumentId}.", document.Id);
            return false;
        }
    }

    /// <summary>Reads the sidecar next to a file in a storage location; null when there is none.</summary>
    public async Task<DocumentSidecar?> ReadAsync(StorageLocation location, string relativePath, CancellationToken ct)
    {
        try
        {
            return DocumentSidecar.TryParse(
                await _storage.ReadCompanionAsync(location, relativePath, DocumentStorageService.MetadataCompanion, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No readable metadata file next to {Path}.", relativePath);
            return null;
        }
    }

    private readonly Dictionary<string, long> _typeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _correspondentCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _tagCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long?> _projectCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Copies a sidecar's metadata onto a document that is about to be saved. Missing types,
    /// correspondents and tags are created by name (a project is only matched, never created: it
    /// has an owner and sharing). The document's own file facts stay untouched.
    /// </summary>
    public async Task ApplyAsync(Document document, DocumentSidecar sidecar, long? actingUserId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(sidecar.Title))
        {
            document.Title = sidecar.Title.Trim();
        }

        document.DocumentDate = sidecar.DocumentDate.HasValue ? DateTime.SpecifyKind(sidecar.DocumentDate.Value, DateTimeKind.Utc) : document.DocumentDate;

        document.DocumentTypeId = await ResolveAsync(sidecar.DocumentType, _typeCache,
            async name => (await _db.DocumentTypes.AsNoTracking().Where(t => t.UpdateState != UpdateState.Deleted && t.Name.ToLower() == name.ToLower()).Select(t => (long?)t.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false)),
            async name =>
            {
                var created = new DocumentType { Name = name, UpdateState = UpdateState.Created, CreateDate = DateTime.UtcNow, UpdateDate = DateTime.UtcNow, CreateUserId = actingUserId, UpdateUserId = actingUserId };
                _db.DocumentTypes.Add(created);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                _db.Entry(created).State = EntityState.Detached;
                return created.Id;
            }) ?? document.DocumentTypeId;

        document.CorrespondentId = await ResolveAsync(sidecar.Correspondent, _correspondentCache,
            async name => (await _db.Correspondents.AsNoTracking().Where(c => c.UpdateState != UpdateState.Deleted && c.Name.ToLower() == name.ToLower()).Select(c => (long?)c.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false)),
            async name =>
            {
                var created = new Correspondent { Name = name, UpdateState = UpdateState.Created, CreateDate = DateTime.UtcNow, UpdateDate = DateTime.UtcNow, CreateUserId = actingUserId, UpdateUserId = actingUserId };
                _db.Correspondents.Add(created);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                _db.Entry(created).State = EntityState.Detached;
                return created.Id;
            }) ?? document.CorrespondentId;

        if (!string.IsNullOrWhiteSpace(sidecar.Project))
        {
            var key = sidecar.Project.Trim();
            if (!_projectCache.TryGetValue(key, out var projectId))
            {
                projectId = await _db.Projects.AsNoTracking()
                    .Where(p => p.UpdateState != UpdateState.Deleted && p.Name.ToLower() == key.ToLower())
                    .Select(p => (long?)p.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                _projectCache[key] = projectId;
            }

            document.ProjectId = projectId ?? document.ProjectId;
        }

        foreach (var tagName in sidecar.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tagId = await ResolveAsync(tagName, _tagCache,
                async name => (await _db.Tags.AsNoTracking().Where(t => t.UpdateState != UpdateState.Deleted && t.Name.ToLower() == name.ToLower()).Select(t => (long?)t.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false)),
                async name =>
                {
                    var created = new Tag { Name = name, UpdateState = UpdateState.Created, CreateDate = DateTime.UtcNow, UpdateDate = DateTime.UtcNow, CreateUserId = actingUserId, UpdateUserId = actingUserId };
                    _db.Tags.Add(created);
                    await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                    _db.Entry(created).State = EntityState.Detached;
                    return created.Id;
                });

            if (tagId is long id && document.DocumentTags.All(dt => dt.TagId != id))
            {
                document.DocumentTags.Add(new DocumentTag
                {
                    TagId = id,
                    CreateDate = DateTime.UtcNow,
                    UpdateDate = DateTime.UtcNow,
                    CreateUserId = actingUserId,
                    UpdateUserId = actingUserId
                });
            }
        }

        document.InvoiceNumber = sidecar.InvoiceNumber ?? document.InvoiceNumber;
        if (sidecar.IsEInvoice)
        {
            document.IsEInvoice = true;
            document.NetAmount = sidecar.NetAmount;
            document.TaxAmount = sidecar.TaxAmount;
            document.GrossAmount = sidecar.GrossAmount;
            document.Currency = sidecar.Currency;
            document.DueDate = sidecar.DueDate.HasValue ? DateTime.SpecifyKind(sidecar.DueDate.Value, DateTimeKind.Utc) : null;
            document.SellerVatId = sidecar.SellerVatId;
            document.SellerIban = sidecar.SellerIban;
            document.BuyerName = sidecar.BuyerName;
            document.BuyerReference = sidecar.BuyerReference;
        }

        if (sidecar.Reviewed)
        {
            document.ReviewState = ReviewState.Reviewed;
        }
    }

    private static async Task<long?> ResolveAsync(
        string? name, Dictionary<string, long> cache, Func<string, Task<long?>> find, Func<string, Task<long>> create)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var key = name.Trim();
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var id = await find(key).ConfigureAwait(false) ?? await create(key).ConfigureAwait(false);
        cache[key] = id;
        return id;
    }
}
