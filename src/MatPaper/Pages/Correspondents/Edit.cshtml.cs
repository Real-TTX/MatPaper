using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Correspondents;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;

    public EditModel(AppDbContext db, CurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public string? MatchPattern { get; set; }
        public string? Notes { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        SetBreadcrumb();

        if (IsEdit)
        {
            var correspondent = await _db.Correspondents
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == Id && c.UpdateState != UpdateState.Deleted);
            if (correspondent == null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Name = correspondent.Name,
                MatchPattern = correspondent.MatchPattern,
                Notes = correspondent.Notes
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetBreadcrumb();

        var name = Input.Name?.Trim() ?? string.Empty;
        var matchPattern = string.IsNullOrWhiteSpace(Input.MatchPattern) ? null : Input.MatchPattern.Trim();
        var notes = string.IsNullOrWhiteSpace(Input.Notes) ? null : Input.Notes.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ModelState.AddModelError("Input.Name", "Name is required.");
        }
        else
        {
            var nameTaken = await _db.Correspondents.AnyAsync(c =>
                c.Id != Id &&
                c.UpdateState != UpdateState.Deleted &&
                c.Name.ToLower() == name.ToLower());
            if (nameTaken)
            {
                ModelState.AddModelError("Input.Name", "A correspondent with that name already exists.");
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var now = DateTime.UtcNow;

        if (IsEdit)
        {
            var existing = await _db.Correspondents.FirstOrDefaultAsync(c => c.Id == Id);
            if (existing == null || existing.UpdateState == UpdateState.Deleted)
            {
                return NotFound();
            }

            existing.Name = name;
            existing.MatchPattern = matchPattern;
            existing.Notes = notes;
            existing.UpdateState = UpdateState.Updated;
            existing.UpdateDate = now;
            existing.UpdateUserId = _currentUser.UserId;
        }
        else
        {
            var correspondent = new Correspondent
            {
                Name = name,
                MatchPattern = matchPattern,
                Notes = notes,
                UpdateState = UpdateState.Created,
                CreateDate = now,
                UpdateDate = now,
                CreateUserId = _currentUser.UserId,
                UpdateUserId = _currentUser.UserId
            };
            _db.Correspondents.Add(correspondent);
        }

        await _db.SaveChangesAsync();

        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!IsEdit)
        {
            return RedirectToPage("Index");
        }

        var correspondent = await _db.Correspondents.FirstOrDefaultAsync(c => c.Id == Id);
        if (correspondent == null)
        {
            return NotFound();
        }

        correspondent.UpdateState = UpdateState.Deleted;
        correspondent.UpdateDate = DateTime.UtcNow;
        correspondent.UpdateUserId = _currentUser.UserId;

        await _db.SaveChangesAsync();

        return RedirectToPage("Index");
    }

    private void SetBreadcrumb()
    {
        ViewData["Breadcrumb"] = IsEdit ? "Correspondents / Edit" : "Correspondents / New";
    }
}
