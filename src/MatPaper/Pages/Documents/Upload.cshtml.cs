using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.Documents;

/// <summary>
/// "Add document": files (drag and drop) and camera scans on one page. Step 1 chooses what to add,
/// step 2 fills in what is already known (correspondent, type, project, tags, title). Everything lands
/// in the review inbox (local staging area); the storage location is chosen when it is confirmed there.
/// </summary>
public class UploadModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DocumentIngestService _ingest;
    private readonly ImageToPdfService _imageToPdf;
    private readonly CurrentUser _currentUser;
    private readonly IStringLocalizer<SharedResource> _l;

    public UploadModel(
        AppDbContext db,
        DocumentIngestService ingest,
        ImageToPdfService imageToPdf,
        CurrentUser currentUser,
        IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _ingest = ingest;
        _imageToPdf = imageToPdf;
        _currentUser = currentUser;
        _l = l;
    }

    [BindProperty]
    public List<IFormFile> Files { get; set; } = new();

    // Fields of step 2 (the page only reads them; the pickers bind to these names).
    public long? CorrespondentId { get; set; }
    public long? DocumentTypeId { get; set; }
    public long? ProjectId { get; set; }
    public long[] TagIds { get; set; } = Array.Empty<long>();

    public List<SelectListItem> CorrespondentOptions { get; private set; } = new();
    public List<SelectListItem> DocumentTypeOptions { get; private set; } = new();
    public List<SelectListItem> ProjectOptions { get; private set; } = new();
    public List<SelectListItem> TagOptions { get; private set; } = new();

    /// <summary>Open the scanner right away (the dashboard's "Scan" shortcut).</summary>
    [BindProperty(SupportsGet = true)]
    public bool Camera { get; set; }

    public List<string> Messages { get; } = new();

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "Documents / Add";
        await LoadOptionsAsync();
    }

    /// <summary>What step 2 may set on a new document; ids are checked against what the user may use.</summary>
    private sealed record Meta(long? CorrespondentId, long? DocumentTypeId, long? ProjectId, List<long> TagIds, string? Title);

    private async Task<Meta> ReadMetaAsync(long? correspondentId, long? documentTypeId, long? projectId, string? tagIds, string? title, CancellationToken ct)
    {
        if (correspondentId is long cid && !await _db.Correspondents.AnyAsync(c => c.Id == cid && c.UpdateState != UpdateState.Deleted, ct))
        {
            correspondentId = null;
        }
        if (documentTypeId is long tid && !await _db.DocumentTypes.AnyAsync(t => t.Id == tid && t.UpdateState != UpdateState.Deleted, ct))
        {
            documentTypeId = null;
        }
        if (projectId is long pid
            && !await _db.Projects.Where(p => p.UpdateState != UpdateState.Deleted).AccessibleTo(_currentUser).AnyAsync(p => p.Id == pid, ct))
        {
            projectId = null;
        }

        var wanted = (tagIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        var tags = wanted.Count == 0
            ? new List<long>()
            : await _db.Tags.Where(t => wanted.Contains(t.Id) && t.UpdateState != UpdateState.Deleted).Select(t => t.Id).ToListAsync(ct);

        return new Meta(correspondentId, documentTypeId, projectId, tags, string.IsNullOrWhiteSpace(title) ? null : title.Trim());
    }

    /// <summary>
    /// Async single-file ingest used by the uploader so it can report per-file progress. Returns JSON.
    /// The document is owned by the current user and lands in their inbox (Pending) for review.
    /// </summary>
    public async Task<IActionResult> OnPostAjaxAsync(
        IFormFile? file, long? correspondentId, long? documentTypeId, long? projectId, string? tagIds, string? title, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return new JsonResult(new { status = "failed", message = _l["Empty file."].Value });
        }

        var meta = await ReadMetaAsync(correspondentId, documentTypeId, projectId, tagIds, title, ct);
        await using var stream = file.OpenReadStream();
        var result = await _ingest.IngestAsync(
            stream, file.FileName,
            storageLocationId: null,
            meta.CorrespondentId, meta.DocumentTypeId, meta.ProjectId, meta.TagIds,
            _currentUser.UserId, ct);

        if (result.Status == IngestStatus.Created && meta.Title is not null)
        {
            await SetTitleAsync(result.DocumentId, meta.Title, ct);
        }

        return new JsonResult(new { status = result.Status.ToString().ToLowerInvariant(), id = result.DocumentId, name = file.FileName });
    }

    /// <summary>The pages of an in-browser scan (already cropped and filtered) become one PDF.</summary>
    public async Task<IActionResult> OnPostScanAsync(
        List<IFormFile> images, long? correspondentId, long? documentTypeId, long? projectId, string? tagIds, string? title, CancellationToken ct)
    {
        var pictures = new List<byte[]>();
        foreach (var image in images)
        {
            if (image.Length == 0)
            {
                continue;
            }

            await using var stream = image.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            pictures.Add(buffer.ToArray());
        }

        if (pictures.Count == 0)
        {
            return new JsonResult(new { status = "failed", message = _l["Please take or choose at least one photo."].Value });
        }

        byte[] pdf;
        try
        {
            pdf = _imageToPdf.Build(pictures, fullPage: true);
        }
        catch (Exception)
        {
            return new JsonResult(new { status = "failed", message = _l["The photos could not be converted into a PDF. Please try again with different images."].Value });
        }

        var meta = await ReadMetaAsync(correspondentId, documentTypeId, projectId, tagIds, title, ct);
        var fileName = $"{Sanitize(meta.Title ?? "Scan")}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.pdf";

        await using var pdfStream = new MemoryStream(pdf);
        var result = await _ingest.IngestAsync(
            pdfStream, fileName,
            storageLocationId: null,
            meta.CorrespondentId, meta.DocumentTypeId, meta.ProjectId, meta.TagIds,
            _currentUser.UserId, ct,
            origin: DocumentOrigin.CameraScan);

        return new JsonResult(new { status = result.Status.ToString().ToLowerInvariant(), id = result.DocumentId, name = fileName });
    }

    /// <summary>Plain form post (no JavaScript): files only.</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        ViewData["Breadcrumb"] = "Documents / Add";
        await LoadOptionsAsync();

        if (Files.Count == 0)
        {
            ModelState.AddModelError(nameof(Files), _l["Please choose at least one file to upload."]);
            return Page();
        }

        int created = 0, duplicate = 0, failed = 0;
        foreach (var file in Files)
        {
            if (file.Length == 0)
            {
                continue;
            }

            await using var stream = file.OpenReadStream();
            var result = await _ingest.IngestAsync(
                stream, file.FileName,
                storageLocationId: null, correspondentId: null, documentTypeId: null, projectId: null,
                tagIds: Array.Empty<long>(),
                _currentUser.UserId, ct);

            switch (result.Status)
            {
                case IngestStatus.Created:
                    created++;
                    break;
                case IngestStatus.Duplicate:
                    duplicate++;
                    Messages.Add(_l["\"{0}\" was skipped as a duplicate of an existing document.", file.FileName].Value);
                    break;
                default:
                    failed++;
                    Messages.Add(_l["\"{0}\" could not be stored.", file.FileName].Value);
                    break;
            }
        }

        if (created > 0)
        {
            var parts = new List<string> { _l["{0} document(s) added to your inbox", created].Value };
            if (duplicate > 0) { parts.Add(_l["{0} duplicate(s) skipped", duplicate].Value); }
            if (failed > 0) { parts.Add(_l["{0} failed", failed].Value); }
            this.Notify(string.Join(", ", parts) + ".");
            return RedirectToPage("/Inbox/Index");
        }

        if (Messages.Count == 0)
        {
            Messages.Add(_l["No documents were uploaded."].Value);
        }

        return Page();
    }

    private async Task SetTitleAsync(long documentId, string title, CancellationToken ct)
    {
        var document = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is null)
        {
            return;
        }

        document.Title = title.Length > 250 ? title[..250] : title;
        document.UpdateDate = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task LoadOptionsAsync()
    {
        CorrespondentOptions = (await _db.Correspondents.AsNoTracking()
                .Where(c => c.UpdateState != UpdateState.Deleted).OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name }).ToListAsync())
            .Select(c => new SelectListItem(c.Name, c.Id.ToString())).ToList();
        DocumentTypeOptions = (await _db.DocumentTypes.AsNoTracking()
                .Where(t => t.UpdateState != UpdateState.Deleted).OrderBy(t => t.Name)
                .Select(t => new { t.Id, t.Name }).ToListAsync())
            .Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToList();
        ProjectOptions = (await _db.Projects.AsNoTracking()
                .Where(p => p.UpdateState != UpdateState.Deleted).AccessibleTo(_currentUser).OrderBy(p => p.Name)
                .Select(p => new { p.Id, p.Name }).ToListAsync())
            .Select(p => new SelectListItem(p.Name, p.Id.ToString())).ToList();
        TagOptions = (await _db.Tags.AsNoTracking()
                .Where(t => t.UpdateState != UpdateState.Deleted).OrderBy(t => t.Name)
                .Select(t => new { t.Id, t.Name }).ToListAsync())
            .Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToList();
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Scan" : cleaned;
    }
}
