using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;

namespace MatPaper.Pages.System.ImportTasks;

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

    public record Row(
        long Id,
        string Name,
        ImportTaskType Type,
        bool IsEnabled,
        string? CronExpression,
        TaskRunState? LastState,
        DateTime? LastStartedAt);

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "System / Import tasks";

        IQueryable<ImportTask> query = _db.ImportTasks
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
            .Select(t => new { t.Id, t.Name, t.Type, t.IsEnabled, t.CronExpression })
            .ToListAsync();

        var ids = tasks.Select(t => t.Id).ToList();

        // Latest import run per task on this page.
        var runs = await _db.TaskRuns
            .AsNoTracking()
            .Where(r => r.Kind == TaskRunKind.Import && ids.Contains(r.TaskId))
            .Select(r => new { r.TaskId, r.State, r.StartedAt })
            .ToListAsync();

        var latest = runs
            .GroupBy(r => r.TaskId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StartedAt).First());

        Rows = tasks
            .Select(t =>
            {
                latest.TryGetValue(t.Id, out var run);
                return new Row(
                    t.Id,
                    t.Name,
                    t.Type,
                    t.IsEnabled,
                    t.CronExpression,
                    run?.State,
                    run?.StartedAt);
            })
            .ToList();
    }
}
