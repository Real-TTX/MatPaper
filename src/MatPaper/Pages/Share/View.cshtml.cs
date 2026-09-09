using Microsoft.AspNetCore.Mvc.RazorPages;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Share;

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

    public async Task OnGetAsync(Guid token)
    {
        Token = token;
        Document = await _shareLinks.ResolveAsync(token, HttpContext.RequestAborted);

        if (Document is null)
        {
            return;
        }

        var extension = Path.GetExtension(Document.OriginalFileName).ToLowerInvariant();
        IsPdf = extension == ".pdf";
        IsImage = extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff";
    }
}
