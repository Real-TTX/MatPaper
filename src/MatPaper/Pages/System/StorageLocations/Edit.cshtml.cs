using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.System.StorageLocations;

public class EditModel : PageModel
{
    private const string DefaultPathTemplate = "{Correspondent}/{Year}/{DocumentType}/{Date} {Title}{Ext}";

    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;
    private readonly DocumentStorageService _storage;
    private readonly ImportBrowser _browser;
    private readonly ILogger<EditModel> _logger;
    private readonly IStringLocalizer<SharedResource> _l;

    public EditModel(AppDbContext db, CurrentUser currentUser, DocumentStorageService storage, ImportBrowser browser, ILogger<EditModel> logger, IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _currentUser = currentUser;
        _storage = storage;
        _browser = browser;
        _logger = logger;
        _l = l;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;

    /// <summary>The "Runs" block for this location's storage searches. Null for a new location.</summary>
    public TaskRunsPanel? Runs { get; private set; }

    private async Task LoadRunsAsync()
    {
        if (IsEdit)
        {
            Runs = new TaskRunsPanel(
                TaskRunKind.Scan, Id, $"/System/StorageLocations?handler=Scan&id={Id}", _l["Search now"].Value,
                null, null, await TaskRunsPanel.LoadRunsAsync(_db, TaskRunKind.Scan, Id));
        }
    }

    public List<SelectListItem> SmbConnectionOptions { get; private set; } = new();

    public List<SelectListItem> CloudConnectionOptions { get; private set; } = new();

    public List<SelectListItem> OwnerOptions { get; private set; } = new();

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>0 = local folder, 1 = SMB share, 2 = cloud drive (int so the select binds/selects plainly).</summary>
        public int Kind { get; set; }

        public string? RootPath { get; set; }

        /// <summary>The SMB connection that provides this location's endpoint and sign-in.</summary>
        public long? ConnectionId { get; set; }

        /// <summary>Sub-folder within the SMB share that acts as this location's root.</summary>
        public string? BasePath { get; set; }

        /// <summary>The cloud connection (Google Drive, OneDrive) backing this location.</summary>
        /// <remarks>Separate from <see cref="ConnectionId"/> so the SMB and cloud dropdowns never
        /// collide on one posted field name.</remarks>
        public long? CloudConnectionId { get; set; }

        /// <summary>Folder within the cloud drive that acts as this location's root.</summary>
        public string? CloudBasePath { get; set; }

        public string PathTemplate { get; set; } = string.Empty;
        public bool IsDefault { get; set; }

        /// <summary>Owner for documents a storage search finds here; null = whoever starts it.</summary>
        public long? DefaultOwnerId { get; set; }

        public bool DefaultIsCommon { get; set; }

        /// <summary>Extensions a storage search picks up. Empty = every file.</summary>
        public string? ScanExtensions { get; set; }

        /// <summary>Cron expression for an automatic search; empty = only on demand.</summary>
        public string? ScanCron { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        SetBreadcrumb();

        if (IsEdit)
        {
            var entity = await _db.StorageLocations
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == Id && s.UpdateState != UpdateState.Deleted);
            if (entity == null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Name = entity.Name,
                Kind = (int)entity.Kind,
                RootPath = entity.RootPath,
                ConnectionId = entity.Kind == StorageKind.Smb ? entity.ConnectionId : null,
                BasePath = entity.Kind == StorageKind.Smb ? entity.BasePath : null,
                CloudConnectionId = entity.Kind == StorageKind.Cloud ? entity.ConnectionId : null,
                CloudBasePath = entity.Kind == StorageKind.Cloud ? entity.BasePath : null,
                PathTemplate = entity.PathTemplate,
                IsDefault = entity.IsDefault,
                DefaultOwnerId = entity.DefaultOwnerId,
                DefaultIsCommon = entity.DefaultIsCommon,
                ScanExtensions = entity.ScanExtensions,
                ScanCron = entity.ScanCron
            };
        }
        else
        {
            Input.PathTemplate = DefaultPathTemplate;
            Input.RootPath = "/storage";
            Input.ScanExtensions = new StorageLocation().ScanExtensions;
        }

        await BuildOptionListsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetBreadcrumb();
        await BuildOptionListsAsync();

        var draft = BuildDraft();
        if (!Validate(draft))
        {
            return Page();
        }

        var nameTaken = await _db.StorageLocations
            .AnyAsync(s => s.Id != Id
                && s.UpdateState != UpdateState.Deleted
                && s.Name.ToLower() == draft.Name.ToLower());
        if (nameTaken)
        {
            ModelState.AddModelError("Input.Name", _l["A storage location with that name already exists."]);
            return Page();
        }

        var now = DateTime.UtcNow;

        StorageLocation entity;
        if (IsEdit)
        {
            var existing = await _db.StorageLocations
                .FirstOrDefaultAsync(s => s.Id == Id && s.UpdateState != UpdateState.Deleted);
            if (existing == null)
            {
                return NotFound();
            }

            existing.Name = draft.Name;
            existing.Kind = draft.Kind;
            existing.RootPath = draft.RootPath;
            existing.ConnectionId = draft.ConnectionId;
            existing.BasePath = draft.BasePath;
            existing.PathTemplate = draft.PathTemplate;
            existing.IsDefault = draft.IsDefault;
            existing.DefaultOwnerId = draft.DefaultOwnerId;
            existing.DefaultIsCommon = draft.DefaultIsCommon;
            existing.ScanExtensions = draft.ScanExtensions;
            existing.ScanCron = draft.ScanCron;
            existing.UpdateState = UpdateState.Updated;
            existing.UpdateDate = now;
            existing.UpdateUserId = _currentUser.UserId;
            entity = existing;
        }
        else
        {
            draft.UpdateState = UpdateState.Created;
            draft.CreateDate = now;
            draft.CreateUserId = _currentUser.UserId;
            draft.UpdateDate = now;
            draft.UpdateUserId = _currentUser.UserId;
            _db.StorageLocations.Add(draft);
            entity = draft;
        }

        // Guard: at most one default among non-deleted locations.
        if (entity.IsDefault)
        {
            var others = await _db.StorageLocations
                .Where(s => s.Id != Id
                    && s.UpdateState != UpdateState.Deleted
                    && s.IsDefault)
                .ToListAsync();
            foreach (var other in others)
            {
                other.IsDefault = false;
                other.UpdateState = UpdateState.Updated;
                other.UpdateDate = now;
                other.UpdateUserId = _currentUser.UserId;
            }
        }

        await _db.SaveChangesAsync();

        return RedirectToPage("Index");
    }

