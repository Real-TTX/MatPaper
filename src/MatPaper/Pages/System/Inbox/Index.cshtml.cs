using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.System.Inbox;

public class IndexModel : PageModel
{
    private const int PageSize = 50;

    private readonly StorageScanService _scan;
    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;

    public IndexModel(StorageScanService scan, AppDbContext db, CurrentUser currentUser)
    {
        _scan = scan;
        _db = db;
        _currentUser = currentUser;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public long? LocationId { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    /// <summary>Pre-selected location for the scan panel; seeded from the ?location= query.</summary>
    [BindProperty(SupportsGet = true, Name = "location")]
    public long? ScanLocationId { get; set; }

    public IReadOnlyList<InboxItem> Rows { get; private set; } = Array.Empty<InboxItem>();
    public IReadOnlyList<StorageLocation> StorageLocations { get; private set; } = Array.Empty<StorageLocation>();
    public IReadOnlyList<Correspondent> Correspondents { get; private set; } = Array.Empty<Correspondent>();
    public IReadOnlyList<DocumentType> DocumentTypes { get; private set; } = Array.Empty<DocumentType>();
    public IReadOnlyList<Project> Projects { get; private set; } = Array.Empty<Project>();
    public IReadOnlyList<Tag> Tags { get; private set; } = Array.Empty<Tag>();

    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        ViewData["Breadcrumb"] = "System / Import inbox";

        await LoadOptionsAsync(ct);

        if (ScanLocationId is null || StorageLocations.All(s => s.Id != ScanLocationId))
        {
            var preferred = StorageLocations.FirstOrDefault(s => s.IsDefault) ?? StorageLocations.FirstOrDefault();
            ScanLocationId = preferred?.Id;
        }

        IQueryable<InboxItem> query = _db.InboxItems
            .AsNoTracking()
            .Include(i => i.StorageLocation)
            .Where(i => i.UpdateState != UpdateState.Deleted);

        if (LocationId.HasValue)
        {
            query = query.Where(i => i.StorageLocationId == LocationId.Value);
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var pattern = $"%{Search.Trim()}%";
            query = query.Where(i => EF.Functions.ILike(i.FileName, pattern));
        }

        query = query.OrderBy(i => i.FileName);

        TotalCount = await query.CountAsync(ct);
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
            .ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostScanAsync(long ScanLocationId, CancellationToken ct)
    {
        var result = await _scan.ScanAsync(ScanLocationId, ct);

        TempData["InboxMessage"] = result.LocationMissing
            ? "Storage location not found."
            : $"Scanned {result.Scanned}, added {result.Added} new, {result.Skipped} already known.";

        return RedirectToPage(new { location = ScanLocationId });
    }

    public async Task<IActionResult> OnPostImportAsync(
        long[] SelectedIds,
        long? CorrespondentId,
        long? DocumentTypeId,
        long? ProjectId,
        long[] TagIds,
        bool Refile,
        CancellationToken ct)
    {
        if (SelectedIds is null || SelectedIds.Length == 0)
        {
            TempData["InboxMessage"] = "Select at least one inbox item to import.";
            return RedirectToPage();
        }

        var summary = await _scan.ImportAsync(
            SelectedIds,
            CorrespondentId,
            DocumentTypeId,
            ProjectId,
            TagIds ?? Array.Empty<long>(),
            Refile,
            _currentUser.UserId,
            ct);

        TempData["InboxMessage"] =
            $"Imported {summary.Imported}, {summary.Duplicates} duplicate(s) dismissed, {summary.Errors} error(s).";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDismissAsync(long[] SelectedIds, CancellationToken ct)
    {
        if (SelectedIds is null || SelectedIds.Length == 0)
        {
            TempData["InboxMessage"] = "Select at least one inbox item to dismiss.";
            return RedirectToPage();
        }

        var dismissed = await _scan.DismissAsync(SelectedIds, _currentUser.UserId, ct);
        TempData["InboxMessage"] = $"Dismissed {dismissed} item(s).";
        return RedirectToPage();
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{size:0.#} {units[unit]}";
    }

    private async Task LoadOptionsAsync(CancellationToken ct)
    {
        StorageLocations = await _db.StorageLocations.AsNoTracking()
            .Where(s => s.UpdateState != UpdateState.Deleted)
            .OrderByDescending(s => s.IsDefault)
            .ThenBy(s => s.Name)
            .ToListAsync(ct);

        Correspondents = await _db.Correspondents.AsNoTracking()
            .Where(c => c.UpdateState != UpdateState.Deleted)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        DocumentTypes = await _db.DocumentTypes.AsNoTracking()
            .Where(t => t.UpdateState != UpdateState.Deleted)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        Projects = await _db.Projects.AsNoTracking()
            .Where(p => p.UpdateState != UpdateState.Deleted)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        Tags = await _db.Tags.AsNoTracking()
            .Where(t => t.UpdateState != UpdateState.Deleted)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
    }
}
