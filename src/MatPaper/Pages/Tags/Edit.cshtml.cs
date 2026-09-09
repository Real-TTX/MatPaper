using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Tags;

public class EditModel : PageModel
{
    private const string DefaultColor = "#808080";

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
        public string? Color { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        SetBreadcrumb();

        if (IsEdit)
        {
            var tag = await _db.Tags
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == Id && t.UpdateState != UpdateState.Deleted);
            if (tag == null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Name = tag.Name,
                Color = string.IsNullOrWhiteSpace(tag.Color) ? DefaultColor : tag.Color
            };
        }
        else
        {
            Input.Color = DefaultColor;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetBreadcrumb();

        var name = Input.Name?.Trim() ?? string.Empty;
        var color = string.IsNullOrWhiteSpace(Input.Color) ? DefaultColor : Input.Color.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ModelState.AddModelError("Input.Name", "Name is required.");
        }
        else
        {
            var nameTaken = await _db.Tags
                .AnyAsync(t => t.Id != Id
                    && t.UpdateState != UpdateState.Deleted
                    && t.Name.ToLower() == name.ToLower());
            if (nameTaken)
            {
                ModelState.AddModelError("Input.Name", "A tag with that name already exists.");
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var now = DateTime.UtcNow;

        if (IsEdit)
        {
            var tag = await _db.Tags.FirstOrDefaultAsync(t => t.Id == Id);
            if (tag == null || tag.UpdateState == UpdateState.Deleted)
            {
                return NotFound();
            }

            tag.Name = name;
            tag.Color = color;
            tag.UpdateState = UpdateState.Updated;
            tag.UpdateDate = now;
            tag.UpdateUserId = _currentUser.UserId;
        }
        else
        {
            var tag = new Tag
            {
                Name = name,
                Color = color,
                UpdateState = UpdateState.Created,
                CreateDate = now,
                CreateUserId = _currentUser.UserId,
                UpdateDate = now,
                UpdateUserId = _currentUser.UserId
            };
            _db.Tags.Add(tag);
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

        var tag = await _db.Tags.FirstOrDefaultAsync(t => t.Id == Id);
        if (tag == null)
        {
            return NotFound();
        }

        tag.UpdateState = UpdateState.Deleted;
        tag.UpdateDate = DateTime.UtcNow;
        tag.UpdateUserId = _currentUser.UserId;

        await _db.SaveChangesAsync();

        return RedirectToPage("Index");
    }

    private void SetBreadcrumb()
    {
        ViewData["Breadcrumb"] = IsEdit ? "Tags / Edit" : "Tags / New";
    }
}