    /// <summary>Checks that the folder exists (local) or the share/folder is reachable (SMB) without saving.</summary>
    public async Task<IActionResult> OnPostTestAsync()
    {
        SetBreadcrumb();
        await BuildOptionListsAsync();

        var draft = BuildDraft();
        if (!Validate(draft))
        {
            return Page();
        }

        if (draft.Kind is StorageKind.Smb or StorageKind.Cloud && draft.ConnectionId is long connectionId)
        {
            draft.Connection = await _db.Connections
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == connectionId && c.UpdateState != UpdateState.Deleted);
            _logger.LogInformation(
                "User {UserId} tested storage connection {ConnectionId}.", _currentUser.UserId, connectionId);
        }

        try
        {
            await _storage.TestAsync(draft, HttpContext.RequestAborted);
            this.NotifyNow(draft.Kind == StorageKind.Local
                ? _l["Folder {0} is available.", draft.RootPath].Value
                : _l["Connected to {0}.", draft.DisplayRoot].Value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.NotifyNow(ex.Message, NoticeKind.Danger);
        }

        return Page();
    }

    /// <summary>
    /// Lists subfolders for the folder pickers (JSON, read-only): the container's folders for a
    /// local location; otherwise the folders of the chosen SMB/cloud connection, browsed from the
    /// connection's root because the base path is exactly what is being picked.
    /// </summary>
    public async Task<IActionResult> OnPostBrowseAsync(string scope, string? path, CancellationToken ct)
    {
        try
        {
            if (scope == "local-folders")
            {
                var local = await _browser.ListLocalFoldersAsync(path, pattern: null, ct);
                return new JsonResult(new
                {
                    ok = true,
                    entries = local.Select(e => new { path = e.Path, name = e.Name, count = e.ItemCount, hasChildren = e.HasChildren })
                });
            }

            var draft = BuildDraft();
            if (draft.Kind == StorageKind.Local || draft.ConnectionId is not long connectionId)
            {
                return new JsonResult(new { ok = false, error = _l["Choose a connection."].Value });
            }

            draft.Connection = await _db.Connections
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == connectionId && c.UpdateState != UpdateState.Deleted, ct);
            if (draft.Connection is null)
            {
                return new JsonResult(new { ok = false, error = _l["Choose a connection."].Value });
            }
            draft.BasePath = null;

            var dir = (path ?? string.Empty).Replace('\\', '/').Trim('/');
            var names = await _storage.ListFoldersAsync(draft, dir, ct);
            return new JsonResult(new
            {
                ok = true,
                // Whether a folder has children would cost one more call per entry, so the
                // picker offers "open" everywhere and shows "nothing found" when it is empty.
                entries = names.Select(n => new { path = dir.Length == 0 ? n : dir + "/" + n, name = n, count = (int?)null, hasChildren = true })
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message });
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!IsEdit)
        {
            return RedirectToPage("Index");
        }

        var entity = await _db.StorageLocations
            .FirstOrDefaultAsync(s => s.Id == Id && s.UpdateState != UpdateState.Deleted);
        if (entity == null)
        {
            return NotFound();
        }

        entity.UpdateState = UpdateState.Deleted;
        entity.UpdateDate = DateTime.UtcNow;
        entity.UpdateUserId = _currentUser.UserId;

        await _db.SaveChangesAsync();

