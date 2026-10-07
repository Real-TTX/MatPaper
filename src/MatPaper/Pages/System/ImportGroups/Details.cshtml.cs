using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.System.ImportGroups;

/// <summary>Read-only view of an import group: its target, the rules in running order, its runs and what it brought in.</summary>
public class DetailsModel : PageModel
{
    private const int RecentDocuments = 10;

    private readonly AppDbContext _db;
    private readonly IStringLocalizer<SharedResource> _l;

    public DetailsModel(AppDbContext db, IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _l = l;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public ImportGroup Group { get; private set; } = new();
    public TaskRunsPanel Runs { get; private set; } = null!;
    public TaskRun? LastRun { get; private set; }

    /// <summary>Label/value pairs describing the target every rule of the group shares.</summary>
    public List<(string Label, string Value)> Target { get; } = new();

    /// <summary>What the group's rules read from (empty when each rule has its own source).</summary>
    public List<(string Label, string Value)> Source { get; } = new();

    public record Member(long Id, string Name, ImportTaskType Type, bool IsEnabled, TaskRunState? LastState, DateTime? LastStartedAt);

    public List<Member> Members { get; private set; } = new();

    public int DocumentCount { get; private set; }
    public IReadOnlyList<ImportTasks.DetailsModel.RecentDocument> Documents { get; private set; } = Array.Empty<ImportTasks.DetailsModel.RecentDocument>();

    public async Task<IActionResult> OnGetAsync()
    {
        var group = await _db.ImportGroups.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == Id && g.UpdateState != UpdateState.Deleted);
        if (group is null)
        {
            return NotFound();
        }

        Group = group;
        ViewData["Breadcrumb"] = _l["System / Import tasks / Group"].Value;
        ViewData["Title"] = group.Name;

        await DescribeSourceAsync(group);
        await DescribeTargetAsync(group);

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

        Runs = new TaskRunsPanel(
            TaskRunKind.ImportGroup, Id,
            $"/System/ImportGroups/Edit?id={Id}&handler=Run",
            _l["Run group now"].Value,
            null, null,
            await TaskRunsPanel.LoadRunsAsync(_db, TaskRunKind.ImportGroup, Id));
        LastRun = Runs.Runs.FirstOrDefault();

        var docs = _db.Documents.AsNoTracking()
            .Where(d => d.ImportTaskId != null && ids.Contains(d.ImportTaskId.Value) && d.UpdateState != UpdateState.Deleted);
        DocumentCount = await docs.CountAsync();
        Documents = await docs
            .OrderByDescending(d => d.CreateDate)
            .Take(RecentDocuments)
            .Select(d => new ImportTasks.DetailsModel.RecentDocument(d.Id, d.Token, d.Title, d.OriginalFileName, d.CreateDate,
                d.ThumbnailPath != null, d.ReviewState, d.IsStaged))
            .ToListAsync();

        return Page();
    }

    private async Task DescribeSourceAsync(ImportGroup g)
    {
        if (g.SourceType is not ImportTaskType type) { return; }
        Source.Add((_l["Type"].Value, type switch
        {
            ImportTaskType.Imap => "IMAP",
            ImportTaskType.Pop3 => "POP3",
            ImportTaskType.Smb => _l["SMB share"].Value,
            _ => _l["Watched folder"].Value
        }));
        if (g.SourceConnectionId is long cid)
        {
            var name = await _db.Connections.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync();
            if (name is not null) { Source.Add((_l["Connection"].Value, name)); }
        }
        if (type == ImportTaskType.Smb && !string.IsNullOrWhiteSpace(g.SourceShare)) { Source.Add((_l["SMB share"].Value, g.SourceShare)); }
        if (!string.IsNullOrWhiteSpace(g.SourcePath))
        {
            Source.Add((type == ImportTaskType.Imap ? _l["Mailbox folder"].Value : _l["Folder"].Value, g.SourcePath));
        }
    }

    private async Task DescribeTargetAsync(ImportGroup g)
    {
        const string none = "—";
        Target.Add((_l["Storage location"].Value, g.StorageLocationId is long lid
            ? await _db.StorageLocations.AsNoTracking().Where(x => x.Id == lid).Select(x => x.Name).FirstOrDefaultAsync() ?? none
            : _l["Default location"].Value));
        if (g.ProjectId is long pid)
        {
            Target.Add((_l["Project"].Value, await _db.Projects.AsNoTracking().Where(x => x.Id == pid).Select(x => x.Name).FirstOrDefaultAsync() ?? none));
        }

        var tagIds = ImportGroupDefaults.ParseTagIds(g.TagIds).ToList();
        if (tagIds.Count > 0)
        {
            var names = await _db.Tags.AsNoTracking().Where(x => tagIds.Contains(x.Id)).OrderBy(x => x.Name).Select(x => x.Name).ToListAsync();
            Target.Add((_l["Tags"].Value, string.Join(", ", names)));
        }

        Target.Add((_l["Lands in"].Value, g.IsCommon ? _l["Common area"].Value : _l["Review inbox"].Value));
        if (g.SkipInbox) { Target.Add((_l["Review"].Value, _l["Skips the inbox"].Value)); }
        if (g.OwnerUserId is long oid)
        {
            var owner = await _db.Users.AsNoTracking().Where(x => x.Id == oid).Select(x => x.Username).FirstOrDefaultAsync();
            if (owner is not null) { Target.Add((_l["Owner"].Value, owner)); }
        }
    }
}
