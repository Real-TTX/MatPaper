using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MatPaper.Services;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.Documents;

/// <summary>
/// Upload page. Files go straight into the review inbox (local staging area); the
/// storage location is chosen when the document is confirmed there.
/// </summary>
public class UploadModel : PageModel
{
    private readonly DocumentIngestService _ingest;
    private readonly CurrentUser _currentUser;
    private readonly IStringLocalizer<SharedResource> _l;

    public UploadModel(DocumentIngestService ingest, CurrentUser currentUser, IStringLocalizer<SharedResource> l)
    {
        _ingest = ingest;
        _currentUser = currentUser;
        _l = l;
    }

    [BindProperty]
    public List<IFormFile> Files { get; set; } = new();

    public List<string> Messages { get; } = new();

    public void OnGet()
    {
        ViewData["Breadcrumb"] = "Documents / Add";
    }

    /// <summary>
    /// Async single-file ingest used by the drag-&-drop uploader so it can report
    /// per-file progress. Returns JSON. The document is owned by the current user
    /// and lands in their inbox (Pending) for review.
    /// </summary>
    public async Task<IActionResult> OnPostAjaxAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return new JsonResult(new { status = "failed", message = _l["Empty file."].Value });
        }

        await using var stream = file.OpenReadStream();
        var result = await _ingest.IngestAsync(
            stream,
            file.FileName,
            storageLocationId: null,
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
        ViewData["Breadcrumb"] = "Documents / Add";

        if (Files.Count == 0)
        {
            ModelState.AddModelError(nameof(Files), _l["Please choose at least one file to upload."]);
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
                storageLocationId: null,
                correspondentId: null,
                documentTypeId: null,
                projectId: null,
                tagIds: Array.Empty<long>(),
                _currentUser.UserId,
                ct);

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
            if (duplicate > 0)
            {
                parts.Add(_l["{0} duplicate(s) skipped", duplicate].Value);
            }
            if (failed > 0)
            {
                parts.Add(_l["{0} failed", failed].Value);
            }

            TempData["InboxMessage"] = string.Join(", ", parts) + ".";
            return RedirectToPage("/Inbox/Index");
        }

        if (Messages.Count == 0)
        {
            Messages.Add(_l["No documents were uploaded."].Value);
        }

        return Page();
    }
}
