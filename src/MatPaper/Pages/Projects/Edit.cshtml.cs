using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.Projects;

public class EditModel : PageModel
{
    private const string DefaultColor = "#808080";

    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;
    private readonly IStringLocalizer<SharedResource> _l;

    public EditModel(AppDbContext db, CurrentUser currentUser, IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _currentUser = currentUser;
        _l = l;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;

    // Ownership / sharing view state.
    public bool CanManage { get; private set; } = true;
    public bool IsCommon { get; private set; }
    public string? OwnerName { get; private set; }
    public List<UserShareView> UserShares { get; private set; } = new();
    public List<SelectListItem> ShareUserOptions { get; private set; } = new();

    /// <summary>Bound only for the "share with user" picker.</summary>
    public long? ShareUserId { get; set; }

    public record UserShareView(long ShareId, long UserId, string UserName, bool CanEdit);

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
                .Include(p => p.Owner)
                .Include(p => p.Shares).ThenInclude(s => s.User)
                .AccessibleTo(_currentUser)
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

            BuildAccessView(project);
        }
        else
        {
            Input.Color = DefaultColor;
        }

        return Page();
    }

    private void BuildAccessView(Project project)
    {
        CanManage = DocumentAccess.IsProjectOwnerOrAdmin(project, _currentUser.UserId, _currentUser.IsAdmin);
        IsCommon = project.IsCommon;
        OwnerName = project.Owner?.DisplayName ?? project.Owner?.Username;

        UserShares = project.Shares
            .Where(s => s.UpdateState != UpdateState.Deleted)
            .Select(s => new UserShareView(
                s.Id,
                s.UserId,
                s.User != null ? (s.User.DisplayName ?? s.User.Username) : $"#{s.UserId}",
                s.CanEdit))
            .OrderBy(s => s.UserName)
            .ToList();

        if (CanManage)
        {
            var alreadyShared = UserShares.Select(s => s.UserId).ToHashSet();
            ShareUserOptions = _db.Users
                .AsNoTracking()
                .Where(u => u.IsActive && u.Id != project.OwnerId)
                .OrderBy(u => u.DisplayName)
                .Select(u => new { u.Id, u.DisplayName, u.Username })
                .AsEnumerable()
                .Where(u => !alreadyShared.Contains(u.Id))
                .Select(u => new SelectListItem(u.DisplayName ?? u.Username, u.Id.ToString()))
                .ToList();
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetBreadcrumb();

        var name = Input.Name?.Trim() ?? string.Empty;
        var color = string.IsNullOrWhiteSpace(Input.Color) ? DefaultColor : Input.Color.Trim();

        Project? project = null;
        long? ownerScope = _currentUser.UserId;

        if (IsEdit)
        {
            project = await _db.Projects
                .AccessibleTo(_currentUser)
                .FirstOrDefaultAsync(p => p.Id == Id && p.UpdateState != UpdateState.Deleted);
            if (project == null)
            {
                return NotFound();
            }

            if (!DocumentAccess.IsProjectOwnerOrAdmin(project, _currentUser.UserId, _currentUser.IsAdmin))
            {
                return Forbid();
            }

            ownerScope = project.OwnerId;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            ModelState.AddModelError("Input.Name", _l["Name is required."]);
        }
        else
        {
            // Unique per owner (different users may reuse the same project name).
            var nameTaken = await _db.Projects
                .AnyAsync(p => p.Id != Id
                    && p.UpdateState != UpdateState.Deleted
                    && p.OwnerId == ownerScope
                    && p.Name.ToLower() == name.ToLower());
            if (nameTaken)
            {
                ModelState.AddModelError("Input.Name", _l["You already have a project with that name."]);
            }
        }

        if (!ModelState.IsValid)
        {
            if (project != null)
            {
                BuildAccessView(project);
            }
            return Page();
        }

        var now = DateTime.UtcNow;

        if (project != null)
        {
            project.Name = name;
            project.Color = color;
            project.UpdateState = UpdateState.Updated;
            project.UpdateDate = now;
            project.UpdateUserId = _currentUser.UserId;
        }
        else
        {
            _db.Projects.Add(new Project
            {
                Name = name,
                Color = color,
                OwnerId = _currentUser.UserId,
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

        var project = await LoadManageableAsync();
        if (project == null)
        {
            return NotFound();
        }

        if (!DocumentAccess.IsProjectOwnerOrAdmin(project, _currentUser.UserId, _currentUser.IsAdmin))
        {
            return Forbid();
        }

        project.UpdateState = UpdateState.Deleted;
        project.UpdateDate = DateTime.UtcNow;
        project.UpdateUserId = _currentUser.UserId;

        await _db.SaveChangesAsync();

        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostShareUserAsync(long shareUserId, bool canEdit)
    {
        var project = await LoadManageableAsync();
        if (project == null)
        {
            return NotFound();
        }

        if (!DocumentAccess.IsProjectOwnerOrAdmin(project, _currentUser.UserId, _currentUser.IsAdmin))
        {
            return Forbid();
        }

        var targetExists = await _db.Users.AnyAsync(u => u.Id == shareUserId && u.IsActive);
        if (!targetExists || shareUserId == project.OwnerId)
        {
            return RedirectToPage("Edit", new { id = Id });
        }

        var now = DateTime.UtcNow;
        var existing = await _db.ProjectShares
            .FirstOrDefaultAsync(s => s.ProjectId == Id && s.UserId == shareUserId);

        if (existing == null)
        {
            _db.ProjectShares.Add(new ProjectShare
            {
                ProjectId = Id,
                UserId = shareUserId,
                CanEdit = canEdit,
                UpdateState = UpdateState.Created,
                CreateDate = now,
                UpdateDate = now,
                CreateUserId = _currentUser.UserId,
                UpdateUserId = _currentUser.UserId
            });
        }
        else
        {
            existing.CanEdit = canEdit;
            existing.UpdateState = UpdateState.Updated;
            existing.UpdateDate = now;
            existing.UpdateUserId = _currentUser.UserId;
        }

        await _db.SaveChangesAsync();
        return RedirectToPage("Edit", new { id = Id });
    }

    public async Task<IActionResult> OnPostRevokeUserShareAsync(long shareId)
    {
        var project = await LoadManageableAsync();
        if (project == null)
        {
            return NotFound();
        }

        if (!DocumentAccess.IsProjectOwnerOrAdmin(project, _currentUser.UserId, _currentUser.IsAdmin))
        {
            return Forbid();
        }

        var share = await _db.ProjectShares
            .FirstOrDefaultAsync(s => s.Id == shareId && s.ProjectId == Id);
        if (share != null)
        {
            share.UpdateState = UpdateState.Deleted;
            share.UpdateDate = DateTime.UtcNow;
            share.UpdateUserId = _currentUser.UserId;
            await _db.SaveChangesAsync();
        }

        return RedirectToPage("Edit", new { id = Id });
    }

    public async Task<IActionResult> OnPostSetCommonAsync(bool isCommon)
    {
        var project = await LoadManageableAsync();
        if (project == null)
        {
            return NotFound();
        }

        if (!DocumentAccess.IsProjectOwnerOrAdmin(project, _currentUser.UserId, _currentUser.IsAdmin))
        {
            return Forbid();
        }

        project.IsCommon = isCommon;
        project.UpdateState = UpdateState.Updated;
        project.UpdateDate = DateTime.UtcNow;
        project.UpdateUserId = _currentUser.UserId;
        await _db.SaveChangesAsync();

        return RedirectToPage("Edit", new { id = Id });
    }

    private async Task<Project?> LoadManageableAsync()
    {
        if (!IsEdit)
        {
            return null;
        }

        return await _db.Projects
            .AccessibleTo(_currentUser)
            .FirstOrDefaultAsync(p => p.Id == Id && p.UpdateState != UpdateState.Deleted);
    }

    private void SetBreadcrumb()
    {
        ViewData["Breadcrumb"] = IsEdit ? "Projects / Edit" : "Projects / New";
    }
}
