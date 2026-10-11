using MatPaper.Configuration;
using MatPaper.Data;
using MatPaper.Services;
using MatPaper.Services.Barcodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Net.Http.Headers;

namespace MatPaper.Pages.Documents;

/// <summary>
/// A copy of a document's PDF with its archive number printed on the first page (a code and the number as text), to
/// print and file: the paper then carries the number the document is found by. The stored document is not changed -
/// except that a document without a number can be given the next free one on the way.
/// </summary>
public class StampModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DocumentStorageService _storage;
    private readonly DocumentFilingService _filing;
    private readonly ArchiveNumberService _numbers;
    private readonly CurrentUser _currentUser;
    private readonly AppConfig _config;
    private readonly ILogger<StampModel> _logger;
    private readonly IStringLocalizer<SharedResource> _l;

    public StampModel(
        AppDbContext db,
        DocumentStorageService storage,
        DocumentFilingService filing,
        ArchiveNumberService numbers,
        CurrentUser currentUser,
        AppConfig config,
        ILogger<StampModel> logger,
        IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _storage = storage;
        _filing = filing;
        _numbers = numbers;
        _currentUser = currentUser;
        _config = config;
        _logger = logger;
        _l = l;
    }

    /// <summary>Only a form post gets here (it may give a number); a plain visit finds nothing.</summary>
    public IActionResult OnGet(Guid token) => NotFound();

    public async Task<IActionResult> OnPostAsync(Guid token, string? corner, string? kind, bool assign, CancellationToken ct)
    {
        var document = await _db.Documents
            .Include(d => d.StorageLocation).ThenInclude(s => s!.Connection)
            .AccessibleTo(_currentUser)
            .FirstOrDefaultAsync(d => d.Token == token && d.UpdateState != UpdateState.Deleted, ct);
        if (document is null)
        {
            return NotFound();
        }

        IActionResult Back() => RedirectToPage("Edit", new { id = document.Id });

        if (!string.Equals(Path.GetExtension(document.OriginalFileName), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            this.Notify(_l["Only a PDF can be printed with its archive number."].Value, NoticeKind.Warn);
            return Back();
        }

        var number = document.ArchiveSerialNumber;
        if (number is null)
        {
            if (!assign)
            {
                this.Notify(_l["This document has no archive number yet."].Value, NoticeKind.Warn);
                return Back();
            }

            if (!await DocumentAccess.CanEditAsync(_db, document, _currentUser.UserId, _currentUser.IsAdmin, ct))
            {
                return Forbid();
            }

            number = await GiveNextNumberAsync(document, ct);
            if (number is null)
            {
                this.Notify(_l["No free archive number could be given. Please try again."].Value, NoticeKind.Danger);
                return Back();
            }
        }

        byte[] source;
        try
        {
            await using var stream = await _storage.OpenDocumentReadAsync(document, ct);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, ct);
            source = copy.ToArray();
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading document {DocumentId} for its archive number copy failed.", document.Id);
            return StatusCode(StatusCodes.Status502BadGateway, "The storage location is not reachable.");
        }

        var options = _config.ResolveBarcodeOptions();
        var wantedCorner = Enum.TryParse<StampCorner>(corner, ignoreCase: true, out var parsedCorner) ? parsedCorner : StampCorner.TopRight;
        var wantedKind = Enum.TryParse<BarcodeKind>(kind, ignoreCase: true, out var parsedKind) ? parsedKind : BarcodeKind.Qr;

        byte[] stamped;
        try
        {
            stamped = await Task.Run(() => PdfStamper.Stamp(source, options, number.Value, wantedCorner, wantedKind), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Putting the archive number on document {DocumentId} failed.", document.Id);
            this.Notify(_l["The number could not be put on this PDF (it may be protected or damaged)."].Value, NoticeKind.Danger);
            return Back();
        }

        // shown in the browser's own viewer (it has the print button); the name is what "save" proposes
        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(StampName(document.Title, options.AsnReadable(number.Value)));
        Response.Headers.ContentDisposition = disposition.ToString();
        Response.Headers.CacheControl = "no-store";
        return File(stamped, "application/pdf");
    }

    /// <summary>Gives the document the next free number (a few tries: somebody else may take it first). Null when that did not work.</summary>
    private async Task<long?> GiveNextNumberAsync(Document document, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var next = await _numbers.NextFreeAsync(ct);
            document.ArchiveSerialNumber = next;
            document.UpdateState = UpdateState.Updated;
            document.UpdateDate = DateTime.UtcNow;
            document.UpdateUserId = _currentUser.UserId;
            try
            {
                await _db.SaveChangesAsync(ct);
                await _filing.WriteMetadataFileAsync(document, ct);
                return next;
            }
            catch (DbUpdateException ex) when (DocumentIngestService.IsNumberTaken(ex))
            {
                // taken in the meantime: ask again
            }
        }

        document.ArchiveSerialNumber = null;
        return null;
    }

    private static string StampName(string title, string number)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((string.IsNullOrWhiteSpace(title) ? "Document" : title).Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        if (cleaned.Length > 80)
        {
            cleaned = cleaned[..80].TrimEnd();
        }

        return $"{cleaned} ({number}).pdf";
    }
}
