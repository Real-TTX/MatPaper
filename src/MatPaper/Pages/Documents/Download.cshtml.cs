using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Documents;

/// <summary>Serves a document's file as an attachment (staged, local or SMB-stored).</summary>
public class DownloadModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DocumentStorageService _storage;
    private readonly CurrentUser _currentUser;
    private readonly ILogger<DownloadModel> _logger;

    public DownloadModel(AppDbContext db, DocumentStorageService storage, CurrentUser currentUser, ILogger<DownloadModel> logger)
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
            .Include(d => d.StorageLocation).ThenInclude(s => s!.Credential)
            .AccessibleTo(_currentUser)
            .FirstOrDefaultAsync(d => d.Token == token && d.UpdateState != UpdateState.Deleted, ct);

        if (document == null)
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
                FileDownloadName = document.OriginalFileName,
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
            _logger.LogWarning(ex, "Downloading document {DocumentId} from its storage location failed.", document.Id);
            return StatusCode(StatusCodes.Status502BadGateway, "The storage location is not reachable.");
        }

        return new FileStreamResult(stream, contentType)
        {
            FileDownloadName = document.OriginalFileName,
            EnableRangeProcessing = true
        };
    }
}
