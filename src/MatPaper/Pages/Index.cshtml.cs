using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;
    private readonly InboxService _inbox;

    public IndexModel(AppDbContext db, CurrentUser currentUser, InboxService inbox)
    {
        _db = db;
        _currentUser = currentUser;
        _inbox = inbox;
    }

    public int DocumentCount { get; private set; }
    public int PendingReviewCount { get; private set; }
    public IReadOnlyList<RecentDoc> Recent { get; private set; } = Array.Empty<RecentDoc>();

    public record RecentDoc(long Id, Guid Token, string Title, string? TypeName, string? CorrespondentName, DateTime Date, string? ThumbnailPath);

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "Dashboard";

        var documents = _db.Documents
            .Where(d => d.UpdateState != UpdateState.Deleted)
            .AccessibleTo(_currentUser)
            .InArchive();

        DocumentCount = await documents.CountAsync();
        PendingReviewCount = await _inbox.CountPendingAsync();

        Recent = await documents
            .AsNoTracking()
            .OrderByDescending(d => d.CreateDate)
            .Take(8)
            .Select(d => new RecentDoc(
                d.Id,
                d.Token,
                d.Title,
                d.DocumentType != null ? d.DocumentType.Name : null,
                d.Correspondent != null ? d.Correspondent.Name : null,
                d.DocumentDate ?? d.CreateDate,
                d.ThumbnailPath))
            .ToListAsync();
    }
}
