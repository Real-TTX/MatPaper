using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;

namespace MatPaper.Pages.System.TaskRuns;

public class DetailsModel : PageModel
{
    private readonly AppDbContext _db;

    public DetailsModel(AppDbContext db)
    {
        _db = db;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public TaskRun Run { get; private set; } = null!;
    public string TaskName { get; private set; } = string.Empty;
    public TimeSpan? Duration { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        ViewData["Breadcrumb"] = "System / Task history / Run";

        var run = await _db.TaskRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == Id);
        if (run == null)
        {
            return NotFound();
        }

        Run = run;
        Duration = run.FinishedAt is null ? null : run.FinishedAt.Value - run.StartedAt;

        if (run.Kind == TaskRunKind.Import)
        {
            var task = await _db.ImportTasks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == run.TaskId);
            TaskName = task?.Name ?? $"#{run.TaskId} (deleted)";
        }
        else
        {
            var task = await _db.ExportTasks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == run.TaskId);
            TaskName = task?.Name ?? $"#{run.TaskId} (deleted)";
        }

        return Page();
    }
}
