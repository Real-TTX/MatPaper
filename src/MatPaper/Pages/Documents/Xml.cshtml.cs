using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Documents;

/// <summary>Downloads the original e-invoice XML stored next to an imported XRechnung's PDF.</summary>
public class XmlModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DocumentStorageService _storage;
    private readonly CurrentUser _currentUser;

    public XmlModel(AppDbContext db, DocumentStorageService storage, CurrentUser currentUser)
    {
        _db = db;
        _storage = storage;
        _currentUser = currentUser;
    }

    public async Task<IActionResult> OnGetAsync(Guid token, CancellationToken ct)
    {
        var document = await _db.Documents
            .AsNoTracking()
            .Include(d => d.StorageLocation).ThenInclude(s => s!.Connection)
            .AccessibleTo(_currentUser)
            .FirstOrDefaultAsync(d => d.Token == token && d.UpdateState != UpdateState.Deleted && d.HasEInvoiceXml, ct);

        if (document is null)
        {
            return NotFound();
        }

        byte[]? xml;
        try
        {
            xml = document.IsStaged
                ? await _storage.ReadStagedCompanionAsync(document.RelativePath, DocumentStorageService.XmlCompanion, ct)
                : document.StorageLocation is null
                    ? null
                    : await _storage.ReadCompanionAsync(document.StorageLocation, document.RelativePath, DocumentStorageService.XmlCompanion, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, "The storage location is not reachable.");
        }

        if (xml is null)
        {
            return NotFound();
        }

        var name = Path.GetFileNameWithoutExtension(document.OriginalFileName) + ".xml";
        return File(xml, "application/xml", name);
    }
}
