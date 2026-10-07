using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PDFtoImage;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Documents;

/// <summary>
/// What the phone's page viewer needs to know about a document: how it can be shown.
/// <c>pdf</c> = rendered page by page (see <see cref="PageImageModel"/>), <c>image</c> = the file
/// itself is one picture, <c>other</c> = only download or open in a tab.
/// </summary>
public class PagesModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DocumentStorageService _storage;
    private readonly CurrentUser _currentUser;
    private readonly ILogger<PagesModel> _logger;

    public PagesModel(AppDbContext db, DocumentStorageService storage, CurrentUser currentUser, ILogger<PagesModel> logger)
    {
        _db = db;
        _storage = storage;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<IActionResult> OnGetAsync(Guid token, CancellationToken ct)
    {
        var document = await _db.Documents
            .AsNoTracking()
            .Include(d => d.StorageLocation).ThenInclude(s => s!.Connection)
            .AccessibleTo(_currentUser)
            .FirstOrDefaultAsync(d => d.Token == token && d.UpdateState != UpdateState.Deleted, ct);
        if (document is null)
        {
            return NotFound();
        }

        var extension = Path.GetExtension(document.OriginalFileName ?? string.Empty).ToLowerInvariant();
        if (extension != ".pdf")
        {
            var isImage = extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp";
            return new JsonResult(new { kind = isImage ? "image" : "other", pages = isImage ? 1 : 0 });
        }

        // Known after text recognition; otherwise count from the file.
        if (document.PageCount > 0)
        {
            return new JsonResult(new { kind = "pdf", pages = document.PageCount });
        }

        try
        {
            using var local = await _storage.GetLocalCopyAsync(document, ct);
            var bytes = await global::System.IO.File.ReadAllBytesAsync(local.FilePath, ct);
            return new JsonResult(new { kind = "pdf", pages = Conversion.GetPageCount(bytes) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not count the pages of document {DocumentId}.", document.Id);
            return new JsonResult(new { kind = "other", pages = 0 });
        }
    }
}
