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

        TaskName = run.Kind switch
        {
            TaskRunKind.Import => (await _db.ImportTasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == run.TaskId))?.Name,
            TaskRunKind.Export => (await _db.ExportTasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == run.TaskId))?.Name,
            // A search run points at a storage location, not at a task.
            _ => (await _db.StorageLocations.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == run.TaskId))?.Name,
        } ?? $"#{run.TaskId} (deleted)";

        return Page();
    }
}
