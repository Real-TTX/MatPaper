using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Inbox;

/// <summary>
/// The per-user review inbox ("Posteingang"): documents the current user owns
/// that still carry auto-suggested metadata awaiting confirmation. The user can
/// accept the suggestions (single or all) or open a document to change them.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;

    public IndexModel(AppDbContext db, CurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public IReadOnlyList<InboxRow> Rows { get; private set; } = Array.Empty<InboxRow>();

    public record InboxRow(
        long Id,
        Guid Token,
        string Title,
        string? ThumbnailPath,
        string? TypeName,
        string? CorrespondentName,
        DateTime? DocumentDate,
        IReadOnlyList<string> TagNames,
        OcrState OcrState,
        DateTime AddedDate);

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "Inbox";
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostConfirmAsync(long id)
    {
        var uid = _currentUser.UserId;
        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == id && d.OwnerId == uid && d.UpdateState != UpdateState.Deleted);

        if (doc != null)
        {
            doc.ReviewState = ReviewState.Reviewed;
            doc.UpdateDate = DateTime.UtcNow;
            doc.UpdateUserId = uid;
            await _db.SaveChangesAsync();
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostConfirmAllAsync()
    {
        var uid = _currentUser.UserId;
        var now = DateTime.UtcNow;

        var pending = await _db.Documents
            .Where(d => d.OwnerId == uid && d.ReviewState == ReviewState.Pending && d.UpdateState != UpdateState.Deleted)
            .ToListAsync();

        foreach (var doc in pending)
        {
            doc.ReviewState = ReviewState.Reviewed;
            doc.UpdateDate = now;
            doc.UpdateUserId = uid;
        }

        if (pending.Count > 0)
        {
            await _db.SaveChangesAsync();
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        var uid = _currentUser.UserId;

        Rows = await _db.Documents
            .AsNoTracking()
            .Where(d => d.OwnerId == uid && d.ReviewState == ReviewState.Pending && d.UpdateState != UpdateState.Deleted)
            .OrderByDescending(d => d.CreateDate)
            .Select(d => new InboxRow(
                d.Id,
                d.Token,
                d.Title,
                d.ThumbnailPath,
                d.DocumentType != null ? d.DocumentType.Name : null,
                d.Correspondent != null ? d.Correspondent.Name : null,
                d.DocumentDate,
                d.DocumentTags.Select(t => t.Tag!.Name).ToList(),
                d.OcrState,
                d.CreateDate))
            .ToListAsync();
    }
}
