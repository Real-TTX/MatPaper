using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;

namespace MatPaper.Pages.Tags;

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

    public IReadOnlyList<Tag> Rows { get; private set; } = Array.Empty<Tag>();
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; }

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "Tags";

        IQueryable<Tag> query = _db.Tags
            .AsNoTracking()
            .Where(t => t.UpdateState != UpdateState.Deleted);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var pattern = $"%{Search.Trim()}%";
            query = query.Where(t => EF.Functions.ILike(t.Name, pattern));
        }

        query = Sort == "name_desc"
            ? query.OrderByDescending(t => t.Name)
            : query.OrderBy(t => t.Name);

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
