using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MatPaper.Services;

namespace MatPaper.Pages.Share;

public class FileModel : PageModel
{
    private readonly ShareLinkService _shareLinks;
    private readonly DocumentStorageService _storage;

    public FileModel(ShareLinkService shareLinks, DocumentStorageService storage)
    {
        _shareLinks = shareLinks;
        _storage = storage;
    }

    public async Task<IActionResult> OnGetAsync(Guid token, bool download = false)
    {
        var document = await _shareLinks.ResolveAsync(token, HttpContext.RequestAborted);
        if (document?.StorageLocation is null)
        {
            return NotFound();
        }

        var absolutePath = _storage.GetAbsolutePath(document.StorageLocation, document.RelativePath);
        if (!global::System.IO.File.Exists(absolutePath))
        {
            return NotFound();
        }

        var contentType = GetContentType(document.OriginalFileName);

        return download
            ? PhysicalFile(absolutePath, contentType, document.OriginalFileName)
            : PhysicalFile(absolutePath, contentType);
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
