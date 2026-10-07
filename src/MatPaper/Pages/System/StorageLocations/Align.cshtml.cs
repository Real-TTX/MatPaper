using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.System.StorageLocations;

/// <summary>
/// "Align to template" for one storage location: shows which files lie somewhere other than the path
/// the location's template gives them, and moves them on request (a run in the background, logged like
/// any other task).
/// </summary>
public class AlignModel : PageModel
{
    public const int ShownItems = 200;

    private readonly AppDbContext _db;
    private readonly DocumentAlignService _align;
    private readonly TaskTriggerQueue _tasks;
    private readonly CurrentUser _currentUser;
    private readonly IStringLocalizer<SharedResource> _l;

    public AlignModel(AppDbContext db, DocumentAlignService align, TaskTriggerQueue tasks, CurrentUser currentUser, IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _align = align;
        _tasks = tasks;
        _currentUser = currentUser;
        _l = l;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    /// <summary>Also move files MatPaper only found where they lay (storage search).</summary>
    [BindProperty(SupportsGet = true)]
    public bool Found { get; set; }

    public AlignPlan Plan { get; private set; } = default!;
    public TaskRunsPanel Runs { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var plan = await _align.PlanAsync(Id, Found, ct);
        if (plan is null)
        {
            return NotFound();
        }

        Plan = plan;
        ViewData["Breadcrumb"] = _l["System / Storage locations / Align to template"].Value;
        ViewData["Title"] = plan.Location.Name;

        Runs = new TaskRunsPanel(
            TaskRunKind.Align, Id,
            $"/System/StorageLocations/Align?id={Id}&handler=Start&found={(Found ? "true" : "false")}",
            _l["Align now"].Value,
            plan.Total == 0 ? _l["Everything lies where the template puts it."].Value : _l["{0} file(s) would move.", plan.Total].Value,
            null,
            await TaskRunsPanel.LoadRunsAsync(_db, TaskRunKind.Align, Id, ct));
        return Page();
    }

    public async Task<IActionResult> OnPostStartAsync(CancellationToken ct)
    {
        var exists = await _db.StorageLocations.FindAsync(new object[] { Id }, ct);
        if (exists is null)
        {
            return NotFound();
        }

        if (!_tasks.Enqueue(TaskRunKind.Align, Id, _currentUser.UserId, Found ? "found" : null))
        {
            this.Notify(_l["An alignment of this location is already running."].Value, NoticeKind.Warn);
        }
        else
        {
            this.Notify(_l["Alignment started."].Value);
        }

        return RedirectToPage(new { id = Id, found = Found ? "true" : (string?)null });
    }
}
