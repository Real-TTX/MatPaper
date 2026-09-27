using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Share;

/// <summary>
/// The public page behind a share link. An unknown, revoked or expired token is a 404 —
/// the error page says so; the view itself only ever renders a real document.
/// </summary>
public class ViewModel : PageModel
{
    private readonly ShareLinkService _shareLinks;

    public ViewModel(ShareLinkService shareLinks)
    {
        _shareLinks = shareLinks;
    }

    public Guid Token { get; private set; }

    public Document? Document { get; private set; }

    public bool IsImage { get; private set; }

    public bool IsPdf { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid token)
    {
        Token = token;
        Document = await _shareLinks.ResolveAsync(token, HttpContext.RequestAborted);

        if (Document is null)
        {
            return NotFound();
        }

        var extension = Path.GetExtension(Document.OriginalFileName).ToLowerInvariant();
        IsPdf = extension == ".pdf";
        IsImage = extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff";

        return Page();
    }
}
