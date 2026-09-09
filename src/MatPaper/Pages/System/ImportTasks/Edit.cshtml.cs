using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.System.ImportTasks;

public class EditModel : PageModel
{
    private const string DefaultAttachmentExtensions = ".pdf,.png,.jpg,.jpeg,.tif,.tiff";

    private readonly AppDbContext _db;
    private readonly SecretProtector _secrets;
    private readonly TaskTriggerQueue _queue;
    private readonly ImportRunner _runner;
    private readonly CurrentUser _currentUser;

    public EditModel(
        AppDbContext db,
        SecretProtector secrets,
        TaskTriggerQueue queue,
        ImportRunner runner,
        CurrentUser currentUser)
    {
        _db = db;
        _secrets = secrets;
        _queue = queue;
        _runner = runner;
        _currentUser = currentUser;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public long[] TagIds { get; set; } = Array.Empty<long>();

    public bool IsEdit => Id != 0;

    public string? Notice { get; private set; }
    public bool NoticeOk { get; private set; }

    // Option lists.
    public List<SelectListItem> StorageLocationOptions { get; private set; } = new();
    public List<SelectListItem> CorrespondentOptions { get; private set; } = new();
    public List<SelectListItem> DocumentTypeOptions { get; private set; } = new();
    public List<SelectListItem> ProjectOptions { get; private set; } = new();
    public List<TagOption> TagOptions { get; private set; } = new();

    public record TagOption(long Id, string Name, bool Selected);

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;

        // Bound as int so the option values ("0"/"1"/"2") drive both selection and
        // the client-side show-when comparison.
        public int Type { get; set; } = (int)ImportTaskType.Filesystem;

        public bool IsEnabled { get; set; } = true;
        public string? CronExpression { get; set; }

        // Filesystem.
        public string SourcePath { get; set; } = string.Empty;
        public string Pattern { get; set; } = "*";
        public bool Recursive { get; set; }
        public string PostAction { get; set; } = "none";
        public string? MoveToPath { get; set; }

        // Mail (IMAP / POP3).
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 993;
        public bool UseSsl { get; set; } = true;
        public string Username { get; set; } = string.Empty;
        public string? Password { get; set; }
        public string Folder { get; set; } = "INBOX";
        public string? SenderRegex { get; set; }
        public string? SubjectRegex { get; set; }
        public string AttachmentExtensions { get; set; } = DefaultAttachmentExtensions;
        public string MailPostAction { get; set; } = "markseen";

        // Common metadata defaults.
        public long? StorageLocationId { get; set; }
        public long? CorrespondentId { get; set; }
        public long? DocumentTypeId { get; set; }
        public long? ProjectId { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        SetBreadcrumb();

        if (IsEdit)
        {
            var entity = await _db.ImportTasks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == Id && t.UpdateState != UpdateState.Deleted);
            if (entity == null)
            {
                return NotFound();
            }

            LoadFromEntity(entity);
        }

        await BuildOptionListsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetBreadcrumb();

        Validate();
        if (!ModelState.IsValid)
        {
            await BuildOptionListsAsync();
            return Page();
        }

        var now = DateTime.UtcNow;

        ImportTask entity;
        if (IsEdit)
        {
            var existing = await _db.ImportTasks
                .FirstOrDefaultAsync(t => t.Id == Id && t.UpdateState != UpdateState.Deleted);
            if (existing == null)
            {
                return NotFound();
            }
            entity = existing;
            entity.UpdateState = UpdateState.Updated;
        }
        else
        {
            entity = new ImportTask
            {
                UpdateState = UpdateState.Created,
                CreateDate = now,
                CreateUserId = _currentUser.UserId
            };
            _db.ImportTasks.Add(entity);
        }

        entity.Name = Input.Name.Trim();
        entity.Type = (ImportTaskType)Input.Type;
        entity.IsEnabled = Input.IsEnabled;
        entity.CronExpression = string.IsNullOrWhiteSpace(Input.CronExpression) ? null : Input.CronExpression.Trim();
        entity.SettingsJson = BuildSettingsJson(entity.SettingsJson);
        entity.UpdateDate = now;
        entity.UpdateUserId = _currentUser.UserId;

        await _db.SaveChangesAsync();

        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostRunAsync()
    {
        SetBreadcrumb();

        if (!IsEdit)
        {
            ModelState.AddModelError(string.Empty, "Save the task before running it.");
            await BuildOptionListsAsync();
            return Page();
        }

        var exists = await _db.ImportTasks
            .AnyAsync(t => t.Id == Id && t.UpdateState != UpdateState.Deleted);
        if (!exists)
        {
            return NotFound();
        }

        _queue.Enqueue(TaskRunKind.Import, Id);
        TempData["TaskMessage"] = "Task queued";

        return RedirectToPage("Edit", new { id = Id });
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken ct)
    {
        SetBreadcrumb();

        // When editing and no new password was typed, fall back to the stored (protected)
        // password so the test uses the real credential instead of an empty one.
        var protectedPassword = string.Empty;
        if (IsEdit && string.IsNullOrEmpty(Input.Password))
        {
            var stored = await _db.ImportTasks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == Id && t.UpdateState != UpdateState.Deleted, ct);
            if (stored != null && stored.Type != ImportTaskType.Filesystem)
            {
                protectedPassword = TaskSettingsJson.Read<MailImportSettings>(stored.SettingsJson).ProtectedPassword;
            }
        }

        var settings = BuildMailSettings(protectedPassword);
        var isPop3 = Input.Type == (int)ImportTaskType.Pop3;
        var plaintextOverride = string.IsNullOrEmpty(Input.Password) ? null : Input.Password;

        var (ok, message) = await _runner.TestMailConnectionAsync(settings, isPop3, plaintextOverride, ct);
        NoticeOk = ok;
        Notice = message;

        await BuildOptionListsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!IsEdit)
        {
            return RedirectToPage("Index");
        }

