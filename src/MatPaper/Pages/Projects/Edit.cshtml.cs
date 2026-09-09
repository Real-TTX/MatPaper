using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Projects;

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
            var project = await _db.Projects
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == Id && p.UpdateState != UpdateState.Deleted);
            if (project == null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Name = project.Name,
                Color = string.IsNullOrWhiteSpace(project.Color) ? DefaultColor : project.Color
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
            var nameTaken = await _db.Projects
                .AnyAsync(p => p.Id != Id
                    && p.UpdateState != UpdateState.Deleted
                    && p.Name.ToLower() == name.ToLower());
            if (nameTaken)
            {
                ModelState.AddModelError("Input.Name", "A project with that name already exists.");
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var now = DateTime.UtcNow;

        if (IsEdit)
        {
            var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == Id);
            if (project == null || project.UpdateState == UpdateState.Deleted)
            {
                return NotFound();
            }

            project.Name = name;
            project.Color = color;
            project.UpdateState = UpdateState.Updated;
            project.UpdateDate = now;
            project.UpdateUserId = _currentUser.UserId;
        }
        else
        {
            var project = new Project
            {
                Name = name,
                Color = color,
                UpdateState = UpdateState.Created,
                CreateDate = now,
                CreateUserId = _currentUser.UserId,
                UpdateDate = now,
                UpdateUserId = _currentUser.UserId
            };
            _db.Projects.Add(project);
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

        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == Id);
        if (project == null)
        {
            return NotFound();
        }

        project.UpdateState = UpdateState.Deleted;
        project.UpdateDate = DateTime.UtcNow;
        project.UpdateUserId = _currentUser.UserId;

        await _db.SaveChangesAsync();

        return RedirectToPage("Index");
    }

    private void SetBreadcrumb()
    {
        ViewData["Breadcrumb"] = IsEdit ? "Projects / Edit" : "Projects / New";
    }
}
