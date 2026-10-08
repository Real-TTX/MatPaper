using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.System.ExportTasks;

/// <summary>Read-only view of an export rule: what it copies and where, its runs and the log of the last one.</summary>
public class DetailsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IStringLocalizer<SharedResource> _l;

    public DetailsModel(AppDbContext db, IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _l = l;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public ExportTask Task { get; private set; } = new();
    public TaskRunsPanel Runs { get; private set; } = null!;
    public TaskRun? LastRun { get; private set; }

    /// <summary>What the rule picks (documents) or includes (backup).</summary>
    public List<(string Label, string Value)> What { get; } = new();

    /// <summary>Where the copies go and under which path.</summary>
    public List<(string Label, string Value)> Destination { get; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        var task = await _db.ExportTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == Id && t.UpdateState != UpdateState.Deleted);
        if (task is null)
        {
            return NotFound();
        }

        Task = task;
        ViewData["Breadcrumb"] = _l["System / Export tasks / View"].Value;
        ViewData["Title"] = task.Name;

        if (task.Type == ExportTaskType.Export)
        {
            await DescribeExportAsync(TaskSettingsJson.Read<DocumentExportSettings>(task.SettingsJson));
        }
        else
        {
            var b = TaskSettingsJson.Read<BackupSettings>(task.SettingsJson);
            What.Add((_l["Database"].Value, _l["Always included"].Value));
            What.Add((_l["Configuration"].Value, b.IncludeConfig ? _l["Included"].Value : "—"));
            What.Add((_l["Documents"].Value, b.IncludeDocuments ? _l["Included"].Value : "—"));
            Destination.Add((_l["Target path"].Value, b.TargetPath));
            Destination.Add((_l["Retention"].Value, _l["{0} backups are kept", b.Retention].Value));
        }

        Runs = new TaskRunsPanel(
            TaskRunKind.Export, Id, $"/System/ExportTasks/Edit?id={Id}&handler=Run", _l["Run now"].Value,
            null, null, await TaskRunsPanel.LoadRunsAsync(_db, TaskRunKind.Export, Id));
        LastRun = Runs.Runs.FirstOrDefault();
        return Page();
    }

    private async Task DescribeExportAsync(DocumentExportSettings s)
    {
        const string none = "—";
        if (s.TagIds.Count > 0)
        {
            var names = await _db.Tags.AsNoTracking().Where(t => s.TagIds.Contains(t.Id)).OrderBy(t => t.Name).Select(t => t.Name).ToListAsync();
            What.Add((_l["Tags"].Value, string.Join(", ", names)));
        }
        if (s.CorrespondentId is long cid)
        {
            What.Add((_l["Correspondent"].Value, await _db.Correspondents.AsNoTracking().Where(x => x.Id == cid).Select(x => x.Name).FirstOrDefaultAsync() ?? none));
        }
        if (s.DocumentTypeId is long tid)
        {
            What.Add((_l["Document type"].Value, await _db.DocumentTypes.AsNoTracking().Where(x => x.Id == tid).Select(x => x.Name).FirstOrDefaultAsync() ?? none));
        }
        if (s.ProjectId is long pid)
        {
            What.Add((_l["Project"].Value, await _db.Projects.AsNoTracking().Where(x => x.Id == pid).Select(x => x.Name).FirstOrDefaultAsync() ?? none));
        }
        if (s.OwnerUserId is long oid)
        {
            What.Add((_l["Owner"].Value, await _db.Users.AsNoTracking().Where(x => x.Id == oid).Select(x => x.Username).FirstOrDefaultAsync() ?? none));
        }
        if (s.SourceLocationId is long lid)
        {
            What.Add((_l["Only documents in this location"].Value, await _db.StorageLocations.AsNoTracking().Where(x => x.Id == lid).Select(x => x.Name).FirstOrDefaultAsync() ?? none));
        }
        if (!string.IsNullOrWhiteSpace(s.SourceFolder))
        {
            What.Add((_l["Only in this folder"].Value, s.SourceFolder));
        }
        if (What.Count == 0)
        {
            What.Add((_l["Documents"].Value, _l["All documents"].Value));
        }

        Destination.Add((_l["Target location"].Value, s.TargetLocationId is long target
            ? await _db.StorageLocations.AsNoTracking().Where(x => x.Id == target).Select(x => x.Name).FirstOrDefaultAsync() ?? none
            : none));
        Destination.Add((_l["Path in the target"].Value, s.PathTemplate));
    }
}
