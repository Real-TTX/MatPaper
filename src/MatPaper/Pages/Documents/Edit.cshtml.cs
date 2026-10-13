using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;
using MatPaper.Services.Barcodes;
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
    private readonly DocumentProcessingQueue _queue;
    private readonly ArchiveNumberService _numbers;
    private readonly MatPaper.Configuration.AppConfig _config;
    private readonly IStringLocalizer<SharedResource> _l;

    public EditModel(
        AppDbContext db,
        DocumentStorageService storage,
        CurrentUser currentUser,
        ShareLinkService shareLinks,
        DocumentAnalysisService analysis,
        DocumentFilingService filing,
        DocumentProcessingQueue queue,
        ArchiveNumberService numbers,
        MatPaper.Configuration.AppConfig config,
        IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _storage = storage;
        _currentUser = currentUser;
        _shareLinks = shareLinks;
        _analysis = analysis;
        _filing = filing;
        _queue = queue;
        _numbers = numbers;
        _config = config;
        _l = l;
    }

    /// <summary>Where "Back" and post-save redirects go (local URLs only); e.g. /Inbox.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string BackUrl => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/Documents";

    /// <summary>True while the file still sits in the inbox staging area (not filed yet).</summary>
    public bool IsStaged { get; private set; }

    public DocumentOrigin Origin { get; private set; }

    public List<ShareLink> ShareLinks { get; private set; } = new();
    public string ShareBaseUrl { get; private set; } = string.Empty;
    public string? InvoiceNumber { get; private set; }
    public Document? EInvoice { get; private set; }

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

    /// <summary>The colour of every tag and every project by id: the picker shows it as a dot before the name.</summary>
    public Dictionary<string, string?> TagColours => TagOptions.ToDictionary(t => t.Id.ToString(), t => t.Color);
    public Dictionary<string, string?> ProjectColours { get; private set; } = new();

    /// <summary>The archive number feature is switched on (System, Archive numbers): only then the page has anything of it.</summary>
    public bool AsnEnabled => _config.ArchiveNumbersEnabled;

    /// <summary>The number the "Next free number" button puts into the field (only for those who may edit).</summary>
    public long NextFreeNumber { get; private set; }

    /// <summary>The number as it is written on a label (ASN 00017), or null when the document has none.</summary>
    public string? ArchiveNumberText { get; private set; }

    /// <summary>A copy with the number printed on it can be made: the document is a PDF.</summary>
    public bool CanStamp { get; private set; }

    /// <summary>A label can be looked for on the document: it is a PDF or an image.</summary>
    public bool CanReadNumber { get; private set; }

    public class InputModel
    {
        public string Title { get; set; } = string.Empty;
        public DateTime? DocumentDate { get; set; }

        /// <summary>The number of the paper original (the sticker on it); empty = none.</summary>
        public long? ArchiveSerialNumber { get; set; }
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
            ArchiveSerialNumber = document.ArchiveSerialNumber,
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
        Origin = document.Origin;
        FillPreview(document);
        await BuildOptionListsAsync(SelectedTagIds);
        await BuildAccessViewAsync(document, HttpContext.RequestAborted);
        ShareLinks = await _shareLinks.ListForDocumentAsync(Id, HttpContext.RequestAborted);
        ShareBaseUrl = $"{Request.Scheme}://{Request.Host}";

        if (AsnEnabled)
        {
            ArchiveNumberText = document.ArchiveSerialNumber is long number ? _config.ResolveBarcodeOptions().AsnReadable(number) : null;
            CanStamp = string.Equals(Path.GetExtension(document.OriginalFileName), ".pdf", StringComparison.OrdinalIgnoreCase);
            CanReadNumber = BarcodeReader.Supports(Path.GetExtension(document.OriginalFileName));
            if (CanEdit)
            {
                NextFreeNumber = await _numbers.NextFreeAsync(HttpContext.RequestAborted);
            }
        }
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
            this.Notify(_l["The document is still being processed — try confirming again in a moment."].Value, NoticeKind.Warn);
            return RedirectToPage("Edit", new { id = Id, returnUrl = ReturnUrl });
        }

        var mode = document.IsStaged ? FilingMode.FromStaging : FilingMode.KeepInPlace;
        var result = await _filing.FileAsync(document, null, mode, _currentUser.UserId, HttpContext.RequestAborted);
        this.Notify(result.Success, result.Success ? _l["Filed and marked as reviewed."].Value : result.Error);

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

        // The archive number is the number of one document: a positive number nobody else has. (Switched off, the
        // page has no such field and the number the document has stays as it is.)
        var archiveNumber = AsnEnabled ? Input.ArchiveSerialNumber : document.ArchiveSerialNumber;
        if (AsnEnabled && Input.ArchiveSerialNumber is long wantedNumber && wantedNumber != document.ArchiveSerialNumber)
        {
            await ValidateArchiveNumberAsync(wantedNumber, document.Id);
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
                .Include(s => s.Connection)
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
        StorageLocation? movedFromLoc = null;
        string? movedFromPath = null;
        if (!document.IsStaged && newLoc != null && !string.IsNullOrEmpty(document.RelativePath))
        {
            var oldLoc = document.StorageLocationId is long oldId
                ? await _db.StorageLocations.Include(s => s.Connection).FirstOrDefaultAsync(s => s.Id == oldId)
                : null;

            // A file MatPaper found where it lay keeps its path: only an explicit change of
            // storage location moves it, and even then its folders are left alone.
            var keepFolders = document.Origin == DocumentOrigin.StorageScan;
            var locationChanged = oldLoc != null && oldLoc.Id != newLoc.Id;

            if (oldLoc != null && (locationChanged || !keepFolders))
            {
                var effectiveDate = Input.DocumentDate ?? document.FileModifiedUtc ?? document.CreateDate;
                var desiredRelativePath = _storage.BuildRelativePath(
                    newLoc, title, effectiveDate, correspondentName, documentTypeName, document.OriginalFileName, document.Token, archiveNumber);

                try
                {
                    var pathBefore = document.RelativePath;
                    document.RelativePath = locationChanged
                        ? await _storage.RelocateAsync(oldLoc, document.RelativePath, newLoc, desiredRelativePath, HttpContext.RequestAborted, cleanupEmptyDirectories: !keepFolders)
                        : await _storage.MoveAsync(oldLoc, document.RelativePath, desiredRelativePath, HttpContext.RequestAborted, cleanupEmptyDirectories: !keepFolders);
                    if (document.RelativePath != pathBefore)
                    {
                        movedFromLoc = oldLoc;
                        movedFromPath = pathBefore;
                    }
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
        document.ArchiveSerialNumber = archiveNumber;
        document.UpdateState = UpdateState.Updated;
        document.UpdateDate = now;
        document.UpdateUserId = _currentUser.UserId;

        // Sync tags to the selected set.
        SyncTags(document, now);

        // The file already moved when the save fails: put it back so file and database keep agreeing.
        async Task PutFileBackAsync()
        {
            if (movedFromLoc != null && movedFromPath != null && newLoc != null)
            {
                try
                {
                    document.RelativePath = await _storage.RelocateAsync(newLoc, document.RelativePath, movedFromLoc, movedFromPath, CancellationToken.None, cleanupEmptyDirectories: false);
                }
                catch
                {
                    // nothing more to do here; the original error is what gets reported
                }
            }
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (DocumentIngestService.IsNumberTaken(ex))
        {
            // somebody took the number between the check above and the save
            await PutFileBackAsync();
            ModelState.AddModelError("Input.ArchiveSerialNumber", _l["This archive number is used by another document."]);
            await RenderAsync(document);
            return Page();
        }
        catch
        {
            await PutFileBackAsync();
            throw;
        }

        await _filing.WriteMetadataFileAsync(document, HttpContext.RequestAborted);

        if (confirm)
        {
            // Filing moves the staged file out from under the still-running OCR worker.
            if (document.OcrState == OcrState.Pending)
            {
                this.Notify(_l["The document is still being processed — try confirming again in a moment."].Value, NoticeKind.Warn);
                return RedirectToPage("Edit", new { id = Id, returnUrl = ReturnUrl });
            }

            // The physical move, if any, already happened above.
            var filingMode = document.IsStaged ? FilingMode.FromStaging : FilingMode.KeepInPlace;
            var result = await _filing.FileAsync(document, Input.StorageLocationId, filingMode, _currentUser.UserId, HttpContext.RequestAborted);
            if (!result.Success)
            {
                this.Notify(result.Error, NoticeKind.Danger);
                return RedirectToPage("Edit", new { id = Id, returnUrl = ReturnUrl });
            }

            this.Notify(_l["\"{0}\" filed.", document.Title].Value);
            return LocalRedirect(!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/Inbox");
        }

        return LocalRedirect(BackUrl);
    }

    /// <summary>
    /// Looks for a label on the first sheet of the document and puts its number into the field - as a suggestion: the
    /// page is shown again with the values as they were typed, nothing is saved.
    /// </summary>
    public async Task<IActionResult> OnPostReadNumberAsync()
    {
        ViewData["Breadcrumb"] = "Documents / Edit";
        if (Id == 0 || !AsnEnabled)
        {
            return NotFound();
        }

        var document = await _db.Documents
            .Include(d => d.DocumentTags)
            .Include(d => d.Owner)
            .Include(d => d.StorageLocation).ThenInclude(s => s!.Connection)
            .Include(d => d.Shares).ThenInclude(s => s.User)
            .AsSplitQuery()
            .AccessibleTo(_currentUser)
            .FirstOrDefaultAsync(d => d.Id == Id && d.UpdateState != UpdateState.Deleted);
        if (document is null)
        {
            return NotFound();
        }

        if (!await DocumentAccess.CanEditAsync(_db, document, _currentUser.UserId, _currentUser.IsAdmin, HttpContext.RequestAborted))
        {
            return Forbid();
        }

        var extension = Path.GetExtension(document.OriginalFileName);
        if (!BarcodeReader.Supports(extension))
        {
            this.NotifyNow(_l["Only a PDF or an image can be searched for a label."].Value, NoticeKind.Warn);
            await RenderAsync(document);
            return Page();
        }

        byte[] bytes;
        try
        {
            await using var stream = await _storage.OpenDocumentReadAsync(document, HttpContext.RequestAborted);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, HttpContext.RequestAborted);
            bytes = copy.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.NotifyNow(_l["The document file could not be read."].Value, NoticeKind.Danger);
            await RenderAsync(document);
            return Page();
        }

        // only the label matters here: no need to look for separator sheets
        var options = _config.ResolveBarcodeOptions() with { Enabled = true, SplitAtSeparators = false };
        var scan = await Task.Run(() => BarcodeReader.Scan(bytes, extension, options, HttpContext.RequestAborted), HttpContext.RequestAborted);
        var found = scan?.AsnOn(new[] { 1 });

        // the field shows what was found, not what was in it
        ModelState.Remove("Input.ArchiveSerialNumber");
        if (found is long number)
        {
            Input.ArchiveSerialNumber = number;
            this.NotifyNow(_l["Found {0} on the first page. Save to take it over.", options.AsnReadable(number)].Value, NoticeKind.Info);
            if (number != document.ArchiveSerialNumber)
            {
                await ValidateArchiveNumberAsync(number, document.Id);
            }
        }
        else
        {
            this.NotifyNow(_l["No archive number found on the first page."].Value, NoticeKind.Warn);
        }

        await RenderAsync(document);
        return Page();
    }

    /// <summary>
    /// Checks a number typed into the field: a positive number that no other document has. When another one has it,
    /// the message names it if the user may see it.
    /// </summary>
    private async Task ValidateArchiveNumberAsync(long number, long documentId)
    {
        const string field = "Input.ArchiveSerialNumber";
        if (number < 1 || number > 999_999_999_999)
        {
            ModelState.AddModelError(field, _l["Enter a whole number greater than zero."]);
            return;
        }

        var holder = await _db.Documents.AsNoTracking()
            .Where(d => d.ArchiveSerialNumber == number && d.UpdateState != UpdateState.Deleted && d.Id != documentId)
            .Select(d => new { d.Id, d.Title })
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        if (holder is null)
        {
            return;
        }

        var visible = await _db.Documents.AsNoTracking().AccessibleTo(_currentUser).AnyAsync(d => d.Id == holder.Id, HttpContext.RequestAborted);
        ModelState.AddModelError(field, visible
            ? _l["This archive number is used by \"{0}\".", holder.Title]
            : _l["This archive number is used by another document."]);
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
            await _filing.RefileAndSaveAsync(document, HttpContext.RequestAborted);
            this.Notify(result.UsedInvoiceData
                ? _l["Re-analyzed from the structured e-invoice (XRechnung/ZUGFeRD)."].Value
                : _l["Re-analyzed from the document text."].Value);
        }
        else
        {
            this.Notify(_l["Analysis found nothing to change."].Value, NoticeKind.Info);
        }

        return RedirectToPage("Edit", new { id = Id });
    }

    /// <summary>Starts text recognition for a found document that was never analysed.</summary>
    public async Task<IActionResult> OnPostAnalyzeAsync()
    {
        var document = await LoadEditableAsync();
        if (document is null)
        {
            return NotFound();
        }

        if (document.OcrState != OcrState.Deferred)
        {
            return RedirectToPage("Edit", new { id = Id, returnUrl = ReturnUrl });
        }

        document.OcrState = OcrState.Pending;
        document.UpdateDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _queue.Enqueue(document.Id);

        this.Notify(_l["Text recognition started for {0} document(s).", 1].Value);
        return RedirectToPage("Edit", new { id = Id, returnUrl = ReturnUrl });
    }

    /// <summary>Puts an ignored document back into the inbox.</summary>
    public async Task<IActionResult> OnPostRestoreAsync()
    {
        var document = await LoadEditableAsync();
        if (document is null)
        {
            return NotFound();
        }

        if (document.ReviewState != ReviewState.Ignored)
        {
            return RedirectToPage("Edit", new { id = Id, returnUrl = ReturnUrl });
        }

        document.ReviewState = ReviewState.Pending;
        document.UpdateState = UpdateState.Updated;
        document.UpdateDate = DateTime.UtcNow;
        document.UpdateUserId = _currentUser.UserId;
        await _db.SaveChangesAsync();

        return RedirectToPage("Edit", new { id = Id, returnUrl = ReturnUrl });
    }

    /// <summary>The tracked document, but only when the current user may change it.</summary>
    private async Task<Document?> LoadEditableAsync()
    {
        if (Id == 0)
        {
            return null;
        }

        var document = await _db.Documents
            .AccessibleTo(_currentUser)
            .FirstOrDefaultAsync(d => d.Id == Id && d.UpdateState != UpdateState.Deleted);

        if (document is null)
        {
            return null;
        }

        return await DocumentAccess.CanEditAsync(_db, document, _currentUser.UserId, _currentUser.IsAdmin, HttpContext.RequestAborted)
            ? document
            : null;
    }

    public async Task<IActionResult> OnPostBlockAsync()
    {
        var document = await _db.Documents.AccessibleTo(_currentUser).FirstOrDefaultAsync(d => d.Id == Id);
        if (document is not null && DocumentAccess.IsOwnerOrAdmin(document, _currentUser.UserId, _currentUser.IsAdmin))
        {
            await BlocklistService.BlockAsync(_db, document, _currentUser.UserId);
            this.Notify(_l["Deleted and blocked: it is skipped when it turns up again (see the blocklist)."].Value);
        }

        return await OnPostDeleteAsync();
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
        EInvoice = document.IsEInvoice ? document : null;
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
            .Select(p => new { p.Id, p.Name, p.Color })
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
        ProjectColours = projects.ToDictionary(p => p.Id.ToString(), p => (string?)p.Color);
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
