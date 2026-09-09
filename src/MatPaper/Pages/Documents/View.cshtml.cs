using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Documents;

// Serves a document's file INLINE (no download disposition) so the browser renders it
// inside an <iframe>/<img> for preview. Auth-protected (under /Documents).
public class ViewModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DocumentStorageService _storage;

    public ViewModel(AppDbContext db, DocumentStorageService storage)
    {
        _db = db;
        _storage = storage;
    }

    public async Task<IActionResult> OnGetAsync(Guid token)
    {
        var document = await _db.Documents
            .AsNoTracking()
            .Include(d => d.StorageLocation)
            .FirstOrDefaultAsync(d => d.Token == token && d.UpdateState != UpdateState.Deleted);

        if (document?.StorageLocation == null)
        {
            return NotFound();
        }

        var absolutePath = _storage.GetAbsolutePath(document.StorageLocation, document.RelativePath);
        if (!global::System.IO.File.Exists(absolutePath))
        {
            return NotFound();
        }

        // No download name => Content-Disposition: inline => browser renders it.
        return PhysicalFile(absolutePath, GetContentType(document.OriginalFileName));
    }

    private static string GetContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".tif" or ".tiff" => "image/tiff",
        _ => "application/octet-stream",
    };
}
