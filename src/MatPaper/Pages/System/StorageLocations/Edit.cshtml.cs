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
    private readonly ILogger<EditModel> _logger;
    private readonly IStringLocalizer<SharedResource> _l;

    public EditModel(AppDbContext db, CurrentUser currentUser, DocumentStorageService storage, ILogger<EditModel> logger, IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _currentUser = currentUser;
        _storage = storage;
        _logger = logger;
        _l = l;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;

    public List<SelectListItem> CredentialOptions { get; private set; } = new();

    public List<SelectListItem> OwnerOptions { get; private set; } = new();

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>0 = local folder, 1 = SMB share (int so the select binds/selects plainly).</summary>
        public int Kind { get; set; }

        public string? RootPath { get; set; }
        public string? SmbHost { get; set; }
        public string? SmbShare { get; set; }
        public string? SmbPath { get; set; }
        public long? CredentialId { get; set; }
        public string PathTemplate { get; set; } = string.Empty;
        public bool IsDefault { get; set; }

        /// <summary>Owner for documents a storage search finds here; null = whoever starts it.</summary>
        public long? DefaultOwnerId { get; set; }

        public bool DefaultIsCommon { get; set; }

        /// <summary>Extensions a storage search picks up. Empty = every file.</summary>
        public string? ScanExtensions { get; set; }
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
                SmbHost = entity.SmbHost,
                SmbShare = entity.SmbShare,
                SmbPath = entity.SmbPath,
                CredentialId = entity.CredentialId,
                PathTemplate = entity.PathTemplate,
                IsDefault = entity.IsDefault,
                DefaultOwnerId = entity.DefaultOwnerId,
                DefaultIsCommon = entity.DefaultIsCommon,
                ScanExtensions = entity.ScanExtensions
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
            existing.SmbHost = draft.SmbHost;
            existing.SmbShare = draft.SmbShare;
            existing.SmbPath = draft.SmbPath;
            existing.CredentialId = draft.CredentialId;
            existing.PathTemplate = draft.PathTemplate;
            existing.IsDefault = draft.IsDefault;
            existing.DefaultOwnerId = draft.DefaultOwnerId;
            existing.DefaultIsCommon = draft.DefaultIsCommon;
            existing.ScanExtensions = draft.ScanExtensions;
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

        if (draft.Kind == StorageKind.Smb && draft.CredentialId is long credentialId)
        {
            draft.Credential = await _db.Credentials
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == credentialId && c.UpdateState != UpdateState.Deleted);

            // Testing signs in to whatever host was typed into the form, so leave a trail
            // of which credential was offered to which server.
            _logger.LogInformation(
                "User {UserId} tested credential '{Credential}' against SMB host {Host}, share {Share}.",
                _currentUser.UserId, draft.Credential?.Name ?? $"#{credentialId}", draft.SmbHost, draft.SmbShare);
        }

        try
        {
            await _storage.TestAsync(draft, HttpContext.RequestAborted);
            this.NotifyNow(draft.Kind == StorageKind.Smb
                ? _l["Connected to {0}.", draft.DisplayRoot].Value
                : _l["Folder {0} is available.", draft.RootPath].Value);
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
        var kind = Input.Kind == (int)StorageKind.Smb ? StorageKind.Smb : StorageKind.Local;

        return new StorageLocation
        {
            Id = Id,
            Name = (Input.Name ?? string.Empty).Trim(),
            Kind = kind,
            RootPath = kind == StorageKind.Local ? (Input.RootPath ?? string.Empty).Trim() : string.Empty,
            SmbHost = kind == StorageKind.Smb ? NullIfEmpty(Input.SmbHost) : null,
            SmbShare = kind == StorageKind.Smb ? NullIfEmpty(Input.SmbShare) : null,
            SmbPath = kind == StorageKind.Smb ? NullIfEmpty(Input.SmbPath) : null,
            CredentialId = kind == StorageKind.Smb ? Input.CredentialId : null,
            PathTemplate = (Input.PathTemplate ?? string.Empty).Trim(),
            IsDefault = Input.IsDefault,
            DefaultOwnerId = Input.DefaultOwnerId,
            DefaultIsCommon = Input.DefaultIsCommon,
            ScanExtensions = (Input.ScanExtensions ?? string.Empty).Trim()
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
            if (string.IsNullOrWhiteSpace(draft.SmbHost))
            {
                ModelState.AddModelError("Input.SmbHost", _l["Host is required."]);
            }
            if (string.IsNullOrWhiteSpace(draft.SmbShare))
            {
                ModelState.AddModelError("Input.SmbShare", _l["Share is required."]);
            }
            if (draft.CredentialId is null)
            {
                ModelState.AddModelError("Input.CredentialId", _l["Choose a saved credential."]);
            }
        }

        if (string.IsNullOrWhiteSpace(draft.PathTemplate))
        {
            ModelState.AddModelError("Input.PathTemplate", _l["Path template is required."]);
        }

        return ModelState.IsValid;
    }

    private async Task BuildOptionListsAsync()
    {
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

        var credentials = await _db.Credentials
            .AsNoTracking()
            .Where(c => c.UpdateState != UpdateState.Deleted)
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync();

        CredentialOptions = new List<SelectListItem>
        {
            new() { Value = string.Empty, Text = "— Select —", Selected = Input.CredentialId is null }
        };
        CredentialOptions.AddRange(credentials.Select(c => new SelectListItem
        {
            Value = c.Id.ToString(),
            Text = c.Name,
            Selected = Input.CredentialId == c.Id
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
