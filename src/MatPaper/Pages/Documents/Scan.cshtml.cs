using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Documents;

public class ScanModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ImageToPdfService _imageToPdf;
    private readonly DocumentIngestService _ingest;
    private readonly CurrentUser _currentUser;

    public ScanModel(
        AppDbContext db,
        ImageToPdfService imageToPdf,
        DocumentIngestService ingest,
        CurrentUser currentUser)
    {
        _db = db;
        _imageToPdf = imageToPdf;
        _ingest = ingest;
        _currentUser = currentUser;
    }

    [BindProperty]
    public string? Title { get; set; }

    [BindProperty]
    public List<IFormFile> Images { get; set; } = new();

    [BindProperty]
    public long StorageLocationId { get; set; }

    [BindProperty]
    public long? CorrespondentId { get; set; }

    [BindProperty]
    public long? DocumentTypeId { get; set; }

    public IReadOnlyList<StorageLocation> StorageLocations { get; private set; } = Array.Empty<StorageLocation>();
    public IReadOnlyList<Correspondent> Correspondents { get; private set; } = Array.Empty<Correspondent>();
    public IReadOnlyList<DocumentType> DocumentTypes { get; private set; } = Array.Empty<DocumentType>();

    public bool HasStorage => StorageLocations.Count > 0;
    public List<string> Messages { get; } = new();

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "Documents / Scan";

        await LoadOptionsAsync();

        var defaultLocation = StorageLocations.FirstOrDefault(s => s.IsDefault) ?? StorageLocations.FirstOrDefault();
        if (defaultLocation != null)
        {
            StorageLocationId = defaultLocation.Id;
        }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        ViewData["Breadcrumb"] = "Documents / Scan";

        await LoadOptionsAsync();

        if (!HasStorage)
        {
            return Page();
        }

        var validLocation = StorageLocations.Any(s => s.Id == StorageLocationId);
        if (!validLocation)
        {
            ModelState.AddModelError(nameof(StorageLocationId), "Please select a valid storage location.");
            return Page();
        }

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
            ModelState.AddModelError(nameof(Images), "Please take or choose at least one photo.");
            return Page();
        }

        byte[] pdfBytes;
        try
        {
            pdfBytes = _imageToPdf.Build(images);
        }
        catch (Exception)
        {
            Messages.Add("The photos could not be converted into a PDF. Please try again with different images.");
            return Page();
        }

        var baseName = string.IsNullOrWhiteSpace(Title) ? "Scan" : Title.Trim();
        var fileName = $"{Sanitize(baseName)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.pdf";

        await using var pdfStream = new MemoryStream(pdfBytes);
        var result = await _ingest.IngestAsync(
            pdfStream,
            fileName,
            StorageLocationId,
            CorrespondentId,
            DocumentTypeId,
            null,
            Array.Empty<long>(),
            _currentUser.UserId,
            ct);

        switch (result.Status)
        {
            case IngestStatus.Created:
                return RedirectToPage("Edit", new { id = result.DocumentId });
            case IngestStatus.Duplicate:
                Messages.Add("This scan matches an existing document; nothing new was created.");
                return Page();
            case IngestStatus.NoStorage:
            default:
                Messages.Add("The scan could not be stored (no valid storage location).");
                return Page();
        }
    }

    private async Task LoadOptionsAsync()
    {
        StorageLocations = await _db.StorageLocations.AsNoTracking()
            .Where(s => s.UpdateState != UpdateState.Deleted)
            .OrderByDescending(s => s.IsDefault)
            .ThenBy(s => s.Name)
            .ToListAsync();

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
