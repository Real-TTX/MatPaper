using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;

namespace MatPaper.Pages.System.Credentials;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db)
    {
        _db = db;
    }

    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    public IReadOnlyList<Credential> Rows { get; private set; } = Array.Empty<Credential>();

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "System / Credentials";

        IQueryable<Credential> query = _db.Credentials
            .AsNoTracking()
            .Where(c => c.UpdateState != UpdateState.Deleted);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var pattern = $"%{Search.Trim()}%";
            query = query.Where(c => EF.Functions.ILike(c.Name, pattern) || EF.Functions.ILike(c.Username, pattern));
        }

        Rows = await query.OrderBy(c => c.Name).ToListAsync();
    }
}
