using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Inbox;

/// <summary>
/// The per-user review inbox ("Posteingang"): documents the current user owns that
/// still wait for confirmation. Their files sit in the local staging area; each row
/// lets the user correct the suggested metadata, pick the storage location and
/// confirm — which files the document (template path in the chosen location) and
/// marks it reviewed. Rows are inline forms; the pickers post plain field names.
/// </summary>
public class IndexModel : PageModel
{
    private const int PageSize = 20;

    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;
    private readonly DocumentFilingService _filing;
    private readonly DocumentStorageService _storage;

    public IndexModel(AppDbContext db, CurrentUser currentUser, DocumentFilingService filing, DocumentStorageService storage)
    {
        _db = db;
        _currentUser = currentUser;
        _filing = filing;
        _storage = storage;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; } = 1;
    public int ProcessingCount { get; private set; }
    public bool IsAdmin => _currentUser.IsAdmin;

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
        string OriginalFileName);

    public record LocationOption(long Id, string Name, bool IsDefault);

    public async Task OnGetAsync(CancellationToken ct)
    {
        ViewData["Breadcrumb"] = "Inbox";
        await LoadAsync(ct);
    }

    /// <summary>Processing state of the given own documents (polled while OCR runs).</summary>
    public async Task<IActionResult> OnGetStatusAsync(long[] ids, CancellationToken ct)
    {
        var uid = _currentUser.UserId;
        var wanted = ids ?? Array.Empty<long>();

        var rows = await _db.Documents
            .AsNoTracking()
            .Where(d => wanted.Contains(d.Id) && d.OwnerId == uid)
            .Select(d => new { d.Id, d.OcrState })
            .ToListAsync(ct);

        return new JsonResult(rows.Select(r => new { id = r.Id, state = r.OcrState.ToString().ToLowerInvariant() }));
    }

    public async Task<IActionResult> OnPostConfirmAsync(
        long id,
        string? title,
        long? documentTypeId,
        long? correspondentId,
        long? projectId,
        DateTime? documentDate,
        long[]? tagIds,
        long? storageLocationId,
        CancellationToken ct)
    {
        var uid = _currentUser.UserId;
        var document = await _db.Documents
            .Include(d => d.DocumentTags)
            .FirstOrDefaultAsync(d => d.Id == id && d.OwnerId == uid
                && d.ReviewState == ReviewState.Pending && d.UpdateState != UpdateState.Deleted, ct);

        if (document == null)
        {
            return RedirectBack();
        }

        if (document.OcrState == OcrState.Pending)
        {
            TempData["InboxError"] = $"\"{document.Title}\" is still being processed — try again in a moment.";
            return RedirectBack();
        }

        var now = DateTime.UtcNow;

        var cleanTitle = title?.Trim();
        if (!string.IsNullOrWhiteSpace(cleanTitle))
        {
            document.Title = cleanTitle;
        }

        document.DocumentTypeId = documentTypeId is long typeId
            && await _db.DocumentTypes.AnyAsync(t => t.Id == typeId && t.UpdateState != UpdateState.Deleted, ct)
            ? typeId : null;

        document.CorrespondentId = correspondentId is long corrId
            && await _db.Correspondents.AnyAsync(c => c.Id == corrId && c.UpdateState != UpdateState.Deleted, ct)
            ? corrId : null;

        document.ProjectId = projectId is long projId
            && await _db.Projects.Where(p => p.UpdateState != UpdateState.Deleted).AccessibleTo(_currentUser).AnyAsync(p => p.Id == projId, ct)
            ? projId : null;

        document.DocumentDate = documentDate.HasValue
            ? DateTime.SpecifyKind(documentDate.Value, DateTimeKind.Utc)
            : null;

        var wantedTags = tagIds ?? Array.Empty<long>();
        var validTags = wantedTags.Length == 0
            ? new List<long>()
            : await _db.Tags.Where(t => wantedTags.Contains(t.Id) && t.UpdateState != UpdateState.Deleted).Select(t => t.Id).ToListAsync(ct);
        _filing.SyncTags(document, validTags, now, uid);

        document.UpdateState = UpdateState.Updated;
        document.UpdateDate = now;
        document.UpdateUserId = uid;

        // Persist the corrections first so nothing is lost if filing fails (e.g. NAS offline).
        await _db.SaveChangesAsync(ct);

        var result = await _filing.FileAsync(document, storageLocationId, uid, ct);
        if (!result.Success)
        {
            TempData["InboxError"] = result.Error;
        }

        return RedirectBack();
    }

    public async Task<IActionResult> OnPostConfirmAllAsync(CancellationToken ct)
    {
        var uid = _currentUser.UserId;

        var pending = await _db.Documents
            .Where(d => d.OwnerId == uid && d.ReviewState == ReviewState.Pending && d.UpdateState != UpdateState.Deleted)
            .OrderBy(d => d.CreateDate)
            .ToListAsync(ct);

        int filed = 0, skipped = 0;
        string? firstError = null;

        foreach (var document in pending)
        {
            if (document.OcrState == OcrState.Pending)
            {
                skipped++;
                continue;
            }

            var result = await _filing.FileAsync(document, null, uid, ct);
            if (result.Success)
            {
                filed++;
            }
            else
            {
                firstError ??= result.Error;
            }
        }

        var failed = pending.Count - filed - skipped;
        var parts = new List<string> { $"{filed} filed" };
        if (skipped > 0)
        {
            parts.Add($"{skipped} still processing");
        }
        if (failed > 0)
        {
            parts.Add($"{failed} failed");
        }

        TempData[failed > 0 ? "InboxError" : "InboxMessage"] =
            string.Join(", ", parts) + "." + (firstError is null ? string.Empty : " " + firstError);

        return RedirectBack();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id, CancellationToken ct)
    {
        var uid = _currentUser.UserId;
        var document = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == id && d.UpdateState != UpdateState.Deleted, ct);

        if (document != null && DocumentAccess.IsOwnerOrAdmin(document, uid, _currentUser.IsAdmin))
        {
            // A staged document never reached a storage location: drop the file too, so
            // the inbox area only ever holds what is still waiting for review.
            if (document.IsStaged)
            {
                _storage.TryDeleteStaged(document.RelativePath);
                document.IsStaged = false;
                document.RelativePath = string.Empty;
            }

            document.UpdateState = UpdateState.Deleted;
            document.UpdateDate = DateTime.UtcNow;
            document.UpdateUserId = uid;
            await _db.SaveChangesAsync(ct);
        }

        return RedirectBack();
    }

    private IActionResult RedirectBack()
        => RedirectToPage(new { Search, PageNumber = PageNumber > 1 ? PageNumber : (int?)null });

    private async Task LoadAsync(CancellationToken ct)
    {
        var uid = _currentUser.UserId;

        IQueryable<Document> query = _db.Documents
            .AsNoTracking()
            .Where(d => d.OwnerId == uid && d.ReviewState == ReviewState.Pending && d.UpdateState != UpdateState.Deleted);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var pattern = $"%{Search.Trim()}%";
            query = query.Where(d =>
                EF.Functions.ILike(d.Title, pattern) ||
                (d.Correspondent != null && EF.Functions.ILike(d.Correspondent.Name, pattern)));
        }

        TotalCount = await query.CountAsync(ct);
        TotalPages = TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);
        ProcessingCount = await query.CountAsync(d => d.OcrState == OcrState.Pending, ct);

        Rows = await query
            .OrderByDescending(d => d.CreateDate)
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
                d.OriginalFileName))
            .ToListAsync(ct);

        await LoadOptionsAsync(ct);
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
}
