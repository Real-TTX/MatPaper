using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.Inbox;

/// <summary>
/// The one inbox ("Eingang"): every document that has not been taken into the archive yet,
/// whatever brought it here. Two kinds of row share the list:
/// <list type="bullet">
/// <item><b>Staged</b> — uploaded, scanned or imported. The file waits in the local staging
/// area and is moved into a storage location when the row is confirmed.</item>
/// <item><b>Found</b> — discovered by a storage search. The file already lies inside a
/// storage location; confirming adopts it where it is, or re-files it by the template.</item>
/// </list>
/// Rows are inline forms; a shared bulk form below the list applies metadata and one action
/// to the selected rows.
/// </summary>
public class IndexModel : PageModel
{
    private const int PageSize = 20;

    /// <summary>Upper bound for anything that loops over documents inside one request.</summary>
    private const int BatchLimit = 50;

    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;
    private readonly DocumentFilingService _filing;
    private readonly DocumentStorageService _storage;
    private readonly DocumentProcessingQueue _queue;
    private readonly IStringLocalizer<SharedResource> _l;

    public IndexModel(
        AppDbContext db,
        CurrentUser currentUser,
        DocumentFilingService filing,
        DocumentStorageService storage,
        DocumentProcessingQueue queue,
        IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _currentUser = currentUser;
        _filing = filing;
        _storage = storage;
        _queue = queue;
        _l = l;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    /// <summary>Filter by how the document arrived.</summary>
    [BindProperty(SupportsGet = true)]
    public DocumentOrigin? Origin { get; set; }

    /// <summary>Filter found documents by the storage location they were found in.</summary>
    [BindProperty(SupportsGet = true)]
    public long? LocationId { get; set; }

    /// <summary>Filter found documents by a folder prefix inside the location.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Folder { get; set; }

    /// <summary>Order of the list: added (newest first), oldest, date (document date), title.</summary>
    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "added";

    /// <summary>"open" (waiting) or "ignored" (waved away).</summary>
    [BindProperty(SupportsGet = true)]
    public string State { get; set; } = "open";

    /// <summary>Administrators can work through every owner's inbox.</summary>
    [BindProperty(SupportsGet = true)]
    public bool AllOwners { get; set; }

    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; } = 1;
    public int ProcessingCount { get; private set; }
    public int FoundCount { get; private set; }
    public bool IsAdmin => _currentUser.IsAdmin;
    public bool ShowingIgnored => string.Equals(State, "ignored", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<InboxRow> Rows { get; private set; } = Array.Empty<InboxRow>();

    public List<SelectListItem> DocumentTypeOptions { get; private set; } = new();
    public List<SelectListItem> CorrespondentOptions { get; private set; } = new();
    public List<SelectListItem> ProjectOptions { get; private set; } = new();
    public List<SelectListItem> TagItems { get; private set; } = new();
    public IReadOnlyList<LocationOption> StorageLocations { get; private set; } = Array.Empty<LocationOption>();
    public long? DefaultLocationId { get; private set; }
    public bool HasStorage => StorageLocations.Count > 0;

    // Expression-only members backing the per-row pickers: the picker derives its field
    // name from these, the actual value comes from the row (value="…" attribute).
    public long? DocumentTypeId { get; set; }
    public long? CorrespondentId { get; set; }
    public long? ProjectId { get; set; }
    public long[] TagIds { get; set; } = Array.Empty<long>();

    // …and the same for the bulk form below the list.
    public long? BulkDocumentTypeId { get; set; }
    public long? BulkCorrespondentId { get; set; }
    public long? BulkProjectId { get; set; }
    public long[] BulkTagIds { get; set; } = Array.Empty<long>();

    public record InboxRow(
        long Id,
        Guid Token,
        string Title,
        string? ThumbnailPath,
        long? DocumentTypeId,
        long? CorrespondentId,
        long? ProjectId,
        DateTime? DocumentDate,
        IReadOnlyList<long> TagIds,
        OcrState OcrState,
        DateTime AddedDate,
        long? StorageLocationId,
        bool IsStaged,
        string OriginalFileName,
        DocumentOrigin Origin,
        string RelativePath,
        string? LocationName,
        long FileSize,
        DateTime? FileModifiedUtc,
        string? OwnerName);

    public record LocationOption(long Id, string Name, bool IsDefault);

    /// <summary>What the shared row partial needs: the row plus the page it lives on.</summary>
    public record RowContext(IndexModel Page, InboxRow Row)
    {
        // Expression-only members so the row pickers post plain field names
        // (CorrespondentId, …) that the Confirm handler binds directly.
        public long? CorrespondentId => Row.CorrespondentId;
        public long? DocumentTypeId => Row.DocumentTypeId;
        public long? ProjectId => Row.ProjectId;
        public long[] TagIds => Row.TagIds.ToArray();
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        ViewData["Breadcrumb"] = "Inbox";
        await LoadAsync(ct);
    }

    /// <summary>Processing state of the given documents (polled while OCR runs).</summary>
    public async Task<IActionResult> OnGetStatusAsync(long[] ids, CancellationToken ct)
    {
        var wanted = ids ?? Array.Empty<long>();

        var rows = await _db.Documents
            .AsNoTracking()
            .AccessibleTo(_currentUser)
            .Where(d => wanted.Contains(d.Id))
            .Select(d => new { d.Id, d.OcrState })
            .ToListAsync(ct);

        return new JsonResult(rows.Select(r => new { id = r.Id, state = r.OcrState.ToString().ToLowerInvariant() }));
    }

    // ----- Single row -------------------------------------------------------

    public async Task<IActionResult> OnPostConfirmAsync(
        long id,
        string? title,
        long? documentTypeId,
        long? correspondentId,
        long? projectId,
        DateTime? documentDate,
        long[]? tagIds,
        long? storageLocationId,
        string? mode,
        CancellationToken ct)
    {
        var document = await LoadEditableAsync(id, ct);
        if (document is null)
        {
            return RedirectBack();
        }

        // Only a waiting document can be taken over. Anything else (already archived,
        // ignored) must not be re-filed and must not have its metadata overwritten.
        if (document.ReviewState != ReviewState.Pending)
        {
            return RedirectBack();
        }

        if (document.OcrState == OcrState.Pending)
        {
            this.Notify(_l["\"{0}\" is still being processed — try again in a moment.", document.Title].Value, NoticeKind.Warn);
            return RedirectBack();
        }

        var uid = _currentUser.UserId;
        var now = DateTime.UtcNow;

        var cleanTitle = title?.Trim();
        if (!string.IsNullOrWhiteSpace(cleanTitle))
        {
            document.Title = cleanTitle;
        }

        document.DocumentTypeId = await ValidTypeAsync(documentTypeId, ct);
        document.CorrespondentId = await ValidCorrespondentAsync(correspondentId, ct);
        document.ProjectId = await ValidProjectAsync(projectId, ct);

        document.DocumentDate = documentDate.HasValue
            ? DateTime.SpecifyKind(documentDate.Value, DateTimeKind.Utc)
            : null;

        _filing.SyncTags(document, await ValidTagsAsync(tagIds, ct), now, uid);

        document.UpdateState = UpdateState.Updated;
        document.UpdateDate = now;
        document.UpdateUserId = uid;

        // Persist the corrections first so nothing is lost if filing fails (e.g. NAS offline).
        await _db.SaveChangesAsync(ct);

        var filingMode = document.IsStaged
            ? FilingMode.FromStaging
            : string.Equals(mode, "refile", StringComparison.OrdinalIgnoreCase)
                ? FilingMode.RefileByTemplate
                : FilingMode.KeepInPlace;

        var result = await _filing.FileAsync(document, storageLocationId, filingMode, uid, ct);
        if (!result.Success)
        {
            this.Notify(result.Error, NoticeKind.Danger);
            return RedirectBack();
        }

        // A found file has never been looked at. Confirming one is a deliberate,
        // single-document act, so this is the moment to start text recognition.
        if (document.OcrState == OcrState.Deferred)
        {
            document.OcrState = OcrState.Pending;
            await _db.SaveChangesAsync(ct);
            _queue.Enqueue(document.Id);
        }

        return RedirectBack();
    }

    /// <summary>Wave a document away. The file is never touched; the row blocks re-discovery.</summary>
    public async Task<IActionResult> OnPostIgnoreAsync(long id, CancellationToken ct)
    {
        var document = await LoadEditableAsync(id, ct);
        if (document is null)
        {
            return RedirectBack();
        }

        if (document.IsStaged)
        {
            this.Notify(_l["A document that is still in the staging area cannot be ignored — take it over or delete it."].Value, NoticeKind.Warn);
            return RedirectBack();
        }

        document.ReviewState = ReviewState.Ignored;
        document.UpdateState = UpdateState.Updated;
        document.UpdateDate = DateTime.UtcNow;
        document.UpdateUserId = _currentUser.UserId;
        await _db.SaveChangesAsync(ct);

        return RedirectBack();
    }

    public async Task<IActionResult> OnPostRestoreAsync(long id, CancellationToken ct)
    {
        var document = await LoadEditableAsync(id, ct);
        if (document is null || document.ReviewState != ReviewState.Ignored)
        {
            return RedirectBack();
        }

        document.ReviewState = ReviewState.Pending;
        document.UpdateState = UpdateState.Updated;
        document.UpdateDate = DateTime.UtcNow;
        document.UpdateUserId = _currentUser.UserId;
        await _db.SaveChangesAsync(ct);

        return RedirectBack();
    }

    public async Task<IActionResult> OnPostAnalyzeAsync(long id, CancellationToken ct)
    {
        var document = await LoadEditableAsync(id, ct);
        if (document is null || document.OcrState != OcrState.Deferred)
        {
            return RedirectBack();
        }

        document.OcrState = OcrState.Pending;
        document.UpdateDate = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _queue.Enqueue(document.Id);

        this.Notify(_l["Text recognition started for {0} document(s).", 1].Value);
        return RedirectBack();
    }

    /// <summary>Delete and put the content on the blocklist: it is skipped when it turns up again.</summary>
    public async Task<IActionResult> OnPostBlockAsync(long id, CancellationToken ct)
    {
        var document = await LoadEditableAsync(id, ct);
        if (document is not null && DocumentAccess.IsOwnerOrAdmin(document, _currentUser.UserId, IsAdmin))
        {
            await BlocklistService.BlockAsync(_db, document, _currentUser.UserId, ct);
            this.Notify(_l["Deleted and blocked: it is skipped when it turns up again (see the blocklist)."].Value);
        }

        return await OnPostDeleteAsync(id, ct);
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id, CancellationToken ct)
    {
        var document = await LoadEditableAsync(id, ct);

        // Deleting is not shared work: it stays with the owner even in the common area.
        if (document is not null && !DocumentAccess.IsOwnerOrAdmin(document, _currentUser.UserId, IsAdmin))
        {
            this.Notify(_l["You are not allowed to change this document."].Value, NoticeKind.Warn);
            return RedirectBack();
        }

        if (document is not null)
        {
            // A staged document never reached a storage location: drop the file too, so
            // the staging area only ever holds what is still waiting for review.
            if (document.IsStaged)
            {
                _storage.TryDeleteStaged(document.RelativePath);
                document.IsStaged = false;
                document.RelativePath = string.Empty;
            }

            document.UpdateState = UpdateState.Deleted;
            document.UpdateDate = DateTime.UtcNow;
            document.UpdateUserId = _currentUser.UserId;
            await _db.SaveChangesAsync(ct);
        }

        return RedirectBack();
    }

    // ----- Many rows --------------------------------------------------------

    /// <summary>
    /// One handler for the bulk form so typed-in metadata can never be lost by pressing a
    /// different button. Metadata is additive: a field left empty changes nothing.
    /// </summary>
    public async Task<IActionResult> OnPostBulkAsync(
        long[]? selectedIds,
        string action,
        long? bulkCorrespondentId,
        long? bulkDocumentTypeId,
        long? bulkProjectId,
        long[]? bulkTagIds,
        string? mode,
        bool allMatching,
        CancellationToken ct)
    {
        var uid = _currentUser.UserId;
        List<Document> documents;
        if (allMatching)
        {
            // "Select all N": everything the list shows on every page, one batch at a time.
            // Without a filter a large backlog would be a single endless request.
            var query = FilteredQuery(tracked: true);
            var total = await query.CountAsync(ct);
            if (total > BatchLimit && !HasFilter)
            {
                this.Notify(_l["Narrow the list with a filter before taking everything over."].Value, NoticeKind.Warn);
                return RedirectBack();
            }

            documents = await query
                .Include(d => d.DocumentTags)
                .OrderBy(d => d.CreateDate)
                .Take(BatchLimit)
                .ToListAsync(ct);
        }
        else
        {
            var ids = (selectedIds ?? Array.Empty<long>()).Distinct().ToList();
            if (ids.Count == 0)
            {
                this.Notify(_l["Nothing was selected."].Value, NoticeKind.Warn);
                return RedirectBack();
            }

            if (ids.Count > BatchLimit)
            {
                this.Notify(_l["Select at most {0} documents at once.", BatchLimit].Value, NoticeKind.Warn);
                return RedirectBack();
            }

            documents = await _db.Documents
                .Include(d => d.DocumentTags)
                .AccessibleTo(_currentUser)
                .Where(d => ids.Contains(d.Id) && d.UpdateState != UpdateState.Deleted)
                .ToListAsync(ct);
        }

        var editable = new List<Document>(documents.Count);
        foreach (var candidate in documents)
        {
            if (await DocumentAccess.CanEditAsync(_db, candidate, uid, IsAdmin, ct))
            {
                editable.Add(candidate);
            }
        }

        documents = editable;
        if (documents.Count == 0)
        {
            this.Notify(_l["You are not allowed to change this document."].Value, NoticeKind.Warn);
            return RedirectBack();
        }

        switch (action)
        {
            case "ignore":
                return await BulkIgnoreAsync(documents, ct);
            case "analyze":
                return await BulkAnalyzeAsync(documents, ct);
            default:
                return await BulkConfirmAsync(documents, bulkCorrespondentId, bulkDocumentTypeId, bulkProjectId, bulkTagIds, mode, ct);
        }
    }

    private async Task<IActionResult> BulkConfirmAsync(
        List<Document> documents,
        long? correspondentId,
        long? documentTypeId,
        long? projectId,
        long[]? tagIds,
        string? mode,
        CancellationToken ct)
    {
        var uid = _currentUser.UserId;
        var now = DateTime.UtcNow;

        var validType = await ValidTypeAsync(documentTypeId, ct);
        var validCorrespondent = await ValidCorrespondentAsync(correspondentId, ct);
        var validProject = await ValidProjectAsync(projectId, ct);
        var validTags = await ValidTagsAsync(tagIds, ct);

        int filed = 0, skipped = 0, deferred = 0;
        string? firstError = null;

        foreach (var document in documents)
        {
            if (document.ReviewState != ReviewState.Pending || document.OcrState == OcrState.Pending)
            {
                skipped++;
                continue;
            }

            if (validType is not null)
            {
                document.DocumentTypeId = validType;
            }
            if (validCorrespondent is not null)
            {
                document.CorrespondentId = validCorrespondent;
            }
            if (validProject is not null)
            {
                document.ProjectId = validProject;
            }
            if (validTags.Count > 0)
            {
                var merged = document.DocumentTags.Select(t => t.TagId).Union(validTags).ToList();
                _filing.SyncTags(document, merged, now, uid);
            }

            document.UpdateState = UpdateState.Updated;
            document.UpdateDate = now;
            document.UpdateUserId = uid;

            var filingMode = document.IsStaged
                ? FilingMode.FromStaging
                : string.Equals(mode, "refile", StringComparison.OrdinalIgnoreCase)
                    ? FilingMode.RefileByTemplate
                    : FilingMode.KeepInPlace;

            var result = await _filing.FileAsync(document, null, filingMode, uid, ct);
            if (result.Success)
            {
                filed++;
                if (document.OcrState == OcrState.Deferred)
                {
                    deferred++;
                }
            }
            else
            {
                firstError ??= result.Error;
            }
        }

        var parts = new List<string> { _l["{0} filed", filed].Value };
        if (skipped > 0)
        {
            parts.Add(_l["{0} still processing", skipped].Value);
        }

        var message = string.Join(", ", parts) + ".";
        if (deferred > 0)
        {
            message += " " + _l["Taken over without text recognition."].Value;
        }
        if (firstError is not null)
        {
            message += " " + firstError;
        }

        this.Notify(message, firstError is null ? NoticeKind.Ok : NoticeKind.Warn);
        return RedirectBack();
    }

    private async Task<IActionResult> BulkIgnoreAsync(List<Document> documents, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        int ignored = 0, staged = 0;

        foreach (var document in documents)
        {
            // Ignoring a staged upload would strand its file in the staging area forever.
            if (document.IsStaged || document.ReviewState != ReviewState.Pending)
            {
                staged++;
                continue;
            }

            document.ReviewState = ReviewState.Ignored;
            document.UpdateState = UpdateState.Updated;
            document.UpdateDate = now;
            document.UpdateUserId = _currentUser.UserId;
            ignored++;
        }

        await _db.SaveChangesAsync(ct);
        this.Notify(_l["{0} ignored, {1} skipped (still in the staging area).", ignored, staged].Value,
            staged > 0 ? NoticeKind.Warn : NoticeKind.Ok);
        return RedirectBack();
    }

    private async Task<IActionResult> BulkAnalyzeAsync(List<Document> documents, CancellationToken ct)
    {
        var started = new List<long>();
        foreach (var document in documents.Where(d => d.OcrState == OcrState.Deferred))
        {
            document.OcrState = OcrState.Pending;
            document.UpdateDate = DateTime.UtcNow;
            started.Add(document.Id);
        }

        await _db.SaveChangesAsync(ct);
        foreach (var id in started)
        {
            _queue.Enqueue(id);
        }

        this.Notify(_l["Text recognition started for {0} document(s).", started.Count].Value);
        return RedirectBack();
    }

    // ----- Loading ----------------------------------------------------------

    private bool HasFilter =>
        !string.IsNullOrWhiteSpace(Search) || Origin is not null || LocationId is not null
        || !string.IsNullOrWhiteSpace(Folder);

    private IQueryable<Document> FilteredQuery(bool tracked)
    {
        var uid = _currentUser.UserId;
        IQueryable<Document> query = tracked ? _db.Documents : _db.Documents.AsNoTracking();

        query = ShowingIgnored
            ? query.IgnoredOf(uid, IsAdmin, AllOwners)
            : query.InInboxOf(uid, IsAdmin, AllOwners);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var pattern = $"%{Search.Trim()}%";
            query = query.Where(d =>
                EF.Functions.ILike(d.Title, pattern) ||
                EF.Functions.ILike(d.OriginalFileName, pattern) ||
                (d.Correspondent != null && EF.Functions.ILike(d.Correspondent.Name, pattern)));
        }

        if (Origin is DocumentOrigin origin)
        {
            query = query.Where(d => d.Origin == origin);
        }

        if (LocationId is long locationId)
        {
            query = query.Where(d => !d.IsStaged && d.StorageLocationId == locationId);
        }

        if (!string.IsNullOrWhiteSpace(Folder))
        {
            var prefix = Folder.Trim().Trim('/');
            if (prefix.Length > 0)
            {
                var like = prefix.Replace("%", "\\%").Replace("_", "\\_") + "/%";
                query = query.Where(d => !d.IsStaged && EF.Functions.Like(d.RelativePath, like));
            }
        }

        return query;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var uid = _currentUser.UserId;
        var query = FilteredQuery(tracked: false);

        TotalCount = await query.CountAsync(ct);
        TotalPages = TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);

