using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.System.ExportTasks;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly TaskTriggerQueue _triggers;
    private readonly CurrentUser _currentUser;
    private readonly IStringLocalizer<SharedResource> _l;

    public EditModel(AppDbContext db, TaskTriggerQueue triggers, CurrentUser currentUser, IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _triggers = triggers;
        _currentUser = currentUser;
        _l = l;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;

    /// <summary>The "Runs" block: run now and latest runs. Null for a new task.</summary>
    public TaskRunsPanel? Runs { get; private set; }

    private async Task LoadRunsAsync()
    {
        if (IsEdit)
        {
            Runs = new TaskRunsPanel(
                TaskRunKind.Export, Id, $"/System/ExportTasks/Edit?id={Id}&handler=Run", _l["Run now"].Value,
                null, null, await TaskRunsPanel.LoadRunsAsync(_db, TaskRunKind.Export, Id));
        }
    }

    public List<SelectListItem> TypeOptions => new()
    {
        new SelectListItem(_l["Export documents"].Value, ((int)ExportTaskType.Export).ToString()),
        new SelectListItem(_l["Full backup"].Value, ((int)ExportTaskType.Backup).ToString())
    };

    public List<SelectListItem> LocationOptions { get; private set; } = new();
    public List<SelectListItem> UserOptions { get; private set; } = new();
    public List<SelectListItem> ProjectOptions { get; private set; } = new();
    public List<SelectListItem> CorrespondentOptions { get; private set; } = new();
    public List<SelectListItem> DocumentTypeOptions { get; private set; } = new();
    public List<SelectListItem> TagOptions { get; private set; } = new();

    [BindProperty]
    public long[] TagIds { get; set; } = Array.Empty<long>();

    private async Task LoadOptionsAsync()
    {
        static List<SelectListItem> Items(IEnumerable<(long Id, string Name)> rows) => rows.Select(r => new SelectListItem(r.Name, r.Id.ToString())).ToList();
        LocationOptions = Items((await _db.StorageLocations.AsNoTracking().Where(x => x.UpdateState != UpdateState.Deleted).OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync()).Select(x => (x.Id, x.Name)));
        UserOptions = Items((await _db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.DisplayName).Select(u => new { u.Id, u.DisplayName, u.Username }).ToListAsync()).Select(u => (u.Id, string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName)));
        ProjectOptions = Items((await _db.Projects.AsNoTracking().Where(x => x.UpdateState != UpdateState.Deleted).OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync()).Select(x => (x.Id, x.Name)));
        CorrespondentOptions = Items((await _db.Correspondents.AsNoTracking().Where(x => x.UpdateState != UpdateState.Deleted).OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync()).Select(x => (x.Id, x.Name)));
        DocumentTypeOptions = Items((await _db.DocumentTypes.AsNoTracking().Where(x => x.UpdateState != UpdateState.Deleted).OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync()).Select(x => (x.Id, x.Name)));
        TagOptions = Items((await _db.Tags.AsNoTracking().Where(x => x.UpdateState != UpdateState.Deleted).OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync()).Select(x => (x.Id, x.Name)));
    }

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public ExportTaskType Type { get; set; } = ExportTaskType.Export;
        public bool IsEnabled { get; set; } = true;
        public string? CronExpression { get; set; }

        // BackupSettings
        public string TargetPath { get; set; } = string.Empty;
        public bool IncludeConfig { get; set; } = true;
        public bool IncludeDocuments { get; set; }
        public int Retention { get; set; } = 7;

        // DocumentExportSettings
        public long? CorrespondentId { get; set; }
        public long? DocumentTypeId { get; set; }
        public long? ProjectId { get; set; }
        public long? OwnerUserId { get; set; }
        public long? SourceLocationId { get; set; }
        public string? SourceFolder { get; set; }
        public long? TargetLocationId { get; set; }
        public string PathTemplate { get; set; } = "{Year}/{Title}{Ext}";
    }

    public async Task<IActionResult> OnGetAsync()
    {
        SetBreadcrumb();

        if (IsEdit)
        {
            var entity = await _db.ExportTasks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == Id && t.UpdateState != UpdateState.Deleted);
            if (entity == null)
            {
                return NotFound();
            }

            var settings = TaskSettingsJson.Read<BackupSettings>(entity.SettingsJson);
            var export = TaskSettingsJson.Read<DocumentExportSettings>(entity.SettingsJson);

            Input = new InputModel
            {
                Name = entity.Name,
                Type = entity.Type,
                IsEnabled = entity.IsEnabled,
                CronExpression = entity.CronExpression,
                TargetPath = settings.TargetPath,
                IncludeConfig = settings.IncludeConfig,
                IncludeDocuments = settings.IncludeDocuments,
                Retention = settings.Retention,
                CorrespondentId = export.CorrespondentId,
                DocumentTypeId = export.DocumentTypeId,
                ProjectId = export.ProjectId,
                OwnerUserId = export.OwnerUserId,
                SourceLocationId = export.SourceLocationId,
                SourceFolder = export.SourceFolder,
                TargetLocationId = export.TargetLocationId,
                PathTemplate = string.IsNullOrWhiteSpace(export.PathTemplate) ? "{Year}/{Title}{Ext}" : export.PathTemplate
            };
            TagIds = export.TagIds.ToArray();
        }

        await LoadOptionsAsync();
        await LoadRunsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetBreadcrumb();

        var name = Input.Name?.Trim() ?? string.Empty;
        var targetPath = Input.TargetPath?.Trim() ?? string.Empty;
        var cron = string.IsNullOrWhiteSpace(Input.CronExpression) ? null : Input.CronExpression.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ModelState.AddModelError("Input.Name", _l["Name is required."]);
        }
        var isExport = Input.Type == ExportTaskType.Export;
        if (isExport)
        {
            if (Input.TargetLocationId is null)
            {
                ModelState.AddModelError("Input.TargetLocationId", _l["A rule needs a target location."]);
            }
            else if (Input.TargetLocationId == Input.SourceLocationId)
            {
                ModelState.AddModelError("Input.TargetLocationId", _l["Choose a different target: the copies would land in the location the documents come from."]);
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                ModelState.AddModelError("Input.TargetPath", _l["Target path is required."]);
            }
            if (Input.Retention < 1)
            {
                ModelState.AddModelError("Input.Retention", _l["Retention must be at least 1."]);
            }
        }
        if (!CronSchedule.IsValid(Input.CronExpression))
        {
            ModelState.AddModelError("Input.CronExpression", _l["The schedule is not a valid cron expression."]);
        }

        if (!ModelState.IsValid)
        {
            await LoadOptionsAsync();
            await LoadRunsAsync();
            return Page();
        }

        var settingsJson = isExport
            ? TaskSettingsJson.Write(new DocumentExportSettings
            {
                TagIds = TagIds.Distinct().ToList(),
                CorrespondentId = Input.CorrespondentId,
                DocumentTypeId = Input.DocumentTypeId,
                ProjectId = Input.ProjectId,
                OwnerUserId = Input.OwnerUserId,
                SourceLocationId = Input.SourceLocationId,
                SourceFolder = string.IsNullOrWhiteSpace(Input.SourceFolder) ? null : Input.SourceFolder.Trim(),
                TargetLocationId = Input.TargetLocationId,
                PathTemplate = string.IsNullOrWhiteSpace(Input.PathTemplate) ? "{Year}/{Title}{Ext}" : Input.PathTemplate.Trim()
            })
            : TaskSettingsJson.Write(new BackupSettings
            {
                TargetPath = targetPath,
                IncludeConfig = Input.IncludeConfig,
                IncludeDocuments = Input.IncludeDocuments,
                Retention = Input.Retention
            });

        var now = DateTime.UtcNow;

        if (IsEdit)
        {
            var existing = await _db.ExportTasks
                .FirstOrDefaultAsync(t => t.Id == Id && t.UpdateState != UpdateState.Deleted);
            if (existing == null)
            {
                return NotFound();
            }

            existing.Name = name;
            existing.Type = Input.Type;
            existing.IsEnabled = Input.IsEnabled;
            existing.CronExpression = cron;
            existing.SettingsJson = settingsJson;
            existing.UpdateState = UpdateState.Updated;
            existing.UpdateDate = now;
            existing.UpdateUserId = _currentUser.UserId;
        }
        else
        {
            var entity = new ExportTask
            {
                Name = name,
                Type = Input.Type,
                IsEnabled = Input.IsEnabled,
                CronExpression = cron,
                SettingsJson = settingsJson,
                UpdateState = UpdateState.Created,
                CreateDate = now,
                CreateUserId = _currentUser.UserId,
                UpdateDate = now,
                UpdateUserId = _currentUser.UserId
            };
            _db.ExportTasks.Add(entity);
        }

        await _db.SaveChangesAsync();

        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostRunAsync()
    {
        if (!IsEdit)
        {
            return RedirectToPage("Index");
        }

        var entity = await _db.ExportTasks
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == Id && t.UpdateState != UpdateState.Deleted);
        if (entity == null)
        {
            return NotFound();
        }

        _triggers.Enqueue(TaskRunKind.Export, Id);
        this.Notify(_l["Export task \"{0}\" queued to run now.", entity.Name].Value);

        return RedirectToPage("Edit", new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!IsEdit)
        {
            return RedirectToPage("Index");
        }

        var entity = await _db.ExportTasks
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

    private void SetBreadcrumb()
    {
        ViewData["Breadcrumb"] = IsEdit
            ? "System / Export tasks / Edit"
            : "System / Export tasks / New";
    }
}
