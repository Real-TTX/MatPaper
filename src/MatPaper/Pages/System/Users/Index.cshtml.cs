using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;

namespace MatPaper.Pages.System.Users;

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
    public string Role { get; set; } = "All";

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "username_asc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<User> Users { get; private set; } = Array.Empty<User>();
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; }
    public int PageSizeValue => PageSize;

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "System / Users";

        IQueryable<User> query = _db.Users
            .AsNoTracking()
            .Include(u => u.Role);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            var pattern = $"%{term}%";
            query = query.Where(u =>
                EF.Functions.ILike(u.Username, pattern) ||
                EF.Functions.ILike(u.DisplayName, pattern) ||
                (u.Email != null && EF.Functions.ILike(u.Email, pattern)));
        }

        if (!string.IsNullOrWhiteSpace(Role) && !string.Equals(Role, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(u => u.Role != null && u.Role.Name == Role);
        }

        query = Sort == "username_desc"
            ? query.OrderByDescending(u => u.Username)
            : query.OrderBy(u => u.Username);

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

        Users = await query
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();
    }
}
