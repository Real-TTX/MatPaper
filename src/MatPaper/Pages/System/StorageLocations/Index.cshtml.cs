using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.System.StorageLocations;

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
    public string Sort { get; set; } = "name_asc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<StorageLocation> Rows { get; private set; } = Array.Empty<StorageLocation>();
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; }

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "System / Storage locations";

        IQueryable<StorageLocation> query = _db.StorageLocations
            .AsNoTracking()
            .Where(s => s.UpdateState != UpdateState.Deleted);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var pattern = $"%{Search.Trim()}%";
            query = query.Where(s =>
                EF.Functions.ILike(s.Name, pattern) ||
                EF.Functions.ILike(s.RootPath, pattern));
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
    }
}
