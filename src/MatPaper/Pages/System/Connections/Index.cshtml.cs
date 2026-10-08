using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;

namespace MatPaper.Pages.System.Connections;

/// <summary>
/// Lists the reusable connections (SMB shares, mailboxes, cloud drives). Replaces the old
/// credentials list: a connection carries the whole endpoint plus its sign-in.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db)
    {
        _db = db;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    /// <summary>"all" or a <see cref="ConnectionKind"/> name.</summary>
    [BindProperty(SupportsGet = true)]
    public string Kind { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name_asc";

    public IReadOnlyList<Connection> Rows { get; private set; } = Array.Empty<Connection>();
    public int TotalCount { get; private set; }

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "System / Connections";

        IQueryable<Connection> query = _db.Connections
            .AsNoTracking()
            .Where(c => c.UpdateState != UpdateState.Deleted);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var pattern = $"%{Search.Trim()}%";
            query = query.Where(c => EF.Functions.ILike(c.Name, pattern)
                || (c.Username != null && EF.Functions.ILike(c.Username, pattern)));
        }

        if (Enum.TryParse<ConnectionKind>(Kind, true, out var kind) && Enum.IsDefined(kind))
        {
            query = query.Where(c => c.Kind == kind);
        }

        query = Sort == "name_desc" ? query.OrderByDescending(c => c.Name) : query.OrderBy(c => c.Name);

        Rows = await query.ToListAsync();
        TotalCount = Rows.Count;
    }
}
