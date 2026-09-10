using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Documents;

public class IndexModel : PageModel
{
    private const int PageSize = 24;

    private readonly AppDbContext _db;
    private readonly DocumentAnalysisService _analysis;
    private readonly CurrentUser _currentUser;

    public IndexModel(AppDbContext db, DocumentAnalysisService analysis, CurrentUser currentUser)
    {
        _db = db;
        _analysis = analysis;
        _currentUser = currentUser;
    }

    public async Task<IActionResult> OnPostReanalyzeAllAsync(bool overwrite)
    {
        var documents = await _db.Documents
            .Include(d => d.StorageLocation)
            .Where(d => d.UpdateState != UpdateState.Deleted)
            .ToListAsync(HttpContext.RequestAborted);

        var options = new AnalysisOptions { Overwrite = overwrite, CreateMissingCorrespondents = true };
        var changed = 0;

        foreach (var document in documents)
        {
            var result = await _analysis.AnalyzeAsync(document, options, _currentUser.UserId, HttpContext.RequestAborted);
            if (result.AnythingChanged)
            {
                document.UpdateState = UpdateState.Updated;
                document.UpdateDate = DateTime.UtcNow;
                document.UpdateUserId = _currentUser.UserId;
                await _db.SaveChangesAsync(HttpContext.RequestAborted);
                changed++;
            }
        }

        TempData["UploadSummary"] = $"Analyzed {documents.Count} document(s); {changed} updated.";
        return RedirectToPage("Index");
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public long? CorrespondentId { get; set; }

    [BindProperty(SupportsGet = true)]
    public long? DocumentTypeId { get; set; }

    [BindProperty(SupportsGet = true)]
    public long? TagId { get; set; }

    [BindProperty(SupportsGet = true)]
    public long? ProjectId { get; set; }

    [BindProperty(SupportsGet = true)]
    public long? StorageLocationId { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? DateFrom { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? DateTo { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "newest";

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

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "Documents";

        await LoadFilterOptionsAsync();

        IQueryable<Document> query = _db.Documents
            .AsNoTracking()
            .Include(d => d.Correspondent)
            .Include(d => d.DocumentType)
            .Where(d => d.UpdateState != UpdateState.Deleted);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            query = query.Where(d =>
                d.SearchVector != null &&
                d.SearchVector.Matches(EF.Functions.WebSearchToTsQuery("german", term)));
        }

        if (CorrespondentId.HasValue)
        {
            query = query.Where(d => d.CorrespondentId == CorrespondentId.Value);
        }

        if (DocumentTypeId.HasValue)
        {
            query = query.Where(d => d.DocumentTypeId == DocumentTypeId.Value);
        }

        if (ProjectId.HasValue)
        {
            query = query.Where(d => d.ProjectId == ProjectId.Value);
        }

        if (StorageLocationId.HasValue)
        {
            query = query.Where(d => d.StorageLocationId == StorageLocationId.Value);
        }

        if (TagId.HasValue)
        {
            query = query.Where(d => d.DocumentTags.Any(dt => dt.TagId == TagId.Value));
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
            "oldest" => query.OrderBy(d => d.DocumentDate ?? d.CreateDate).ThenBy(d => d.Id),
            "title" => query.OrderBy(d => d.Title).ThenBy(d => d.Id),
            _ => query.OrderByDescending(d => d.DocumentDate ?? d.CreateDate).ThenByDescending(d => d.Id),
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
            .OrderBy(p => p.Name)
            .ToListAsync();

        StorageLocations = await _db.StorageLocations.AsNoTracking()
            .Where(s => s.UpdateState != UpdateState.Deleted)
            .OrderBy(s => s.Name)
            .ToListAsync();
    }
}
