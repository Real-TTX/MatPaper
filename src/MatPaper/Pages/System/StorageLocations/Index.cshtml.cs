using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.System.StorageLocations;

/// <summary>
/// Lists the storage locations and starts a storage search on one of them. Searching walks a
/// whole share, so the page only queues the request; <see cref="TaskSchedulerService"/> runs
/// it like any other task. The outcome lands on the location itself (last run, count, error)
/// and in the task history.
/// </summary>
public class IndexModel : PageModel
{
    private const int PageSize = 20;

    private readonly AppDbContext _db;
    private readonly TaskTriggerQueue _tasks;
    private readonly CurrentUser _currentUser;
    private readonly IStringLocalizer<SharedResource> _l;

    public IndexModel(
        AppDbContext db,
        TaskTriggerQueue tasks,
        CurrentUser currentUser,
        IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _tasks = tasks;
        _currentUser = currentUser;
        _l = l;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    /// <summary>"all", "Local" or "Smb".</summary>
    [BindProperty(SupportsGet = true)]
    public string Kind { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name_asc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<StorageLocation> Rows { get; private set; } = Array.Empty<StorageLocation>();
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; }

    /// <summary>Ids of the locations a search is queued or running for.</summary>
    public HashSet<long> Busy { get; private set; } = new();

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "System / Storage locations";

        IQueryable<StorageLocation> query = _db.StorageLocations
            .AsNoTracking()
            .Include(s => s.Connection)
            .Where(s => s.UpdateState != UpdateState.Deleted);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var pattern = $"%{Search.Trim()}%";
            query = query.Where(s =>
                EF.Functions.ILike(s.Name, pattern) ||
                EF.Functions.ILike(s.RootPath, pattern));
        }

        if (Enum.TryParse<StorageKind>(Kind, true, out var kind) && Enum.IsDefined(kind))
        {
            query = query.Where(s => s.Kind == kind);
        }

        query = Sort == "name_desc"
            ? query.OrderByDescending(s => s.Name)
            : query.OrderBy(s => s.Name);

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

        Rows = await query
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();

        Busy = Rows.Where(r => _tasks.IsBusy(TaskRunKind.Scan, r.Id)).Select(r => r.Id).ToHashSet();
    }

    /// <summary>Queues a storage search for one location.</summary>
    public async Task<IActionResult> OnPostScanAsync(long id, CancellationToken ct)
    {
        var location = await _db.StorageLocations
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.UpdateState != UpdateState.Deleted, ct);

        if (location is null)
        {
            this.Notify(_l["Storage location not found."].Value, NoticeKind.Danger);
            return RedirectBack();
        }

        if (!_tasks.Enqueue(TaskRunKind.Scan, location.Id, _currentUser.UserId))
        {
            this.Notify(_l["A search for this location is already running."].Value, NoticeKind.Warn);
            return RedirectBack();
        }

        this.Notify(_l["Search started for \"{0}\".", location.Name].Value);
        return RedirectBack();
    }

    /// <summary>Queues a storage search for every active location.</summary>
    public async Task<IActionResult> OnPostScanAllAsync(CancellationToken ct)
    {
        var locations = await _db.StorageLocations
            .AsNoTracking()
            .Where(s => s.UpdateState != UpdateState.Deleted)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var started = locations.Count(id => _tasks.Enqueue(TaskRunKind.Scan, id, _currentUser.UserId));

        this.Notify(_l["Search started for {0} storage location(s).", started].Value);
        return RedirectBack();
    }

    private IActionResult RedirectBack()
        => RedirectToPage(new { Search, Kind = Kind == "all" ? null : Kind, Sort, PageNumber = PageNumber > 1 ? PageNumber : (int?)null });
}
