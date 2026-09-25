using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.Documents;

/// <summary>
/// Camera scan: one or more photos become a single PDF that lands in the review inbox
/// (staging area). The storage location is chosen when the document is confirmed.
/// </summary>
public class ScanModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ImageToPdfService _imageToPdf;
    private readonly DocumentIngestService _ingest;
    private readonly CurrentUser _currentUser;
    private readonly IStringLocalizer<SharedResource> _l;

    public ScanModel(
        AppDbContext db,
        ImageToPdfService imageToPdf,
        DocumentIngestService ingest,
        CurrentUser currentUser,
        IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _imageToPdf = imageToPdf;
        _ingest = ingest;
        _currentUser = currentUser;
        _l = l;
    }

    [BindProperty]
    public string? Title { get; set; }

    [BindProperty]
    public List<IFormFile> Images { get; set; } = new();

    [BindProperty]
    public long? CorrespondentId { get; set; }

    [BindProperty]
    public long? DocumentTypeId { get; set; }

    public IReadOnlyList<Correspondent> Correspondents { get; private set; } = Array.Empty<Correspondent>();
    public IReadOnlyList<DocumentType> DocumentTypes { get; private set; } = Array.Empty<DocumentType>();

    public List<string> Messages { get; } = new();

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "Documents / Add";
        await LoadOptionsAsync();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        ViewData["Breadcrumb"] = "Documents / Add";

        await LoadOptionsAsync();

        var images = new List<byte[]>();
        foreach (var file in Images)
        {
            if (file.Length == 0)
            {
                continue;
            }

            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            images.Add(buffer.ToArray());
        }

        if (images.Count == 0)
        {
            ModelState.AddModelError(nameof(Images), _l["Please take or choose at least one photo."]);
            return Page();
        }

        byte[] pdfBytes;
        try
        {
            pdfBytes = _imageToPdf.Build(images);
        }
        catch (Exception)
        {
            Messages.Add(_l["The photos could not be converted into a PDF. Please try again with different images."].Value);
            return Page();
        }

        var baseName = string.IsNullOrWhiteSpace(Title) ? "Scan" : Title.Trim();
        var fileName = $"{Sanitize(baseName)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.pdf";

        await using var pdfStream = new MemoryStream(pdfBytes);
        var result = await _ingest.IngestAsync(
            pdfStream,
            fileName,
            storageLocationId: null,
            CorrespondentId,
            DocumentTypeId,
            null,
            Array.Empty<long>(),
            _currentUser.UserId,
            ct);

        switch (result.Status)
        {
            case IngestStatus.Created:
                return RedirectToPage("Edit", new { id = result.DocumentId, returnUrl = "/Inbox" });
            case IngestStatus.Duplicate:
                Messages.Add(_l["This scan matches an existing document; nothing new was created."].Value);
                return Page();
            default:
                Messages.Add(_l["The scan could not be stored."].Value);
                return Page();
        }
    }

    private async Task LoadOptionsAsync()
    {
        Correspondents = await _db.Correspondents.AsNoTracking()
            .Where(c => c.UpdateState != UpdateState.Deleted)
            .OrderBy(c => c.Name)
            .ToListAsync();

        DocumentTypes = await _db.DocumentTypes.AsNoTracking()
            .Where(t => t.UpdateState != UpdateState.Deleted)
            .OrderBy(t => t.Name)
            .ToListAsync();
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '-' : c).ToArray();
        var cleaned = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Scan" : cleaned;
    }
}
