using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.System.StorageLocations;

public class EditModel : PageModel
{
    private const string DefaultPathTemplate = "{Correspondent}/{Year}/{DocumentType}/{Date} {Title}{Ext}";

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
        public string RootPath { get; set; } = string.Empty;
        public string PathTemplate { get; set; } = string.Empty;
        public bool IsDefault { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        SetBreadcrumb();

        if (IsEdit)
        {
            var entity = await _db.StorageLocations
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == Id && s.UpdateState != UpdateState.Deleted);
            if (entity == null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Name = entity.Name,
                RootPath = entity.RootPath,
                PathTemplate = entity.PathTemplate,
                IsDefault = entity.IsDefault
            };
        }
        else
        {
            Input.PathTemplate = DefaultPathTemplate;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetBreadcrumb();

        var name = Input.Name?.Trim() ?? string.Empty;
        var rootPath = Input.RootPath?.Trim() ?? string.Empty;
        var pathTemplate = Input.PathTemplate?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            ModelState.AddModelError("Input.Name", "Name is required.");
        }
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            ModelState.AddModelError("Input.RootPath", "Root path is required.");
        }
        if (string.IsNullOrWhiteSpace(pathTemplate))
        {
            ModelState.AddModelError("Input.PathTemplate", "Path template is required.");
        }

        var nameTaken = await _db.StorageLocations
            .AnyAsync(s => s.Id != Id
                && s.UpdateState != UpdateState.Deleted
                && s.Name.ToLower() == name.ToLower());
        if (nameTaken)
        {
            ModelState.AddModelError("Input.Name", "A storage location with that name already exists.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var now = DateTime.UtcNow;

        StorageLocation entity;
        if (IsEdit)
        {
            var existing = await _db.StorageLocations
                .FirstOrDefaultAsync(s => s.Id == Id && s.UpdateState != UpdateState.Deleted);
            if (existing == null)
            {
                return NotFound();
            }

            existing.Name = name;
            existing.RootPath = rootPath;
            existing.PathTemplate = pathTemplate;
            existing.IsDefault = Input.IsDefault;
            existing.UpdateState = UpdateState.Updated;
            existing.UpdateDate = now;
            existing.UpdateUserId = _currentUser.UserId;
            entity = existing;
        }
        else
        {
            entity = new StorageLocation
            {
                Name = name,
                RootPath = rootPath,
                PathTemplate = pathTemplate,
                IsDefault = Input.IsDefault,
                UpdateState = UpdateState.Created,
                CreateDate = now,
                CreateUserId = _currentUser.UserId,
                UpdateDate = now,
                UpdateUserId = _currentUser.UserId
            };
            _db.StorageLocations.Add(entity);
        }

        // Guard: at most one default among non-deleted locations.
        if (Input.IsDefault)
        {
            var others = await _db.StorageLocations
                .Where(s => s.Id != Id
                    && s.UpdateState != UpdateState.Deleted
                    && s.IsDefault)
                .ToListAsync();
            foreach (var other in others)
            {
                other.IsDefault = false;
                other.UpdateState = UpdateState.Updated;
                other.UpdateDate = now;
                other.UpdateUserId = _currentUser.UserId;
            }
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

        var entity = await _db.StorageLocations
            .FirstOrDefaultAsync(s => s.Id == Id && s.UpdateState != UpdateState.Deleted);
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
        ViewData["Breadcrumb"] = IsEdit
            ? "System / Storage locations / Edit"
            : "System / Storage locations / New";
    }
}
