using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MatPaper.Services;

namespace MatPaper.Pages.Share;

/// <summary>Serves a shared document's file (anonymous, token-gated) from wherever it lives.</summary>
public class FileModel : PageModel
{
    private readonly ShareLinkService _shareLinks;
    private readonly DocumentStorageService _storage;
    private readonly ILogger<FileModel> _logger;

    public FileModel(ShareLinkService shareLinks, DocumentStorageService storage, ILogger<FileModel> logger)
    {
        _shareLinks = shareLinks;
        _storage = storage;
        _logger = logger;
    }

    public async Task<IActionResult> OnGetAsync(Guid token, bool download = false)
    {
        var ct = HttpContext.RequestAborted;

        var document = await _shareLinks.ResolveAsync(token, ct);
        if (document is null)
        {
            return NotFound();
        }

        var contentType = DocumentContentType.FromFileName(document.OriginalFileName);

        var localPath = _storage.TryGetDocumentLocalPath(document);
        if (localPath != null)
        {
            if (!global::System.IO.File.Exists(localPath))
            {
                return NotFound();
            }

            return new PhysicalFileResult(localPath, contentType)
            {
                FileDownloadName = download ? document.OriginalFileName : null,
                EnableRangeProcessing = true
            };
        }

        Stream stream;
        try
        {
            stream = await _storage.OpenDocumentReadAsync(document, ct);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Serving shared document {DocumentId} from its storage location failed.", document.Id);
            return StatusCode(StatusCodes.Status502BadGateway, "The storage location is not reachable.");
        }

        return new FileStreamResult(stream, contentType)
        {
            FileDownloadName = download ? document.OriginalFileName : null,
            EnableRangeProcessing = true
        };
    }
}
