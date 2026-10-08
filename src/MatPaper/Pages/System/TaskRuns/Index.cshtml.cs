using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;

namespace MatPaper.Pages.System.TaskRuns;

public class IndexModel : PageModel
{
    private const int PageSize = 50;

    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Part of the name of the task (or group, or storage location) a run belongs to.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Kind { get; set; } = "all";

    /// <summary>"all" or a <see cref="TaskRunState"/> name.</summary>
    [BindProperty(SupportsGet = true)]
    public string Status { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "started_desc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; }

    public sealed record Row(
        long Id,
        DateTime StartedAt,
        TaskRunKind Kind,
        string TaskName,
        TaskRunState State,
        int ItemsProcessed,
        TimeSpan? Duration);

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "System / Task history";

        IQueryable<TaskRun> query = _db.TaskRuns.AsNoTracking();

        TaskRunKind? kindFilter = Kind switch
        {
            "import" => TaskRunKind.Import,
            "export" => TaskRunKind.Export,
            "scan" => TaskRunKind.Scan,
            "align" => TaskRunKind.Align,
            "importgroup" => TaskRunKind.ImportGroup,
            _ => null
        };

        if (kindFilter is not null)
        {
            query = query.Where(r => r.Kind == kindFilter.Value);
        }

        if (Enum.TryParse<TaskRunState>(Status, true, out var state) && Enum.IsDefined(state))
        {
            query = query.Where(r => r.State == state);
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            // A run only knows the id of its task; the names live in four tables, one per kind.
            var pattern = $"%{Search.Trim()}%";
            var matchImport = await _db.ImportTasks.AsNoTracking().Where(t => EF.Functions.ILike(t.Name, pattern)).Select(t => t.Id).ToListAsync();
            var matchExport = await _db.ExportTasks.AsNoTracking().Where(t => EF.Functions.ILike(t.Name, pattern)).Select(t => t.Id).ToListAsync();
            var matchLocation = await _db.StorageLocations.AsNoTracking().Where(t => EF.Functions.ILike(t.Name, pattern)).Select(t => t.Id).ToListAsync();
            var matchGroup = await _db.ImportGroups.AsNoTracking().Where(t => EF.Functions.ILike(t.Name, pattern)).Select(t => t.Id).ToListAsync();
            query = query.Where(r =>
                (r.Kind == TaskRunKind.Import && matchImport.Contains(r.TaskId))
                || (r.Kind == TaskRunKind.Export && matchExport.Contains(r.TaskId))
                || ((r.Kind == TaskRunKind.Scan || r.Kind == TaskRunKind.Align) && matchLocation.Contains(r.TaskId))
                || (r.Kind == TaskRunKind.ImportGroup && matchGroup.Contains(r.TaskId)));
        }

        query = Sort == "started_asc"
            ? query.OrderBy(r => r.StartedAt)
            : query.OrderByDescending(r => r.StartedAt);

        TotalCount = await query.CountAsync();
        TotalPages = TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);

        if (PageNumber < 1)
        {
            PageNumber = 1;
        }
        if (PageNumber > TotalPages)
        {
            PageNumber = TotalPages;
        }

        var runs = await query
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();

        var importIds = runs.Where(r => r.Kind == TaskRunKind.Import).Select(r => r.TaskId).Distinct().ToList();
        var exportIds = runs.Where(r => r.Kind == TaskRunKind.Export).Select(r => r.TaskId).Distinct().ToList();
        var scanIds = runs.Where(r => r.Kind == TaskRunKind.Scan || r.Kind == TaskRunKind.Align).Select(r => r.TaskId).Distinct().ToList();
        var groupIds = runs.Where(r => r.Kind == TaskRunKind.ImportGroup).Select(r => r.TaskId).Distinct().ToList();
        var groupNames = await _db.ImportGroups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name);

        var importNames = await _db.ImportTasks
            .AsNoTracking()
            .Where(t => importIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name);

        var exportNames = await _db.ExportTasks
            .AsNoTracking()
            .Where(t => exportIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name);

        // A search run carries the storage location id, so it must not be looked up in the
        // export tasks — that would show an unrelated task name.
        var scanNames = await _db.StorageLocations
            .AsNoTracking()
            .Where(s => scanIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name);

        Rows = runs.Select(r =>
        {
            var names = r.Kind switch
            {
                TaskRunKind.Import => importNames,
                TaskRunKind.Export => exportNames,
                TaskRunKind.ImportGroup => groupNames,
                _ => scanNames
            };
            var name = names.TryGetValue(r.TaskId, out var n) ? n : $"#{r.TaskId} (deleted)";
            var duration = r.FinishedAt is null ? (TimeSpan?)null : r.FinishedAt.Value - r.StartedAt;
            return new Row(r.Id, r.StartedAt, r.Kind, name, r.State, r.ItemsProcessed, duration);
        }).ToList();
    }
}
