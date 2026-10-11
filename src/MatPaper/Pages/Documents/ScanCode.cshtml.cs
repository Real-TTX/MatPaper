using MatPaper.Configuration;
using MatPaper.Data;
using MatPaper.Services;
using MatPaper.Services.Barcodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Pages.Documents;

/// <summary>
/// Reads the code in a picture the camera of a phone (or a laptop) took of a label and answers with the document that
/// has the number: "scan the label on the paper, see the document". The picture is looked at and thrown away; what is
/// answered is only what the signed-in user may see.
/// </summary>
public class ScanCodeModel : PageModel
{
    private const long MaxBytes = 8L * 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;
    private readonly AppConfig _config;

    public ScanCodeModel(AppDbContext db, CurrentUser currentUser, AppConfig config)
    {
        _db = db;
        _currentUser = currentUser;
        _config = config;
    }

    public IActionResult OnGet() => NotFound();

    /// <summary>
    /// <c>status</c> is <c>none</c> (no code in the picture), <c>other</c> (a code that is no archive number) or
    /// <c>asn</c> (with <c>found</c>, <c>id</c>, <c>title</c> and <c>url</c> of the document when there is one).
    /// </summary>
    public async Task<IActionResult> OnPostAsync(IFormFile? image, CancellationToken ct)
    {
        if (!_config.ArchiveNumbersEnabled)
        {
            return NotFound();
        }

        if (image is null || image.Length == 0 || image.Length > MaxBytes)
        {
            return new JsonResult(new { status = "error" });
        }

        byte[] bytes;
        await using (var stream = image.OpenReadStream())
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, ct);
            bytes = copy.ToArray();
        }

        var texts = await Task.Run(() => BarcodeReader.DecodeImage(bytes), ct);
        if (texts.Count == 0)
        {
            return new JsonResult(new { status = "none" });
        }

        var options = _config.ResolveBarcodeOptions();
        foreach (var text in texts)
        {
            if (options.ParseAsn(text) is not long number)
            {
                continue;
            }

            var document = await _db.Documents.AsNoTracking()
                .AccessibleTo(_currentUser)
                .Where(d => d.ArchiveSerialNumber == number && d.UpdateState != UpdateState.Deleted)
                .Select(d => new { d.Id, d.Title })
                .FirstOrDefaultAsync(ct);

            return new JsonResult(new
            {
                status = "asn",
                asn = number,
                text = options.AsnReadable(number),
                found = document is not null,
                id = document?.Id,
                title = document?.Title,
                url = document is null ? null : $"/Documents/Edit?id={document.Id}"
            });
        }

        var first = texts[0];
        return new JsonResult(new { status = "other", text = first.Length > 60 ? first[..60] : first });
    }
}
