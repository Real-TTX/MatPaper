using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;

namespace MatPaper.Pages.System.ExportTasks;

public class IndexModel : PageModel
{
    private const int PageSize = 20;

    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db)
    {
        _db = db;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; }

    public sealed record Row(
        long Id,
        string Name,
        ExportTaskType Type,
        bool IsEnabled,
        string? CronExpression,
        TaskRunState? LastRunState,
        DateTime? LastRunStartedAt);

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "System / Export tasks";

        IQueryable<ExportTask> query = _db.ExportTasks
            .AsNoTracking()
            .Where(t => t.UpdateState != UpdateState.Deleted);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var pattern = $"%{Search.Trim()}%";
            query = query.Where(t => EF.Functions.ILike(t.Name, pattern));
        }

        query = query.OrderBy(t => t.Name);

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

        var tasks = await query
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();

        var ids = tasks.Select(t => t.Id).ToList();

        var maxStarted = await _db.TaskRuns
            .AsNoTracking()
            .Where(r => r.Kind == TaskRunKind.Export && ids.Contains(r.TaskId))
            .GroupBy(r => r.TaskId)
            .Select(g => new { TaskId = g.Key, StartedAt = g.Max(x => x.StartedAt) })
            .ToListAsync();

        var startedValues = maxStarted.Select(m => m.StartedAt).ToList();

        var candidates = await _db.TaskRuns
            .AsNoTracking()
            .Where(r => r.Kind == TaskRunKind.Export
                && ids.Contains(r.TaskId)
                && startedValues.Contains(r.StartedAt))
            .Select(r => new { r.TaskId, r.State, r.StartedAt })
            .ToListAsync();

        var lastByTask = maxStarted
            .Select(m => candidates.First(c => c.TaskId == m.TaskId && c.StartedAt == m.StartedAt))
            .ToDictionary(c => c.TaskId);

        Rows = tasks.Select(t =>
        {
            lastByTask.TryGetValue(t.Id, out var last);
            return new Row(
                t.Id,
                t.Name,
                t.Type,
                t.IsEnabled,
                t.CronExpression,
                last?.State,
                last?.StartedAt);
        }).ToList();
    }
}
