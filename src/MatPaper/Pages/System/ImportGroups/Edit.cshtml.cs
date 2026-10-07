using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.System.ImportGroups;

/// <summary>
/// An import group: a target and a schedule shared by several rules, and the order they run in
/// (top rule first). Rules are added from the rule's own editor or taken in from the single rules here.
/// </summary>
public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly TaskTriggerQueue _tasks;
    private readonly CurrentUser _currentUser;
    private readonly IStringLocalizer<SharedResource> _l;

    public EditModel(AppDbContext db, TaskTriggerQueue tasks, CurrentUser currentUser, IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _tasks = tasks;
        _currentUser = currentUser;
        _l = l;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public long[] TagIds { get; set; } = Array.Empty<long>();

    public bool IsEdit => Id != 0;

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public string? CronExpression { get; set; }
        public long? StorageLocationId { get; set; }
        public long? OwnerUserId { get; set; }
        public bool IsCommon { get; set; }
        public bool SkipInbox { get; set; }
        public long? ProjectId { get; set; }
    }

    public record Member(long Id, string Name, ImportTaskType Type, bool IsEnabled, TaskRunState? LastState, DateTime? LastStartedAt);

    public List<Member> Members { get; private set; } = new();
    public List<(long Id, string Name)> SingleRules { get; private set; } = new();
    public List<SelectListItem> StorageLocationOptions { get; private set; } = new();
    public List<SelectListItem> UserOptions { get; private set; } = new();
    public List<SelectListItem> ProjectOptions { get; private set; } = new();
    public List<SelectListItem> TagOptions { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        SetBreadcrumb();
        if (IsEdit)
        {
            var group = await _db.ImportGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == Id && g.UpdateState != UpdateState.Deleted);
            if (group is null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Name = group.Name,
                IsEnabled = group.IsEnabled,
                CronExpression = group.CronExpression,
                StorageLocationId = group.StorageLocationId,
                OwnerUserId = group.OwnerUserId,
                IsCommon = group.IsCommon,
                SkipInbox = group.SkipInbox,
                ProjectId = group.ProjectId
            };
            TagIds = ImportGroupDefaults.ParseTagIds(group.TagIds).ToArray();
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetBreadcrumb();

        var name = Input.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            ModelState.AddModelError("Input.Name", _l["Name is required."]);
        }
        else if (await _db.ImportGroups.AnyAsync(g => g.Id != Id && g.UpdateState != UpdateState.Deleted && g.Name.ToLower() == name.ToLower()))
        {
            ModelState.AddModelError("Input.Name", _l["A group with that name already exists."]);
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        ImportGroup group;
        var now = DateTime.UtcNow;
        if (IsEdit)
        {
            group = await _db.ImportGroups.FirstOrDefaultAsync(g => g.Id == Id && g.UpdateState != UpdateState.Deleted) ?? throw new InvalidOperationException();
            group.UpdateState = UpdateState.Updated;
        }
        else
        {
            group = new ImportGroup { CreateDate = now, CreateUserId = _currentUser.UserId, UpdateState = UpdateState.Created };
            _db.ImportGroups.Add(group);
        }

        group.Name = name;
        group.IsEnabled = Input.IsEnabled;
        group.CronExpression = string.IsNullOrWhiteSpace(Input.CronExpression) ? null : Input.CronExpression.Trim();
        group.StorageLocationId = Input.StorageLocationId;
        group.OwnerUserId = Input.OwnerUserId;
        group.IsCommon = Input.IsCommon;
        group.SkipInbox = Input.SkipInbox;
        group.ProjectId = Input.ProjectId;
        group.TagIds = TagIds.Length == 0 ? null : string.Join(",", TagIds.Distinct());
        group.UpdateDate = now;
        group.UpdateUserId = _currentUser.UserId;
        await _db.SaveChangesAsync();

        this.Notify(_l["Saved"].Value);
        return RedirectToPage("Details", new { id = group.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var group = await _db.ImportGroups.FirstOrDefaultAsync(g => g.Id == Id && g.UpdateState != UpdateState.Deleted);
        if (group is null)
        {
            return RedirectToPage("/System/ImportTasks/Index");
        }

        // The rules stay; they are on their own again (with the schedule they had before).
        var members = await _db.ImportTasks.Where(t => t.GroupId == Id).ToListAsync();
        foreach (var rule in members) { rule.GroupId = null; rule.Priority = 0; }

        group.UpdateState = UpdateState.Deleted;
        group.UpdateDate = DateTime.UtcNow;
        group.UpdateUserId = _currentUser.UserId;
        await _db.SaveChangesAsync();

        this.Notify(_l["Group deleted. Its rules stay as single rules."].Value);
        return RedirectToPage("/System/ImportTasks/Index");
    }

    public async Task<IActionResult> OnPostRunAsync()
    {
        var exists = await _db.ImportGroups.AnyAsync(g => g.Id == Id && g.UpdateState != UpdateState.Deleted);
        if (!exists)
        {
            return NotFound();
        }

        if (_tasks.Enqueue(TaskRunKind.ImportGroup, Id, _currentUser.UserId))
        {
            this.Notify(_l["Group queued"].Value);
        }
        else
        {
            this.Notify(_l["This group is already running."].Value, NoticeKind.Warn);
        }

        return RedirectToPage("Details", new { id = Id });
    }

    /// <summary>Moves a rule one place up or down; the order is renumbered so it stays gap-free.</summary>
    public async Task<IActionResult> OnPostMoveAsync(long ruleId, int delta)
    {
        var members = await _db.ImportTasks
            .Where(t => t.GroupId == Id && t.UpdateState != UpdateState.Deleted)
            .OrderBy(t => t.Priority).ThenBy(t => t.Id)
            .ToListAsync();
        var index = members.FindIndex(t => t.Id == ruleId);
        var target = index + Math.Sign(delta);
        if (index >= 0 && target >= 0 && target < members.Count)
        {
            (members[index], members[target]) = (members[target], members[index]);
        }

        for (var i = 0; i < members.Count; i++) { members[i].Priority = i; }
        await _db.SaveChangesAsync();
        return RedirectToPage("Edit", new { id = Id });
    }

    public async Task<IActionResult> OnPostAddAsync(long ruleId)
    {
        var rule = await _db.ImportTasks.FirstOrDefaultAsync(t => t.Id == ruleId && t.GroupId == null && t.UpdateState != UpdateState.Deleted);
        if (rule is not null)
        {
            var last = await _db.ImportTasks.Where(t => t.GroupId == Id).MaxAsync(t => (int?)t.Priority) ?? -1;
            rule.GroupId = Id;
            rule.Priority = last + 1;
            await _db.SaveChangesAsync();
        }

        return RedirectToPage("Edit", new { id = Id });
    }

    public async Task<IActionResult> OnPostRemoveAsync(long ruleId)
    {
        var rule = await _db.ImportTasks.FirstOrDefaultAsync(t => t.Id == ruleId && t.GroupId == Id);
        if (rule is not null)
        {
            rule.GroupId = null;
            rule.Priority = 0;
            await _db.SaveChangesAsync();
        }

        return RedirectToPage("Edit", new { id = Id });
    }

    private void SetBreadcrumb()
        => ViewData["Breadcrumb"] = IsEdit ? _l["System / Import tasks / Group"].Value : _l["System / Import tasks / New group"].Value;

    private async Task LoadAsync()
    {
        StorageLocationOptions = (await _db.StorageLocations.AsNoTracking()
                .Where(s => s.UpdateState != UpdateState.Deleted).OrderBy(s => s.Name).Select(s => new { s.Id, s.Name }).ToListAsync())
            .Select(s => new SelectListItem(s.Name, s.Id.ToString())).ToList();
        UserOptions = (await _db.Users.AsNoTracking()
                .Where(u => u.IsActive).OrderBy(u => u.DisplayName).Select(u => new { u.Id, u.DisplayName, u.Username }).ToListAsync())
            .Select(u => new SelectListItem(string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName, u.Id.ToString())).ToList();
        ProjectOptions = (await _db.Projects.AsNoTracking()
                .Where(p => p.UpdateState != UpdateState.Deleted).OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync())
            .Select(p => new SelectListItem(p.Name, p.Id.ToString())).ToList();
        TagOptions = (await _db.Tags.AsNoTracking()
                .Where(t => t.UpdateState != UpdateState.Deleted).OrderBy(t => t.Name).Select(t => new { t.Id, t.Name }).ToListAsync())
            .Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToList();

        if (!IsEdit)
        {
            return;
        }

        var rules = await _db.ImportTasks.AsNoTracking()
            .Where(t => t.GroupId == Id && t.UpdateState != UpdateState.Deleted)
            .OrderBy(t => t.Priority).ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.Name, t.Type, t.IsEnabled })
            .ToListAsync();
        var ids = rules.Select(r => r.Id).ToList();
        var latest = (await _db.TaskRuns.AsNoTracking()
                .Where(r => r.Kind == TaskRunKind.Import && ids.Contains(r.TaskId))
                .Select(r => new { r.TaskId, r.State, r.StartedAt }).ToListAsync())
            .GroupBy(r => r.TaskId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StartedAt).First());
        Members = rules.Select(r =>
        {
            latest.TryGetValue(r.Id, out var run);
            return new Member(r.Id, r.Name, r.Type, r.IsEnabled, run?.State, run?.StartedAt);
        }).ToList();

        SingleRules = (await _db.ImportTasks.AsNoTracking()
                .Where(t => t.GroupId == null && t.UpdateState != UpdateState.Deleted).OrderBy(t => t.Name)
                .Select(t => new { t.Id, t.Name }).ToListAsync())
            .Select(t => (t.Id, t.Name)).ToList();
    }
}
