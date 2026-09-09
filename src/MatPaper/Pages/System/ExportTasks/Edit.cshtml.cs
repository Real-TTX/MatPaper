using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.System.ExportTasks;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly TaskTriggerQueue _triggers;
    private readonly CurrentUser _currentUser;

    public EditModel(AppDbContext db, TaskTriggerQueue triggers, CurrentUser currentUser)
    {
        _db = db;
        _triggers = triggers;
        _currentUser = currentUser;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;

    public List<SelectListItem> TypeOptions { get; } = new()
    {
        new SelectListItem("Backup", ((int)ExportTaskType.Backup).ToString())
    };

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public ExportTaskType Type { get; set; } = ExportTaskType.Backup;
        public bool IsEnabled { get; set; } = true;
        public string? CronExpression { get; set; }

        // BackupSettings
        public string TargetPath { get; set; } = string.Empty;
        public bool IncludeConfig { get; set; } = true;
        public bool IncludeDocuments { get; set; }
        public int Retention { get; set; } = 7;
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

            Input = new InputModel
            {
                Name = entity.Name,
                Type = entity.Type,
                IsEnabled = entity.IsEnabled,
                CronExpression = entity.CronExpression,
                TargetPath = settings.TargetPath,
                IncludeConfig = settings.IncludeConfig,
                IncludeDocuments = settings.IncludeDocuments,
                Retention = settings.Retention
            };
        }

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
            ModelState.AddModelError("Input.Name", "Name is required.");
        }
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            ModelState.AddModelError("Input.TargetPath", "Target path is required.");
        }
        if (Input.Retention < 1)
        {
            ModelState.AddModelError("Input.Retention", "Retention must be at least 1.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var settings = new BackupSettings
        {
            TargetPath = targetPath,
            IncludeConfig = Input.IncludeConfig,
            IncludeDocuments = Input.IncludeDocuments,
            Retention = Input.Retention
        };
        var settingsJson = TaskSettingsJson.Write(settings);

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
            existing.Type = ExportTaskType.Backup;
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
                Type = ExportTaskType.Backup,
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
        TempData["ExportTaskMessage"] = $"Export task \"{entity.Name}\" queued to run now.";

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