        // Deferred is not "in progress" — nothing will process it until the user asks.
        ProcessingCount = await query.CountAsync(d => d.OcrState == OcrState.Pending, ct);
        FoundCount = await query.CountAsync(d => d.Origin == DocumentOrigin.StorageScan, ct);

        var ordered = Sort switch
        {
            "oldest" => query.OrderBy(d => d.CreateDate),
            "title" => query.OrderBy(d => d.Title).ThenByDescending(d => d.CreateDate),
            "date" => query.OrderByDescending(d => d.DocumentDate).ThenByDescending(d => d.CreateDate),
            _ => query.OrderByDescending(d => d.CreateDate)
        };

        Rows = await ordered
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .Select(d => new InboxRow(
                d.Id,
                d.Token,
                d.Title,
                d.ThumbnailPath,
                d.DocumentTypeId,
                d.CorrespondentId,
                d.ProjectId,
                d.DocumentDate,
                d.DocumentTags.Select(t => t.TagId).ToList(),
                d.OcrState,
                d.CreateDate,
                d.StorageLocationId,
                d.IsStaged,
                d.OriginalFileName,
                d.Origin,
                d.RelativePath,
                d.StorageLocation != null ? d.StorageLocation.Name : null,
                d.FileSize,
                d.FileModifiedUtc,
                d.Owner != null ? d.Owner.DisplayName : null))
            .ToListAsync(ct);