        var entity = await _db.ImportTasks
            .FirstOrDefaultAsync(t => t.Id == Id && t.UpdateState != UpdateState.Deleted);
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

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Input.Name))
        {
            ModelState.AddModelError("Input.Name", "Name is required.");
        }

        if (Input.Type == (int)ImportTaskType.Filesystem)
        {
            if (string.IsNullOrWhiteSpace(Input.SourcePath))
            {
                ModelState.AddModelError("Input.SourcePath", "Source path is required.");
            }
            if (Input.PostAction == "move" && string.IsNullOrWhiteSpace(Input.MoveToPath))
            {
                ModelState.AddModelError("Input.MoveToPath", "A move target path is required for the move action.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Input.Host))
            {
                ModelState.AddModelError("Input.Host", "Host is required.");
            }
            if (string.IsNullOrWhiteSpace(Input.Username))
            {
                ModelState.AddModelError("Input.Username", "Username is required.");
            }
        }
    }

    private string BuildSettingsJson(string? existingJson)
    {
        if (Input.Type == (int)ImportTaskType.Filesystem)
        {
            var fs = new FilesystemImportSettings
            {
                SourcePath = Input.SourcePath.Trim(),
                Pattern = string.IsNullOrWhiteSpace(Input.Pattern) ? "*" : Input.Pattern.Trim(),
                Recursive = Input.Recursive,
                PostAction = Input.PostAction,
                MoveToPath = string.IsNullOrWhiteSpace(Input.MoveToPath) ? null : Input.MoveToPath.Trim(),
                StorageLocationId = Input.StorageLocationId,
                CorrespondentId = Input.CorrespondentId,
                DocumentTypeId = Input.DocumentTypeId,
                ProjectId = Input.ProjectId,
                TagIds = TagIds.ToList()
            };
            return TaskSettingsJson.Write(fs);
        }

        // Mail: keep the stored password unless a new one was entered.
        var existing = TaskSettingsJson.Read<MailImportSettings>(existingJson);
        var protectedPassword = string.IsNullOrEmpty(Input.Password)
            ? existing.ProtectedPassword
            : _secrets.Protect(Input.Password);

        var mail = BuildMailSettings(protectedPassword);
        return TaskSettingsJson.Write(mail);
    }

    private MailImportSettings BuildMailSettings(string protectedPassword)
    {
        return new MailImportSettings
        {
            Host = Input.Host.Trim(),
            Port = Input.Port,
            UseSsl = Input.UseSsl,
            Username = Input.Username.Trim(),
            ProtectedPassword = protectedPassword,
            Folder = string.IsNullOrWhiteSpace(Input.Folder) ? "INBOX" : Input.Folder.Trim(),
            SenderRegex = string.IsNullOrWhiteSpace(Input.SenderRegex) ? null : Input.SenderRegex.Trim(),
            SubjectRegex = string.IsNullOrWhiteSpace(Input.SubjectRegex) ? null : Input.SubjectRegex.Trim(),
            AttachmentExtensions = string.IsNullOrWhiteSpace(Input.AttachmentExtensions)
                ? DefaultAttachmentExtensions
                : Input.AttachmentExtensions.Trim(),
            PostAction = Input.MailPostAction,
            StorageLocationId = Input.StorageLocationId,
            CorrespondentId = Input.CorrespondentId,
            DocumentTypeId = Input.DocumentTypeId,
            ProjectId = Input.ProjectId,
            TagIds = TagIds.ToList()
        };
    }

    private void LoadFromEntity(ImportTask entity)
    {
        Input.Name = entity.Name;
        Input.Type = (int)entity.Type;
        Input.IsEnabled = entity.IsEnabled;
        Input.CronExpression = entity.CronExpression;

        if (entity.Type == ImportTaskType.Filesystem)
        {
            var fs = TaskSettingsJson.Read<FilesystemImportSettings>(entity.SettingsJson);
            Input.SourcePath = fs.SourcePath;
            Input.Pattern = string.IsNullOrWhiteSpace(fs.Pattern) ? "*" : fs.Pattern;
            Input.Recursive = fs.Recursive;
            Input.PostAction = fs.PostAction;
            Input.MoveToPath = fs.MoveToPath;
            Input.StorageLocationId = fs.StorageLocationId;
            Input.CorrespondentId = fs.CorrespondentId;
            Input.DocumentTypeId = fs.DocumentTypeId;
            Input.ProjectId = fs.ProjectId;
            TagIds = fs.TagIds.ToArray();
        }
        else
        {
            var mail = TaskSettingsJson.Read<MailImportSettings>(entity.SettingsJson);
            Input.Host = mail.Host;
            Input.Port = mail.Port;
            Input.UseSsl = mail.UseSsl;
            Input.Username = mail.Username;
            // Never surface the stored password.
            Input.Password = null;
            Input.Folder = string.IsNullOrWhiteSpace(mail.Folder) ? "INBOX" : mail.Folder;
            Input.SenderRegex = mail.SenderRegex;
            Input.SubjectRegex = mail.SubjectRegex;
            Input.AttachmentExtensions = string.IsNullOrWhiteSpace(mail.AttachmentExtensions)
                ? DefaultAttachmentExtensions
                : mail.AttachmentExtensions;
            Input.MailPostAction = mail.PostAction;
            Input.StorageLocationId = mail.StorageLocationId;
            Input.CorrespondentId = mail.CorrespondentId;
            Input.DocumentTypeId = mail.DocumentTypeId;
            Input.ProjectId = mail.ProjectId;
            TagIds = mail.TagIds.ToArray();
        }
    }

    private async Task BuildOptionListsAsync()
    {
        var storageLocations = await _db.StorageLocations
            .AsNoTracking()
            .Where(s => s.UpdateState != UpdateState.Deleted)
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name })
            .ToListAsync();

        var correspondents = await _db.Correspondents
            .AsNoTracking()
            .Where(c => c.UpdateState != UpdateState.Deleted)
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync();

        var documentTypes = await _db.DocumentTypes
            .AsNoTracking()
            .Where(t => t.UpdateState != UpdateState.Deleted)
            .OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync();

        var projects = await _db.Projects
            .AsNoTracking()
            .Where(p => p.UpdateState != UpdateState.Deleted)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync();

        var tags = await _db.Tags
            .AsNoTracking()
            .Where(t => t.UpdateState != UpdateState.Deleted)
            .OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync();

        StorageLocationOptions = BuildOptions(
            storageLocations.Select(s => (s.Id, s.Name)), Input.StorageLocationId, "— default —");
        CorrespondentOptions = BuildOptions(
            correspondents.Select(c => (c.Id, c.Name)), Input.CorrespondentId, "— None —");
        DocumentTypeOptions = BuildOptions(
            documentTypes.Select(t => (t.Id, t.Name)), Input.DocumentTypeId, "— None —");
        ProjectOptions = BuildOptions(
            projects.Select(p => (p.Id, p.Name)), Input.ProjectId, "— None —");

        var selected = new HashSet<long>(TagIds);
        TagOptions = tags
            .Select(t => new TagOption(t.Id, t.Name, selected.Contains(t.Id)))
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

    private void SetBreadcrumb()
    {
        ViewData["Breadcrumb"] = IsEdit
            ? "System / Import tasks / Edit"
            : "System / Import tasks / New";
    }
}