        return RedirectToPage("Index");
    }

    /// <summary>Trims the input into an unsaved entity; kind-specific fields of the other kind are cleared.</summary>
    private StorageLocation BuildDraft()
    {
        var kind = Input.Kind switch
        {
            (int)StorageKind.Smb => StorageKind.Smb,
            (int)StorageKind.Cloud => StorageKind.Cloud,
            _ => StorageKind.Local
        };

        return new StorageLocation
        {
            Id = Id,
            Name = (Input.Name ?? string.Empty).Trim(),
            Kind = kind,
            RootPath = kind == StorageKind.Local ? (Input.RootPath ?? string.Empty).Trim() : string.Empty,
            ConnectionId = kind switch
            {
                StorageKind.Smb => Input.ConnectionId,
                StorageKind.Cloud => Input.CloudConnectionId,
                _ => null
            },
            BasePath = kind switch
            {
                StorageKind.Smb => NullIfEmpty(Input.BasePath),
                StorageKind.Cloud => NullIfEmpty(Input.CloudBasePath),
                _ => null
            },
            PathTemplate = (Input.PathTemplate ?? string.Empty).Trim(),
            IsDefault = Input.IsDefault,
            DefaultOwnerId = Input.DefaultOwnerId,
            DefaultIsCommon = Input.DefaultIsCommon,
            ScanExtensions = (Input.ScanExtensions ?? string.Empty).Trim(),
            ScanCron = NullIfEmpty(Input.ScanCron)
        };
    }

    private bool Validate(StorageLocation draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            ModelState.AddModelError("Input.Name", _l["Name is required."]);
        }

        if (draft.Kind == StorageKind.Local)
        {
            if (string.IsNullOrWhiteSpace(draft.RootPath))
            {
                ModelState.AddModelError("Input.RootPath", _l["Root path is required."]);
            }
            else if (_storage.OverlapsInternalData(draft, out var conflict))
            {
                ModelState.AddModelError("Input.RootPath",
                    _l["This root overlaps MatPaper's own data folder ({0}).", conflict]);
            }
        }
        else
        {
            if (draft.ConnectionId is null)
            {
                ModelState.AddModelError("Input.ConnectionId", _l["Choose a connection."]);
            }
        }

        if (string.IsNullOrWhiteSpace(draft.PathTemplate))
        {
            ModelState.AddModelError("Input.PathTemplate", _l["Path template is required."]);
        }

        if (draft.ScanCron is not null)
        {
            try
            {
                Cronos.CronExpression.Parse(draft.ScanCron);
            }
            catch (Exception)
            {
                ModelState.AddModelError("Input.ScanCron", _l["The schedule is not a valid cron expression."]);
            }
        }

        return ModelState.IsValid;
    }

    private async Task BuildOptionListsAsync()
    {
        await LoadRunsAsync();

        var users = await _db.Users
            .AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.DisplayName)
            .Select(u => new { u.Id, u.DisplayName, u.Username })
            .ToListAsync();

        OwnerOptions = new List<SelectListItem>
        {
            new() { Value = string.Empty, Text = string.Empty, Selected = Input.DefaultOwnerId is null }
        };
        OwnerOptions.AddRange(users.Select(u => new SelectListItem
        {
            Value = u.Id.ToString(),
            Text = string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName,
            Selected = Input.DefaultOwnerId == u.Id
        }));

        var connections = await _db.Connections
            .AsNoTracking()
            .Where(c => c.UpdateState != UpdateState.Deleted
                && (c.Kind == ConnectionKind.Smb || c.Kind == ConnectionKind.GoogleDrive || c.Kind == ConnectionKind.OneDrive))
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.Kind })
            .ToListAsync();

        SmbConnectionOptions = new List<SelectListItem>
        {
            new() { Value = string.Empty, Text = _l["— Select —"].Value, Selected = Input.ConnectionId is null }
        };
        SmbConnectionOptions.AddRange(connections
            .Where(c => c.Kind == ConnectionKind.Smb)
            .Select(c => new SelectListItem
            {
                Value = c.Id.ToString(),
                Text = c.Name,
                Selected = Input.ConnectionId == c.Id
            }));

        CloudConnectionOptions = new List<SelectListItem>
        {
            new() { Value = string.Empty, Text = _l["— Select —"].Value, Selected = Input.CloudConnectionId is null }
        };
        CloudConnectionOptions.AddRange(connections
            .Where(c => c.Kind == ConnectionKind.GoogleDrive || c.Kind == ConnectionKind.OneDrive)
            .Select(c => new SelectListItem
            {
                Value = c.Id.ToString(),
                Text = c.Kind == ConnectionKind.GoogleDrive ? $"{c.Name} (Google Drive)" : $"{c.Name} (OneDrive)",
                Selected = Input.CloudConnectionId == c.Id
            }));
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void SetBreadcrumb()
    {
        ViewData["Breadcrumb"] = IsEdit
            ? "System / Storage locations / Edit"
            : "System / Storage locations / New";
    }
}
