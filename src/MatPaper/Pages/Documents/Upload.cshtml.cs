using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Documents;

public class UploadModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DocumentIngestService _ingest;
    private readonly CurrentUser _currentUser;

    public UploadModel(AppDbContext db, DocumentIngestService ingest, CurrentUser currentUser)
    {
        _db = db;
        _ingest = ingest;
        _currentUser = currentUser;
    }

    [BindProperty]
    public List<IFormFile> Files { get; set; } = new();

    [BindProperty]
    public long StorageLocationId { get; set; }

    [BindProperty]
    public long? CorrespondentId { get; set; }

    [BindProperty]
    public long? DocumentTypeId { get; set; }

    [BindProperty]
    public long? ProjectId { get; set; }

    [BindProperty]
    public long[] TagIds { get; set; } = Array.Empty<long>();

    public IReadOnlyList<StorageLocation> StorageLocations { get; private set; } = Array.Empty<StorageLocation>();
    public IReadOnlyList<Correspondent> Correspondents { get; private set; } = Array.Empty<Correspondent>();
    public IReadOnlyList<DocumentType> DocumentTypes { get; private set; } = Array.Empty<DocumentType>();
    public IReadOnlyList<Project> Projects { get; private set; } = Array.Empty<Project>();
    public IReadOnlyList<Tag> Tags { get; private set; } = Array.Empty<Tag>();

    public bool HasStorage => StorageLocations.Count > 0;
    public List<string> Messages { get; } = new();

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "Documents / Upload";

        await LoadOptionsAsync();

        var defaultLocation = StorageLocations.FirstOrDefault(s => s.IsDefault) ?? StorageLocations.FirstOrDefault();
        if (defaultLocation != null)
        {
            StorageLocationId = defaultLocation.Id;
        }
    }

    /// <summary>
    /// Async single-file ingest used by the drag-&-drop uploader so it can report
    /// per-file progress. Returns JSON. The document is owned by the current user
    /// and lands in their inbox (Pending) for review.
    /// </summary>
    public async Task<IActionResult> OnPostAjaxAsync(IFormFile? file, long storageLocationId, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return new JsonResult(new { status = "failed", message = "Empty file." });
        }

        var validLocation = await _db.StorageLocations
            .AnyAsync(s => s.Id == storageLocationId && s.UpdateState != UpdateState.Deleted, ct);
        if (!validLocation)
        {
            return new JsonResult(new { status = "failed", message = "Invalid storage location." });
        }

        await using var stream = file.OpenReadStream();
        var result = await _ingest.IngestAsync(
            stream,
            file.FileName,
            storageLocationId,
            correspondentId: null,
            documentTypeId: null,
            projectId: null,
            tagIds: Array.Empty<long>(),
            _currentUser.UserId,
            ct);

        return new JsonResult(new
        {
            status = result.Status.ToString().ToLowerInvariant(),
            id = result.DocumentId,
            name = file.FileName
        });
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        ViewData["Breadcrumb"] = "Documents / Upload";

        await LoadOptionsAsync();

        if (!HasStorage)
        {
            return Page();
        }

        if (Files.Count == 0)
        {
            ModelState.AddModelError(nameof(Files), "Please choose at least one file to upload.");
            return Page();
        }

        var validLocation = StorageLocations.Any(s => s.Id == StorageLocationId);
        if (!validLocation)
        {
            ModelState.AddModelError(nameof(StorageLocationId), "Please select a valid storage location.");
            return Page();
        }

        int created = 0;
        int duplicate = 0;
        int failed = 0;

        foreach (var file in Files)
        {
            if (file.Length == 0)
            {
                continue;
            }

            await using var stream = file.OpenReadStream();
            var result = await _ingest.IngestAsync(
                stream,
                file.FileName,
                StorageLocationId,
                CorrespondentId,
                DocumentTypeId,
                ProjectId,
                TagIds,
                _currentUser.UserId,
                ct);

            switch (result.Status)
            {
                case IngestStatus.Created:
                    created++;
                    break;
                case IngestStatus.Duplicate:
                    duplicate++;
                    Messages.Add($"\"{file.FileName}\" was skipped as a duplicate of an existing document.");
                    break;
                case IngestStatus.NoStorage:
                default:
                    failed++;
                    Messages.Add($"\"{file.FileName}\" could not be stored (no valid storage location).");
                    break;
            }
        }

        if (created > 0)
        {
            var parts = new List<string> { $"{created} document(s) uploaded" };
            if (duplicate > 0)
            {
                parts.Add($"{duplicate} duplicate(s) skipped");
            }
            if (failed > 0)
            {
                parts.Add($"{failed} failed");
            }

            TempData["UploadSummary"] = string.Join(", ", parts) + ".";
            return RedirectToPage("Index");
        }

        if (Messages.Count == 0)
        {
            Messages.Add("No documents were uploaded.");
        }

        return Page();
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

        Projects = await _db.Projects.AsNoTracking()
            .Where(p => p.UpdateState != UpdateState.Deleted)
            .OrderBy(p => p.Name)
            .ToListAsync();

        Tags = await _db.Tags.AsNoTracking()
            .Where(t => t.UpdateState != UpdateState.Deleted)
            .OrderBy(t => t.Name)
            .ToListAsync();
    }
}
