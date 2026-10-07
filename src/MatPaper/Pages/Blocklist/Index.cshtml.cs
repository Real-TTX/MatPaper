using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Blocklist;

public class IndexModel : PageModel
{
    private const int Limit = 300;

    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;

    public IndexModel(AppDbContext db, CurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    public IReadOnlyList<BlockedDocument> Rows { get; private set; } = Array.Empty<BlockedDocument>();
    public bool Truncated { get; private set; }

    public async Task OnGetAsync()
    {
        var query = Mine();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = $"%{Search.Trim()}%";
            query = query.Where(b => EF.Functions.ILike(b.Name, term) || (b.OriginalFileName != null && EF.Functions.ILike(b.OriginalFileName, term)));
        }

        var rows = await query.OrderByDescending(b => b.CreateDate).Take(Limit + 1).ToListAsync();
        Truncated = rows.Count > Limit;
        Rows = rows.Take(Limit).ToList();
    }

    public async Task<IActionResult> OnPostReleaseAsync(long id, string? search)
    {
        var entry = await Mine().FirstOrDefaultAsync(b => b.Id == id);
        if (entry is not null)
        {
            _db.BlockedDocuments.Remove(entry);
            await _db.SaveChangesAsync();
        }

        return RedirectToPage(new { Search = search });
    }

    /// <summary>Everyone sees their own entries; an admin sees all.</summary>
    private IQueryable<BlockedDocument> Mine()
        => _currentUser.IsAdmin
            ? _db.BlockedDocuments
            : _db.BlockedDocuments.Where(b => b.OwnerId == _currentUser.UserId);
}
