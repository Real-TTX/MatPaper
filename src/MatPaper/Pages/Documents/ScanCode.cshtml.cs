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
    private const long MaxBytes = 12L * 1024 * 1024;

    /// <summary>
    /// A phone sends several pictures a second and each takes a moment to read: only a few are read at the same time,
    /// the others are told to wait (the phone asks again) - a scanner must not eat the whole server.
    /// </summary>
    private static readonly SemaphoreSlim Readers = new(2);

    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;
    private readonly AppConfig _config;
    private readonly Fmt _fmt;

    public ScanCodeModel(AppDbContext db, CurrentUser currentUser, AppConfig config, Fmt fmt)
    {
        _db = db;
        _currentUser = currentUser;
        _config = config;
        _fmt = fmt;
    }

    public IActionResult OnGet() => NotFound();

    /// <summary>
    /// <c>status</c> is <c>none</c> (no code in the picture), <c>other</c> (a code that is no archive number),
    /// <c>busy</c> (try again in a moment) or <c>asn</c> - with <c>found</c>, <c>id</c>, <c>title</c>, <c>subtitle</c>
    /// and <c>url</c> of the document when there is one. <paramref name="photo"/> is a photo taken with the camera app:
    /// big, and looked at with more care than a frame of the live camera.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(IFormFile? image, bool photo, CancellationToken ct)
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

        if (!await Readers.WaitAsync(photo ? TimeSpan.FromSeconds(8) : TimeSpan.Zero, ct))
        {
            return new JsonResult(new { status = "busy" });
        }

        IReadOnlyList<string> texts;
        try
        {
            texts = await Task.Run(() => BarcodeReader.DecodeImage(bytes, photo), ct);
        }
        finally
        {
            Readers.Release();
        }

        if (texts.Count == 0)
        {
            return new JsonResult(new { status = "none" });
        }

        // a label of this app, or a bare number: the number is looked for among the documents the user may see
        var options = _config.ResolveBarcodeOptions();
        foreach (var text in texts)
        {
            if (options.ParseAsnLoosely(text) is not long number)
            {
                continue;
            }

            var document = await _db.Documents.AsNoTracking()
                .AccessibleTo(_currentUser)
                .Where(d => d.ArchiveSerialNumber == number && d.UpdateState != UpdateState.Deleted)
                .Select(d => new
                {
                    d.Id,
                    d.Title,
                    Correspondent = d.Correspondent != null ? d.Correspondent.Name : null,
                    Date = d.DocumentDate ?? d.CreateDate
                })
                .FirstOrDefaultAsync(ct);

            string? subtitle = null;
            if (document is not null)
            {
                subtitle = string.Join(" · ", new[] { document.Correspondent, _fmt.Date(document.Date) }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            return new JsonResult(new
            {
                status = "asn",
                asn = number,
                text = options.AsnReadable(number),
                found = document is not null,
                id = document?.Id,
                title = document?.Title,
                subtitle,
                url = document is null ? null : $"/Documents/Edit?id={document.Id}"
            });
        }

        var first = texts[0];
        return new JsonResult(new { status = "other", text = first.Length > 60 ? first[..60] : first });
    }
}
