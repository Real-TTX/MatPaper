using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.Documents;

public class IndexModel : PageModel
{
    private const int PageSize = 24;

    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;
    private readonly IStringLocalizer<SharedResource> _l;
    private readonly Fmt _fmt;

    public IndexModel(AppDbContext db, CurrentUser currentUser, IStringLocalizer<SharedResource> l, Fmt fmt)
    {
        _db = db;
        _currentUser = currentUser;
        _l = l;
        _fmt = fmt;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public long[] CorrespondentIds { get; set; } = Array.Empty<long>();

    [BindProperty(SupportsGet = true)]
    public long[] DocumentTypeIds { get; set; } = Array.Empty<long>();

    [BindProperty(SupportsGet = true)]
    public long[] TagIds { get; set; } = Array.Empty<long>();

    [BindProperty(SupportsGet = true)]
    public long[] ProjectIds { get; set; } = Array.Empty<long>();

    [BindProperty(SupportsGet = true)]
    public long[] StorageLocationIds { get; set; } = Array.Empty<long>();

    [BindProperty(SupportsGet = true)]
    public DateTime? DateFrom { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? DateTo { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "added";

    /// <summary>Ownership scope: all | mine | shared | common.</summary>
    [BindProperty(SupportsGet = true)]
    public string Scope { get; set; } = "all";

    /// <summary>Review state: all | pending | reviewed.</summary>
    [BindProperty(SupportsGet = true)]
    public string Review { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<Document> Rows { get; private set; } = Array.Empty<Document>();
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; }

    public IReadOnlyList<Correspondent> Correspondents { get; private set; } = Array.Empty<Correspondent>();
    public IReadOnlyList<DocumentType> DocumentTypes { get; private set; } = Array.Empty<DocumentType>();
    public IReadOnlyList<Tag> Tags { get; private set; } = Array.Empty<Tag>();
    public IReadOnlyList<Project> Projects { get; private set; } = Array.Empty<Project>();
    public IReadOnlyList<StorageLocation> StorageLocations { get; private set; } = Array.Empty<StorageLocation>();

    public FilterChipBar Filters { get; private set; } = FilterChipBar.Empty;

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "Documents";

        await LoadFilterOptionsAsync();

        IQueryable<Document> query = _db.Documents
            .AsNoTracking()
            .Include(d => d.Correspondent)
            .Include(d => d.DocumentType)
            .Include(d => d.Project)
            .Include(d => d.DocumentTags).ThenInclude(dt => dt.Tag)
            .Where(d => d.UpdateState != UpdateState.Deleted)
            .AccessibleTo(_currentUser);

        var userId = _currentUser.UserId;
        query = Scope switch
        {
            "mine" => query.Where(d => d.OwnerId == userId),
            "shared" => query.Where(d => d.OwnerId != userId
                && d.Shares.Any(s => s.UserId == userId && s.UpdateState != UpdateState.Deleted)),
            "common" => query.Where(d => d.IsCommon),
            _ => query,
        };

        query = Review switch
        {
            "pending" => query.Where(d => d.ReviewState == ReviewState.Pending),
            "reviewed" => query.Where(d => d.ReviewState == ReviewState.Reviewed),
            _ => query,
        };

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            query = query.Where(d =>
                d.SearchVector != null &&
                d.SearchVector.Matches(EF.Functions.WebSearchToTsQuery("german", term)));
        }

        if (CorrespondentIds.Length > 0)
        {
            query = query.Where(d => d.CorrespondentId.HasValue && CorrespondentIds.Contains(d.CorrespondentId.Value));
        }

        if (DocumentTypeIds.Length > 0)
        {
            query = query.Where(d => d.DocumentTypeId.HasValue && DocumentTypeIds.Contains(d.DocumentTypeId.Value));
        }

        if (ProjectIds.Length > 0)
        {
            query = query.Where(d => d.ProjectId.HasValue && ProjectIds.Contains(d.ProjectId.Value));
        }

        if (StorageLocationIds.Length > 0)
        {
            query = query.Where(d => d.StorageLocationId.HasValue && StorageLocationIds.Contains(d.StorageLocationId.Value));
        }

        if (TagIds.Length > 0)
        {
            query = query.Where(d => d.DocumentTags.Any(dt => TagIds.Contains(dt.TagId)));
        }

        if (DateFrom.HasValue)
        {
            var from = DateTime.SpecifyKind(DateFrom.Value.Date, DateTimeKind.Utc);
            query = query.Where(d => (d.DocumentDate ?? d.CreateDate) >= from);
        }

        if (DateTo.HasValue)
        {
            var to = DateTime.SpecifyKind(DateTo.Value.Date.AddDays(1), DateTimeKind.Utc);
            query = query.Where(d => (d.DocumentDate ?? d.CreateDate) < to);
        }

        query = Sort switch
        {
            // "added" (the default) keeps freshly imported documents on top even when
            // their document date is years old.
            "newest" => query.OrderByDescending(d => d.DocumentDate ?? d.CreateDate).ThenByDescending(d => d.Id),
            "oldest" => query.OrderBy(d => d.DocumentDate ?? d.CreateDate).ThenBy(d => d.Id),
            "title" => query.OrderBy(d => d.Title).ThenBy(d => d.Id),
            _ => query.OrderByDescending(d => d.CreateDate).ThenByDescending(d => d.Id),
        };

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

        BuildActiveFilters();
    }

    private void BuildActiveFilters()
    {
        var req = Request;
        var chips = new List<FilterChip>();

        if (!string.IsNullOrWhiteSpace(Search))
        {
            chips.Add(new FilterChip($"{_l["Search"]}: {Search}", FilterUrl.Without(req, "Search")));
        }

        var scopeLabel = Scope switch
        {
            "mine" => "Mine",
            "shared" => "Shared with me",
            "common" => "Common area",
            _ => null
        };
        if (scopeLabel != null)
        {
            chips.Add(new FilterChip($"{_l["Scope"]}: {scopeLabel}", FilterUrl.Without(req, "Scope")));
        }

        var reviewLabel = Review switch
        {
            "pending" => "Needs review",
            "reviewed" => "Reviewed",
            _ => null
        };
        if (reviewLabel != null)
        {
            chips.Add(new FilterChip($"{_l["Review"]}: {reviewLabel}", FilterUrl.Without(req, "Review")));
        }

        foreach (var id in CorrespondentIds)
        {
            var name = Correspondents.FirstOrDefault(c => c.Id == id)?.Name ?? id.ToString();
            chips.Add(new FilterChip($"{_l["Correspondent"]}: {name}", FilterUrl.WithoutValue(req, "CorrespondentIds", id.ToString())));
        }
        foreach (var id in DocumentTypeIds)
        {
            var name = DocumentTypes.FirstOrDefault(t => t.Id == id)?.Name ?? id.ToString();
            chips.Add(new FilterChip($"{_l["Type"]}: {name}", FilterUrl.WithoutValue(req, "DocumentTypeIds", id.ToString())));
        }
        foreach (var id in TagIds)
        {
            var name = Tags.FirstOrDefault(t => t.Id == id)?.Name ?? id.ToString();
            chips.Add(new FilterChip($"{_l["Tag"]}: {name}", FilterUrl.WithoutValue(req, "TagIds", id.ToString())));
        }
        foreach (var id in ProjectIds)
        {
            var name = Projects.FirstOrDefault(p => p.Id == id)?.Name ?? id.ToString();
            chips.Add(new FilterChip($"{_l["Project"]}: {name}", FilterUrl.WithoutValue(req, "ProjectIds", id.ToString())));
        }
        foreach (var id in StorageLocationIds)
        {
            var name = StorageLocations.FirstOrDefault(s => s.Id == id)?.Name ?? id.ToString();
            chips.Add(new FilterChip($"{_l["Location"]}: {name}", FilterUrl.WithoutValue(req, "StorageLocationIds", id.ToString())));
        }

        if (DateFrom.HasValue)
        {
            chips.Add(new FilterChip($"{_l["From"]}: {_fmt.Date(DateFrom)}", FilterUrl.Without(req, "DateFrom")));
        }
        if (DateTo.HasValue)
        {
            chips.Add(new FilterChip($"{_l["To"]}: {_fmt.Date(DateTo)}", FilterUrl.Without(req, "DateTo")));
        }

        Filters = new FilterChipBar(chips, FilterUrl.ClearAll(req));
    }

    private async Task LoadFilterOptionsAsync()
    {
        Correspondents = await _db.Correspondents.AsNoTracking()
            .Where(c => c.UpdateState != UpdateState.Deleted)
            .OrderBy(c => c.Name)
            .ToListAsync();

        DocumentTypes = await _db.DocumentTypes.AsNoTracking()
            .Where(t => t.UpdateState != UpdateState.Deleted)
            .OrderBy(t => t.Name)
            .ToListAsync();

        Tags = await _db.Tags.AsNoTracking()
            .Where(t => t.UpdateState != UpdateState.Deleted)
            .OrderBy(t => t.Name)
            .ToListAsync();

        Projects = await _db.Projects.AsNoTracking()
            .Where(p => p.UpdateState != UpdateState.Deleted)
            .AccessibleTo(_currentUser)
            .OrderBy(p => p.Name)
            .ToListAsync();

        StorageLocations = await _db.StorageLocations.AsNoTracking()
            .Where(s => s.UpdateState != UpdateState.Deleted)
            .OrderBy(s => s.Name)
            .ToListAsync();
    }
}
