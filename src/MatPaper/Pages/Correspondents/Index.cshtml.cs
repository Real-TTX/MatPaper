using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Correspondents;

public class IndexModel : PageModel
{
    private const int PageSize = 20;

    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;

    public IndexModel(AppDbContext db, CurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public sealed record Stat(DateTime? Last, int Count, long? LatestId, string? LatestTitle);

    public IReadOnlyDictionary<long, Stat> Stats { get; private set; } = new Dictionary<long, Stat>();

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name_asc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<Correspondent> Rows { get; private set; } = Array.Empty<Correspondent>();
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; }
    public FilterChipBar Filters { get; private set; } = FilterChipBar.Empty;

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "Correspondents";

        IQueryable<Correspondent> query = _db.Correspondents
            .AsNoTracking()
            .Where(c => c.UpdateState != UpdateState.Deleted);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var pattern = $"%{Search.Trim()}%";
            query = query.Where(c =>
                EF.Functions.ILike(c.Name, pattern) ||
                (c.MatchPattern != null && EF.Functions.ILike(c.MatchPattern, pattern)) ||
                (c.Notes != null && EF.Functions.ILike(c.Notes, pattern)));
        }

        query = Sort == "name_desc"
            ? query.OrderByDescending(c => c.Name)
            : query.OrderBy(c => c.Name);

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

        var pageIds = Rows.Select(c => c.Id).ToList();
        if (pageIds.Count > 0)
        {
            var docs = await _db.Documents
                .AsNoTracking()
                .Where(d => d.UpdateState != UpdateState.Deleted
                    && d.CorrespondentId != null
                    && pageIds.Contains(d.CorrespondentId.Value))
                .AccessibleTo(_currentUser)
                .Select(d => new
                {
                    d.Id,
                    d.Title,
                    CorrespondentId = d.CorrespondentId!.Value,
                    Date = d.DocumentDate ?? d.CreateDate
                })
                .ToListAsync();

            Stats = docs
                .GroupBy(d => d.CorrespondentId)
                .ToDictionary(g => g.Key, g =>
                {
                    var latest = g.OrderByDescending(x => x.Date).ThenByDescending(x => x.Id).First();
                    return new Stat(latest.Date, g.Count(), latest.Id, latest.Title);
                });
        }

        var chips = new List<FilterChip>();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            chips.Add(new FilterChip($"Search: {Search}", FilterUrl.Without(Request, "Search")));
        }
        Filters = new FilterChipBar(chips, FilterUrl.ClearAll(Request));
    }
}
