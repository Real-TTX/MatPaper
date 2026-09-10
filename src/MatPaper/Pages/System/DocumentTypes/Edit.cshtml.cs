using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.System.DocumentTypes;

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
    }

    public async Task<IActionResult> OnGetAsync()
    {
        SetBreadcrumb();

        if (IsEdit)
        {
            var entity = await _db.DocumentTypes
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == Id && t.UpdateState != UpdateState.Deleted);
            if (entity == null)
            {
                return NotFound();
            }

            Input = new InputModel { Name = entity.Name, MatchPattern = entity.MatchPattern };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetBreadcrumb();

        var name = Input.Name?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            ModelState.AddModelError("Input.Name", "Name is required.");
        }
        else
        {
            var nameTaken = await _db.DocumentTypes.AnyAsync(t =>
                t.Id != Id &&
                t.UpdateState != UpdateState.Deleted &&
                t.Name.ToLower() == name.ToLower());
            if (nameTaken)
            {
                ModelState.AddModelError("Input.Name", "That name is already in use.");
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var now = DateTime.UtcNow;

        if (IsEdit)
        {
            var entity = await _db.DocumentTypes.FirstOrDefaultAsync(t => t.Id == Id);
            if (entity == null)
            {
                return NotFound();
            }

            entity.Name = name;
            entity.MatchPattern = string.IsNullOrWhiteSpace(Input.MatchPattern) ? null : Input.MatchPattern.Trim();
            entity.UpdateState = UpdateState.Updated;
            entity.UpdateDate = now;
            entity.UpdateUserId = _currentUser.UserId;
        }
        else
        {
            _db.DocumentTypes.Add(new DocumentType
            {
                Name = name,
                MatchPattern = string.IsNullOrWhiteSpace(Input.MatchPattern) ? null : Input.MatchPattern.Trim(),
                UpdateState = UpdateState.Created,
                CreateDate = now,
                CreateUserId = _currentUser.UserId,
                UpdateDate = now,
                UpdateUserId = _currentUser.UserId
            });
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

        var entity = await _db.DocumentTypes.FirstOrDefaultAsync(t => t.Id == Id);
        if (entity == null)
        {
            return NotFound();
        }

        entity.UpdateState = UpdateState.Deleted;
        entity.UpdateDate = DateTime.UtcNow;
        entity.UpdateUserId = _currentUser.UserId;

        await _db.SaveChangesAsync();

        return RedirectToPage("Index");
    }

    private void SetBreadcrumb()
    {
        ViewData["Breadcrumb"] = IsEdit ? "System / Document types / Edit" : "System / Document types / New";
    }
}
