using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Documents;

/// <summary>
/// "Share" in the page viewer on a phone: when the browser cannot hand the file itself to another app, this gives
/// a link that works for a few days. An active link with enough time left is reused instead of piling up new ones.
/// Only the owner (or an administrator) may publish a document, like on the document page.
/// </summary>
public class ShareLinkModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ShareLinkService _shareLinks;
    private readonly CurrentUser _currentUser;

    public ShareLinkModel(AppDbContext db, ShareLinkService shareLinks, CurrentUser currentUser)
    {
        _db = db;
        _shareLinks = shareLinks;
        _currentUser = currentUser;
    }

    public IActionResult OnGet() => NotFound();

    public async Task<IActionResult> OnPostAsync(Guid token, int days, CancellationToken ct)
    {
        var document = await _db.Documents.AsNoTracking()
            .AccessibleTo(_currentUser)
            .FirstOrDefaultAsync(d => d.Token == token && d.UpdateState != UpdateState.Deleted, ct);
        if (document is null)
        {
            return new JsonResult(new { ok = false }) { StatusCode = StatusCodes.Status404NotFound };
        }

        if (!DocumentAccess.IsOwnerOrAdmin(document, _currentUser.UserId, _currentUser.IsAdmin))
        {
            return new JsonResult(new { ok = false, forbidden = true }) { StatusCode = StatusCodes.Status403Forbidden };
        }

        days = days <= 0 ? 7 : Math.Min(days, 90);
        var now = DateTime.UtcNow;
        var existing = (await _shareLinks.ListForDocumentAsync(document.Id, ct))
            .FirstOrDefault(l => l.ExpiresAt != null && l.ExpiresAt > now.AddDays(1));
        var link = existing ?? await _shareLinks.CreateAsync(document.Id, now.AddDays(days), ct);

        var url = $"{Request.Scheme}://{Request.Host}/Share/{link.Token}";
        var left = Math.Max(1, (int)Math.Round(((link.ExpiresAt ?? now.AddDays(days)) - now).TotalDays));
        return new JsonResult(new { ok = true, url, days = left });
    }
}
