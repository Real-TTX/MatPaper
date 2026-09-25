using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.Documents;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DocumentStorageService _storage;
    private readonly CurrentUser _currentUser;
    private readonly ShareLinkService _shareLinks;
    private readonly DocumentAnalysisService _analysis;
    private readonly DocumentFilingService _filing;
    private readonly IStringLocalizer<SharedResource> _l;

    public EditModel(
        AppDbContext db,
        DocumentStorageService storage,
        CurrentUser currentUser,
        ShareLinkService shareLinks,
        DocumentAnalysisService analysis,
        DocumentFilingService filing,
        IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _storage = storage;
        _currentUser = currentUser;
        _shareLinks = shareLinks;
        _analysis = analysis;
        _filing = filing;
        _l = l;
    }

    /// <summary>Where "Back" and post-save redirects go (local URLs only); e.g. /Inbox.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string BackUrl => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/Documents";

    /// <summary>True while the file still sits in the inbox staging area (not filed yet).</summary>
    public bool IsStaged { get; private set; }

    public List<ShareLink> ShareLinks { get; private set; } = new();
    public string ShareBaseUrl { get; private set; } = string.Empty;
    public string? InvoiceNumber { get; private set; }

    // Ownership / sharing / review state for the current viewer.
    public bool IsOwner { get; private set; }
    public bool CanEdit { get; private set; }
    public ReviewState ReviewState { get; private set; }
    public bool IsCommon { get; private set; }
    public string? OwnerName { get; private set; }
    public List<UserShareView> UserShares { get; private set; } = new();
    public List<SelectListItem> ShareUserOptions { get; private set; } = new();

    /// <summary>Bound only for the "share with user" picker on the edit page.</summary>
    public long? ShareUserId { get; set; }

    public record UserShareView(long ShareId, long UserId, string UserName, bool CanEdit);

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public long[] SelectedTagIds { get; set; } = Array.Empty<long>();

    // Read-only facts for the preview panel.
    public Guid Token { get; private set; }
    public string? ThumbnailPath { get; private set; }
    public OcrState OcrState { get; private set; }
    public int PageCount { get; private set; }
    public long FileSize { get; private set; }
    public string OriginalFileName { get; private set; } = string.Empty;
    public DateTime AddedDate { get; private set; }

    // Advanced / raw facts.
    public string? OcrText { get; private set; }
    public string? ContentHash { get; private set; }
    public string? RelativePath { get; private set; }
    public string? StorageLocationName { get; private set; }

    // Option lists.
    public List<SelectListItem> DocumentTypeOptions { get; private set; } = new();
    public List<SelectListItem> CorrespondentOptions { get; private set; } = new();
    public List<SelectListItem> ProjectOptions { get; private set; } = new();
    public List<SelectListItem> StorageLocationOptions { get; private set; } = new();
    public List<TagOption> TagOptions { get; private set; } = new();
    public List<SelectListItem> TagItems { get; private set; } = new();

    public class InputModel
    {
        public string Title { get; set; } = string.Empty;
        public DateTime? DocumentDate { get; set; }
        public long? DocumentTypeId { get; set; }
        public long? CorrespondentId { get; set; }
        public long? ProjectId { get; set; }
        public long? StorageLocationId { get; set; }
    }

    public record TagOption(long Id, string Name, string? Color, bool Selected);

    public async Task<IActionResult> OnGetAsync()
    {
        ViewData["Breadcrumb"] = "Documents / Edit";

        if (Id == 0)
        {
            return NotFound();
        }

        var document = await _db.Documents
            .AsNoTracking()
            .Include(d => d.DocumentTags)
            .Include(d => d.Owner)
            .Include(d => d.StorageLocation)
            .Include(d => d.Shares).ThenInclude(s => s.User)
            .AsSplitQuery()
            .AccessibleTo(_currentUser)
            .FirstOrDefaultAsync(d => d.Id == Id && d.UpdateState != UpdateState.Deleted);

        if (document == null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            Title = document.Title,
            DocumentDate = document.DocumentDate,
            DocumentTypeId = document.DocumentTypeId,
            CorrespondentId = document.CorrespondentId,
            ProjectId = document.ProjectId,
            StorageLocationId = document.StorageLocationId
        };

        SelectedTagIds = document.DocumentTags.Select(dt => dt.TagId).ToArray();

        // Staged documents preselect the default location as their filing target.
        if (document.IsStaged && Input.StorageLocationId is null)
        {
            Input.StorageLocationId = await _db.StorageLocations
                .Where(s => s.UpdateState != UpdateState.Deleted)
                .OrderByDescending(s => s.IsDefault)
                .ThenBy(s => s.Id)
                .Select(s => (long?)s.Id)
                .FirstOrDefaultAsync();
        }

        await RenderAsync(document);
        return Page();
    }

    /// <summary>Fills everything the view needs (preview facts, option lists, access/sharing state).</summary>
    private async Task RenderAsync(Document document)
    {
        IsStaged = document.IsStaged;
        FillPreview(document);
        await BuildOptionListsAsync(SelectedTagIds);
        await BuildAccessViewAsync(document, HttpContext.RequestAborted);
        ShareLinks = await _shareLinks.ListForDocumentAsync(Id, HttpContext.RequestAborted);
        ShareBaseUrl = $"{Request.Scheme}://{Request.Host}";
    }

    /// <summary>Populates the ownership / sharing / review view-model for the current viewer.</summary>
    private async Task BuildAccessViewAsync(Document document, CancellationToken ct)
    {
        IsOwner = DocumentAccess.IsOwnerOrAdmin(document, _currentUser.UserId, _currentUser.IsAdmin);
        CanEdit = await DocumentAccess.CanEditAsync(_db, document, _currentUser.UserId, _currentUser.IsAdmin, ct);
        ReviewState = document.ReviewState;
        IsCommon = document.IsCommon;
        OwnerName = document.Owner?.DisplayName ?? document.Owner?.Username;

        UserShares = document.Shares
            .Where(s => s.UpdateState != UpdateState.Deleted)
            .Select(s => new UserShareView(
                s.Id,
                s.UserId,
                s.User != null ? (s.User.DisplayName ?? s.User.Username) : $"#{s.UserId}",
                s.CanEdit))
            .OrderBy(s => s.UserName)
            .ToList();

        if (IsOwner)
        {
            var alreadyShared = UserShares.Select(s => s.UserId).ToHashSet();
            var users = await _db.Users
                .AsNoTracking()
                .Where(u => u.IsActive && u.Id != document.OwnerId)
                .OrderBy(u => u.DisplayName)
                .Select(u => new { u.Id, u.DisplayName, u.Username })
                .ToListAsync(ct);

            ShareUserOptions = users
                .Where(u => !alreadyShared.Contains(u.Id))
                .Select(u => new SelectListItem(u.DisplayName ?? u.Username, u.Id.ToString()))
                .ToList();
        }
    }

    public async Task<IActionResult> OnPostCreateShareAsync(int expiryDays)
    {
        var document = await LoadAccessibleAsync(HttpContext.RequestAborted);
        if (document == null)
        {
            return NotFound();
        }

        if (!DocumentAccess.IsOwnerOrAdmin(document, _currentUser.UserId, _currentUser.IsAdmin))
        {
            return Forbid();
        }

        DateTime? expiresAt = expiryDays > 0 ? DateTime.UtcNow.AddDays(expiryDays) : null;
        await _shareLinks.CreateAsync(Id, expiresAt, HttpContext.RequestAborted);

        return RedirectToPage("Edit", new { id = Id });
    }

    public async Task<IActionResult> OnPostRevokeShareAsync(long shareLinkId)
    {
        var document = await LoadAccessibleAsync(HttpContext.RequestAborted);
        if (document == null)
        {
            return NotFound();
        }

        if (!DocumentAccess.IsOwnerOrAdmin(document, _currentUser.UserId, _currentUser.IsAdmin))
        {
            return Forbid();
        }

        // Only revoke a link that actually belongs to this document.
        var belongs = await _db.ShareLinks.AnyAsync(l => l.Id == shareLinkId && l.DocumentId == Id, HttpContext.RequestAborted);
        if (belongs)
        {
            await _shareLinks.RevokeAsync(shareLinkId, HttpContext.RequestAborted);
        }

        return RedirectToPage("Edit", new { id = Id });
    }

    public async Task<IActionResult> OnPostShareUserAsync(long shareUserId, bool canEdit)
    {
        var document = await LoadAccessibleAsync(HttpContext.RequestAborted);
        if (document == null)
        {
            return NotFound();
        }

        if (!DocumentAccess.IsOwnerOrAdmin(document, _currentUser.UserId, _currentUser.IsAdmin))
        {
            return Forbid();
        }

        // Cannot share with the owner or a non-existent/inactive user.
        var targetExists = await _db.Users.AnyAsync(u => u.Id == shareUserId && u.IsActive, HttpContext.RequestAborted);
        if (!targetExists || shareUserId == document.OwnerId)
        {
            return RedirectToPage("Edit", new { id = Id });
        }

        var now = DateTime.UtcNow;
        var existing = await _db.DocumentShares
            .FirstOrDefaultAsync(s => s.DocumentId == Id && s.UserId == shareUserId, HttpContext.RequestAborted);

        if (existing == null)
        {
            _db.DocumentShares.Add(new DocumentShare
            {
                DocumentId = Id,
                UserId = shareUserId,
                CanEdit = canEdit,
                UpdateState = UpdateState.Created,
                CreateDate = now,
                UpdateDate = now,
                CreateUserId = _currentUser.UserId,
                UpdateUserId = _currentUser.UserId
            });
        }
        else
        {
            existing.CanEdit = canEdit;
            existing.UpdateState = UpdateState.Updated;
            existing.UpdateDate = now;
            existing.UpdateUserId = _currentUser.UserId;
        }

        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        return RedirectToPage("Edit", new { id = Id });
    }

    public async Task<IActionResult> OnPostRevokeUserShareAsync(long shareId)
    {
        var document = await LoadAccessibleAsync(HttpContext.RequestAborted);
        if (document == null)
        {
            return NotFound();
        }

        if (!DocumentAccess.IsOwnerOrAdmin(document, _currentUser.UserId, _currentUser.IsAdmin))
        {
            return Forbid();
        }

        var share = await _db.DocumentShares
            .FirstOrDefaultAsync(s => s.Id == shareId && s.DocumentId == Id, HttpContext.RequestAborted);
        if (share != null)
        {
            share.UpdateState = UpdateState.Deleted;
            share.UpdateDate = DateTime.UtcNow;
            share.UpdateUserId = _currentUser.UserId;
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
        }

        return RedirectToPage("Edit", new { id = Id });
    }

    public async Task<IActionResult> OnPostSetCommonAsync(bool isCommon)
    {
        var document = await LoadAccessibleAsync(HttpContext.RequestAborted);
        if (document == null)
        {
            return NotFound();
        }

        if (!DocumentAccess.IsOwnerOrAdmin(document, _currentUser.UserId, _currentUser.IsAdmin))
        {
            return Forbid();
        }

        document.IsCommon = isCommon;
        document.UpdateState = UpdateState.Updated;
        document.UpdateDate = DateTime.UtcNow;
        document.UpdateUserId = _currentUser.UserId;
        await _db.SaveChangesAsync(HttpContext.RequestAborted);

        return RedirectToPage("Edit", new { id = Id });
    }

    public async Task<IActionResult> OnPostConfirmReviewAsync()
    {
        var document = await LoadAccessibleAsync(HttpContext.RequestAborted);
        if (document == null)
        {
            return NotFound();
        }

        if (!await DocumentAccess.CanEditAsync(_db, document, _currentUser.UserId, _currentUser.IsAdmin, HttpContext.RequestAborted))
        {
            return Forbid();
        }

        if (document.OcrState == OcrState.Pending)
        {
            TempData["TaskMessage"] = _l["The document is still being processed — try confirming again in a moment."].Value;
            return RedirectToPage("Edit", new { id = Id, returnUrl = ReturnUrl });
        }

        var result = await _filing.FileAsync(document, null, _currentUser.UserId, HttpContext.RequestAborted);
        TempData["TaskMessage"] = result.Success ? _l["Filed and marked as reviewed."].Value : result.Error;

        return RedirectToPage("Edit", new { id = Id, returnUrl = ReturnUrl });
    }

    /// <summary>Loads the document (tracked) only if the current user may access it.</summary>
    private async Task<Document?> LoadAccessibleAsync(CancellationToken ct)
    {
        if (Id == 0)
        {
            return null;
        }

        return await _db.Documents
            .AccessibleTo(_currentUser)
            .FirstOrDefaultAsync(d => d.Id == Id && d.UpdateState != UpdateState.Deleted, ct);
    }

    /// <summary>Save metadata. Staged documents stay in the inbox; filed ones are re-filed per template.</summary>
    public Task<IActionResult> OnPostAsync() => SaveAsync(confirm: false);

    /// <summary>Save metadata, then file the document into the selected location and mark it reviewed.</summary>
    public Task<IActionResult> OnPostSaveConfirmAsync() => SaveAsync(confirm: true);

    private async Task<IActionResult> SaveAsync(bool confirm)
    {
        ViewData["Breadcrumb"] = "Documents / Edit";

        if (Id == 0)
        {
            return NotFound();
        }

        var document = await _db.Documents
            .Include(d => d.DocumentTags)
            .Include(d => d.Owner)
            .Include(d => d.StorageLocation)
            .Include(d => d.Shares).ThenInclude(s => s.User)
            .AsSplitQuery()
            .AccessibleTo(_currentUser)
            .FirstOrDefaultAsync(d => d.Id == Id);

        if (document == null || document.UpdateState == UpdateState.Deleted)
        {
            return NotFound();
        }

        if (!await DocumentAccess.CanEditAsync(_db, document, _currentUser.UserId, _currentUser.IsAdmin, HttpContext.RequestAborted))
        {
            return Forbid();
        }

        var title = Input.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            ModelState.AddModelError("Input.Title", _l["Title is required."]);
        }

        // Filed documents always need a location; staged ones only remember a target
        // (filing falls back to the default location when none is chosen).
        if (!document.IsStaged && Input.StorageLocationId is null)
        {
            ModelState.AddModelError("Input.StorageLocationId", _l["A storage location is required."]);
        }

        if (!ModelState.IsValid)
        {
            await RenderAsync(document);
            return Page();
        }

        var now = DateTime.UtcNow;

        // Resolve names used to build the human-readable file path. The posted ids come
        // from plain hidden inputs, so an unknown/deleted id is dropped instead of being
        // written through (a forged project id would even widen access via the cascade).
        string? correspondentName = null;
        if (Input.CorrespondentId is long cid)
        {
            correspondentName = await _db.Correspondents
                .Where(c => c.Id == cid && c.UpdateState != UpdateState.Deleted)
                .Select(c => c.Name)
                .FirstOrDefaultAsync();

            if (correspondentName is null)
            {
                Input.CorrespondentId = null;
            }
        }

        string? documentTypeName = null;
        if (Input.DocumentTypeId is long dtid)
        {
            documentTypeName = await _db.DocumentTypes
                .Where(t => t.Id == dtid && t.UpdateState != UpdateState.Deleted)
                .Select(t => t.Name)
                .FirstOrDefaultAsync();

            if (documentTypeName is null)
            {
                Input.DocumentTypeId = null;
            }
        }

        if (Input.ProjectId is long pid)
        {
            var projectAllowed = await _db.Projects
                .Where(p => p.UpdateState != UpdateState.Deleted)
                .AccessibleTo(_currentUser)
                .AnyAsync(p => p.Id == pid, HttpContext.RequestAborted);

            if (!projectAllowed)
            {
                Input.ProjectId = null;
            }
        }

        // The NEW location must be active; the OLD one is loaded even if it was
        // soft-deleted, because the file physically still lives under its root and
        // must be moved from there.
        StorageLocation? newLoc = null;
        if (Input.StorageLocationId is long newLocId)
        {
            newLoc = await _db.StorageLocations
                .Include(s => s.Credential)
                .FirstOrDefaultAsync(s => s.Id == newLocId && s.UpdateState != UpdateState.Deleted);
            if (newLoc == null)
            {
                ModelState.AddModelError("Input.StorageLocationId", _l["Please select a valid storage location."]);
                await RenderAsync(document);
                return Page();
            }
        }

        // Move / relocate the physical file of a FILED document to match the new
        // metadata. Staged files stay in the inbox area until confirmed. A missing or
        // locked source file must not surface as a 500 — fail the save cleanly.
        if (!document.IsStaged && newLoc != null && !string.IsNullOrEmpty(document.RelativePath))
        {
            var oldLoc = document.StorageLocationId is long oldId
                ? await _db.StorageLocations.Include(s => s.Credential).FirstOrDefaultAsync(s => s.Id == oldId)
                : null;

            if (oldLoc != null)
            {
                var effectiveDate = Input.DocumentDate ?? document.CreateDate;
                var desiredRelativePath = _storage.BuildRelativePath(
                    newLoc, title, effectiveDate, correspondentName, documentTypeName, document.OriginalFileName);

                try
                {
                    document.RelativePath = oldLoc.Id != newLoc.Id
                        ? await _storage.RelocateAsync(oldLoc, document.RelativePath, newLoc, desiredRelativePath, HttpContext.RequestAborted)
                        : await _storage.MoveAsync(oldLoc, document.RelativePath, desiredRelativePath, HttpContext.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ModelState.AddModelError(string.Empty, _l["The document file could not be moved: {0} No changes were saved.", ex.Message]);
                    await RenderAsync(document);
                    return Page();
                }
            }
        }

        // Update metadata.
        document.Title = title;
        document.DocumentDate = Input.DocumentDate.HasValue
            ? DateTime.SpecifyKind(Input.DocumentDate.Value, DateTimeKind.Utc)
            : null;
        document.DocumentTypeId = Input.DocumentTypeId;
        document.CorrespondentId = Input.CorrespondentId;
        document.ProjectId = Input.ProjectId;
        document.StorageLocationId = Input.StorageLocationId;
        document.UpdateState = UpdateState.Updated;
        document.UpdateDate = now;
        document.UpdateUserId = _currentUser.UserId;

        // Sync tags to the selected set.
        SyncTags(document, now);

        await _db.SaveChangesAsync();

        if (confirm)
        {
            // Filing moves the staged file out from under the still-running OCR worker.
            if (document.OcrState == OcrState.Pending)
            {
                TempData["TaskMessage"] = _l["The document is still being processed — try confirming again in a moment."].Value;
                return RedirectToPage("Edit", new { id = Id, returnUrl = ReturnUrl });
            }

            var result = await _filing.FileAsync(document, Input.StorageLocationId, _currentUser.UserId, HttpContext.RequestAborted);
            if (!result.Success)
            {
                TempData["TaskMessage"] = result.Error;
                return RedirectToPage("Edit", new { id = Id, returnUrl = ReturnUrl });
            }

            TempData["InboxMessage"] = _l["\"{0}\" filed.", document.Title].Value;
            return LocalRedirect(!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/Inbox");
        }

        return LocalRedirect(BackUrl);
    }

    public async Task<IActionResult> OnPostReanalyzeAsync(bool overwrite)
    {
        if (Id == 0)
        {
            return NotFound();
        }

        var document = await _db.Documents
            .Include(d => d.StorageLocation)
            .AccessibleTo(_currentUser)
            .FirstOrDefaultAsync(d => d.Id == Id && d.UpdateState != UpdateState.Deleted);
        if (document == null)
        {
            return NotFound();
        }

        if (!await DocumentAccess.CanEditAsync(_db, document, _currentUser.UserId, _currentUser.IsAdmin, HttpContext.RequestAborted))
        {
            return Forbid();
        }

        var result = await _analysis.AnalyzeAsync(
            document,
            new AnalysisOptions { Overwrite = overwrite, CreateMissingCorrespondents = true },
            _currentUser.UserId,
            HttpContext.RequestAborted);

        if (result.AnythingChanged)
        {
            document.UpdateState = UpdateState.Updated;
            document.UpdateDate = DateTime.UtcNow;
            document.UpdateUserId = _currentUser.UserId;
            await _db.SaveChangesAsync();
            TempData["TaskMessage"] = result.UsedInvoiceData
                ? _l["Re-analyzed from the structured e-invoice (XRechnung/ZUGFeRD)."].Value
                : _l["Re-analyzed from the document text."].Value;
        }
        else
        {
            TempData["TaskMessage"] = _l["Analysis found nothing to change."].Value;
        }

        return RedirectToPage("Edit", new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Id == 0)
        {
            return NotFound();
        }

        var document = await _db.Documents
            .AccessibleTo(_currentUser)
            .FirstOrDefaultAsync(d => d.Id == Id);
        if (document == null)
        {
            return NotFound();
        }

        // Deleting a document is an owner-only action.
        if (!DocumentAccess.IsOwnerOrAdmin(document, _currentUser.UserId, _currentUser.IsAdmin))
        {
            return Forbid();
        }

        // Filed documents keep their file in the storage location (soft delete); a staged
        // one never reached a location, so its inbox file is removed as well.
        if (document.IsStaged)
        {
            _storage.TryDeleteStaged(document.RelativePath);
            document.IsStaged = false;
            document.RelativePath = string.Empty;
        }

        document.UpdateState = UpdateState.Deleted;
        document.UpdateDate = DateTime.UtcNow;
        document.UpdateUserId = _currentUser.UserId;

        await _db.SaveChangesAsync();

        return LocalRedirect(BackUrl);
    }

    private void SyncTags(Document document, DateTime now)
    {
        var desired = new HashSet<long>(SelectedTagIds ?? Array.Empty<long>());
        var current = document.DocumentTags.ToList();

        foreach (var link in current.Where(dt => !desired.Contains(dt.TagId)))
        {
            document.DocumentTags.Remove(link);
            _db.DocumentTags.Remove(link);
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
                CreateUserId = _currentUser.UserId,
                UpdateUserId = _currentUser.UserId
            });
        }
    }

    private void FillPreview(Document document)
    {
        Token = document.Token;
        ThumbnailPath = document.ThumbnailPath;
        OcrState = document.OcrState;
        PageCount = document.PageCount;
        FileSize = document.FileSize;
        OriginalFileName = document.OriginalFileName;
        AddedDate = document.CreateDate;
        InvoiceNumber = document.InvoiceNumber;
        OcrText = document.OcrText;
        ContentHash = document.ContentHash;
        RelativePath = document.RelativePath;
        StorageLocationName = document.StorageLocation?.Name;
    }

    private async Task BuildOptionListsAsync(IEnumerable<long> selectedTagIds)
    {
        var selected = new HashSet<long>(selectedTagIds);

        var documentTypes = await _db.DocumentTypes
            .AsNoTracking()
            .Where(t => t.UpdateState != UpdateState.Deleted)
            .OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync();

        var correspondents = await _db.Correspondents
            .AsNoTracking()
            .Where(c => c.UpdateState != UpdateState.Deleted)
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync();

        var projects = await _db.Projects
            .AsNoTracking()
            .Where(p => p.UpdateState != UpdateState.Deleted)
            .AccessibleTo(_currentUser)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync();

        var storageLocations = await _db.StorageLocations
            .AsNoTracking()
            .Where(s => s.UpdateState != UpdateState.Deleted)
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name })
            .ToListAsync();

        var tags = await _db.Tags
            .AsNoTracking()
            .Where(t => t.UpdateState != UpdateState.Deleted)
            .OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name, t.Color })
            .ToListAsync();

        DocumentTypeOptions = BuildOptions(
            documentTypes.Select(t => (t.Id, t.Name)), Input.DocumentTypeId, "— None —");
        CorrespondentOptions = BuildOptions(
            correspondents.Select(c => (c.Id, c.Name)), Input.CorrespondentId, "— None —");
        ProjectOptions = BuildOptions(
            projects.Select(p => (p.Id, p.Name)), Input.ProjectId, "— None —");
        StorageLocationOptions = BuildOptions(
            storageLocations.Select(s => (s.Id, s.Name)), Input.StorageLocationId, "— Select —");

        TagOptions = tags
            .Select(t => new TagOption(t.Id, t.Name, t.Color, selected.Contains(t.Id)))
            .ToList();

        TagItems = tags
            .Select(t => new SelectListItem(t.Name, t.Id.ToString()))
            .ToList();
    }

    private static List<SelectListItem> BuildOptions(
        IEnumerable<(long Id, string Name)> source, long? selectedId, string emptyLabel)
    {
        var items = new List<SelectListItem>
        {
            new() { Value = string.Empty, Text = emptyLabel, Selected = selectedId is null }
        };

        items.AddRange(source.Select(s => new SelectListItem
        {
            Value = s.Id.ToString(),
            Text = s.Name,
            Selected = selectedId == s.Id
        }));

        return items;
    }
}
