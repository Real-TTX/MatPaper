using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Documents;

// Serves a document's file INLINE (no download disposition) so the browser renders it
// inside an <iframe>/<img> for preview. Auth-protected (under /Documents). Works for
// staged (inbox), local and SMB-stored files; SMB files are streamed on demand.
public class ViewModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DocumentStorageService _storage;
    private readonly CurrentUser _currentUser;
    private readonly ILogger<ViewModel> _logger;

    public ViewModel(AppDbContext db, DocumentStorageService storage, CurrentUser currentUser, ILogger<ViewModel> logger)
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

            // No download name => Content-Disposition: inline => browser renders it.
            return new PhysicalFileResult(localPath, contentType) { EnableRangeProcessing = true };
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
            _logger.LogWarning(ex, "Serving document {DocumentId} from its storage location failed.", document.Id);
            return StatusCode(StatusCodes.Status502BadGateway, "The storage location is not reachable.");
        }

        return new FileStreamResult(stream, contentType) { EnableRangeProcessing = true };
    }
}
