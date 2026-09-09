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

    [BindProperty(SupportsGet = true)]
    public string Kind { get; set; } = "all";

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
            _ => null
        };

        if (kindFilter is not null)
        {
            query = query.Where(r => r.Kind == kindFilter.Value);
        }

        query = query.OrderByDescending(r => r.StartedAt);

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

        var importNames = await _db.ImportTasks
            .AsNoTracking()
            .Where(t => importIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name);

        var exportNames = await _db.ExportTasks
            .AsNoTracking()
            .Where(t => exportIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name);

        Rows = runs.Select(r =>
        {
            var names = r.Kind == TaskRunKind.Import ? importNames : exportNames;
            var name = names.TryGetValue(r.TaskId, out var n) ? n : $"#{r.TaskId} (deleted)";
            var duration = r.FinishedAt is null ? (TimeSpan?)null : r.FinishedAt.Value - r.StartedAt;
            return new Row(r.Id, r.StartedAt, r.Kind, name, r.State, r.ItemsProcessed, duration);
        }).ToList();
    }
}