        await TakeOverExactCorrespondentsAsync(ct);
        await LoadOptionsAsync(ct);
    }

    /// <summary>
    /// A document without a correspondent whose text names one that already exists (the same name, legal form and
    /// punctuation aside) gets that correspondent right away - it is not a "suggestion" anymore. This also catches
    /// documents that were analysed before the correspondent existed.
    /// </summary>
    private async Task TakeOverExactCorrespondentsAsync(CancellationToken ct)
    {
        var missing = Rows.Where(r => r.CorrespondentId is null && r.OcrState != OcrState.Pending && r.OcrState != OcrState.Deferred)
            .Select(r => r.Id).ToList();
        if (missing.Count == 0) { return; }

        var known = (await _db.Correspondents.AsNoTracking()
                .Where(c => c.UpdateState != UpdateState.Deleted)
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(ct))
            .Select(c => (c.Id, c.Name)).ToList();
        if (known.Count == 0) { return; }

        var texts = await _db.Documents.AsNoTracking()
            .Where(d => missing.Contains(d.Id) && d.OcrText != null && d.OcrText != "")
            .Select(d => new { d.Id, d.OcrText })
            .ToListAsync(ct);

        var assigned = new Dictionary<long, long>();
        foreach (var text in texts)
        {
            foreach (var name in CorrespondentSuggester.Suggest(text.OcrText, 4))
            {
                var hit = known.FirstOrDefault(k => CorrespondentSuggester.SameName(k.Name, name));
                if (hit.Id != 0) { assigned[text.Id] = hit.Id; break; }
            }
        }

        if (assigned.Count == 0) { return; }

        var now = DateTime.UtcNow;
        foreach (var (documentId, correspondentId) in assigned)
        {
            await _db.Documents
                .Where(d => d.Id == documentId && d.CorrespondentId == null)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(d => d.CorrespondentId, correspondentId)
                    .SetProperty(d => d.UpdateDate, now), ct);
        }

        Rows = Rows.Select(r => assigned.TryGetValue(r.Id, out var id) ? r with { CorrespondentId = id } : r).ToList();
    }

    private async Task LoadOptionsAsync(CancellationToken ct)
    {
        DocumentTypeOptions = await _db.DocumentTypes.AsNoTracking()
            .Where(t => t.UpdateState != UpdateState.Deleted)
            .OrderBy(t => t.Name)
            .Select(t => new SelectListItem(t.Name, t.Id.ToString()))
            .ToListAsync(ct);

        CorrespondentOptions = await _db.Correspondents.AsNoTracking()
            .Where(c => c.UpdateState != UpdateState.Deleted)
            .OrderBy(c => c.Name)
            .Select(c => new SelectListItem(c.Name, c.Id.ToString()))
            .ToListAsync(ct);

        ProjectOptions = await _db.Projects.AsNoTracking()
            .Where(p => p.UpdateState != UpdateState.Deleted)
            .AccessibleTo(_currentUser)
            .OrderBy(p => p.Name)
            .Select(p => new SelectListItem(p.Name, p.Id.ToString()))
            .ToListAsync(ct);

        TagItems = await _db.Tags.AsNoTracking()
            .Where(t => t.UpdateState != UpdateState.Deleted)
            .OrderBy(t => t.Name)
            .Select(t => new SelectListItem(t.Name, t.Id.ToString()))
            .ToListAsync(ct);

        StorageLocations = await _db.StorageLocations.AsNoTracking()
            .Where(s => s.UpdateState != UpdateState.Deleted)
            .OrderByDescending(s => s.IsDefault)
            .ThenBy(s => s.Name)
            .Select(s => new LocationOption(s.Id, s.Name, s.IsDefault))
            .ToListAsync(ct);

        DefaultLocationId = StorageLocations.FirstOrDefault()?.Id;
    }

    // ----- Helpers ----------------------------------------------------------

    /// <summary>Loads a document the current user may change, or warns and returns null.</summary>
    private async Task<Document?> LoadEditableAsync(long id, CancellationToken ct)
    {
        var document = await _db.Documents
            .Include(d => d.DocumentTags)
            .FirstOrDefaultAsync(d => d.Id == id && d.UpdateState != UpdateState.Deleted, ct);

        if (document is null
            || !await DocumentAccess.CanEditAsync(_db, document, _currentUser.UserId, IsAdmin, ct))
        {
            this.Notify(_l["You are not allowed to change this document."].Value, NoticeKind.Warn);
            return null;
        }

        return document;
    }

    private async Task<long?> ValidTypeAsync(long? id, CancellationToken ct)
        => id is long value && await _db.DocumentTypes.AnyAsync(t => t.Id == value && t.UpdateState != UpdateState.Deleted, ct)
            ? value : null;

    private async Task<long?> ValidCorrespondentAsync(long? id, CancellationToken ct)
        => id is long value && await _db.Correspondents.AnyAsync(c => c.Id == value && c.UpdateState != UpdateState.Deleted, ct)
            ? value : null;

    private async Task<long?> ValidProjectAsync(long? id, CancellationToken ct)
        => id is long value && await _db.Projects.Where(p => p.UpdateState != UpdateState.Deleted).AccessibleTo(_currentUser).AnyAsync(p => p.Id == value, ct)
            ? value : null;

    private async Task<List<long>> ValidTagsAsync(long[]? tagIds, CancellationToken ct)
    {
        var wanted = tagIds ?? Array.Empty<long>();
        return wanted.Length == 0
            ? new List<long>()
            : await _db.Tags.Where(t => wanted.Contains(t.Id) && t.UpdateState != UpdateState.Deleted).Select(t => t.Id).ToListAsync(ct);
    }

    private IActionResult RedirectBack()
        => RedirectToPage(new
        {
            Search,
            Origin,
            LocationId,
            Folder,
            Sort = string.Equals(Sort, "added", StringComparison.OrdinalIgnoreCase) ? null : Sort,
            State = ShowingIgnored ? "ignored" : null,
            AllOwners = AllOwners ? true : (bool?)null,
            PageNumber = PageNumber > 1 ? PageNumber : (int?)null
        });
}
