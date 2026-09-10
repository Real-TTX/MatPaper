using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Documents;

public class DownloadModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DocumentStorageService _storage;
    private readonly CurrentUser _currentUser;

    public DownloadModel(AppDbContext db, DocumentStorageService storage, CurrentUser currentUser)
    {
        _db = db;
        _storage = storage;
        _currentUser = currentUser;
    }

    public async Task<IActionResult> OnGetAsync(Guid token)
    {
        var document = await _db.Documents
            .AsNoTracking()
            .Include(d => d.StorageLocation)
            .AccessibleTo(_currentUser)
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

        var contentType = GetContentType(document.OriginalFileName);
        return PhysicalFile(absolutePath, contentType, document.OriginalFileName);
    }

    private static string GetContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
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
}
