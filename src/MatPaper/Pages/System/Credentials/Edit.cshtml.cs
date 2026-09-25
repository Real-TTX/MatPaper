using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.System.Credentials;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly SecretProtector _secrets;
    private readonly CurrentUser _currentUser;
    private readonly IStringLocalizer<SharedResource> _l;

    public EditModel(AppDbContext db, SecretProtector secrets, CurrentUser currentUser, IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _secrets = secrets;
        _currentUser = currentUser;
        _l = l;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string? Domain { get; set; }
        public string? Password { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        SetBreadcrumb();

        if (IsEdit)
        {
            var entity = await _db.Credentials
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == Id && c.UpdateState != UpdateState.Deleted);
            if (entity == null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Name = entity.Name,
                Username = entity.Username,
                Domain = entity.Domain
                // Password intentionally left blank.
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetBreadcrumb();

        var name = Input.Name?.Trim() ?? string.Empty;
        var username = Input.Username?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            ModelState.AddModelError("Input.Name", _l["Name is required."]);
        }
        if (string.IsNullOrWhiteSpace(username))
        {
            ModelState.AddModelError("Input.Username", _l["Username is required."]);
        }
        else
        {
            var nameTaken = await _db.Credentials
                .AnyAsync(c => c.Id != Id && c.UpdateState != UpdateState.Deleted && c.Name.ToLower() == name.ToLower());
            if (nameTaken)
            {
                ModelState.AddModelError("Input.Name", _l["A credential with that name already exists."]);
            }
        }

        if (!IsEdit && string.IsNullOrEmpty(Input.Password))
        {
            ModelState.AddModelError("Input.Password", _l["A password is required for a new credential."]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var now = DateTime.UtcNow;
        var domain = string.IsNullOrWhiteSpace(Input.Domain) ? null : Input.Domain.Trim();

        if (IsEdit)
        {
            var entity = await _db.Credentials.FirstOrDefaultAsync(c => c.Id == Id && c.UpdateState != UpdateState.Deleted);
            if (entity == null)
            {
                return NotFound();
            }

            entity.Name = name;
            entity.Username = username;
            entity.Domain = domain;
            if (!string.IsNullOrEmpty(Input.Password))
            {
                entity.ProtectedPassword = _secrets.Protect(Input.Password);
            }
            entity.UpdateState = UpdateState.Updated;
            entity.UpdateDate = now;
            entity.UpdateUserId = _currentUser.UserId;
        }
        else
        {
            _db.Credentials.Add(new Credential
            {
                Name = name,
                Username = username,
                Domain = domain,
                ProtectedPassword = _secrets.Protect(Input.Password!),
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

        var entity = await _db.Credentials.FirstOrDefaultAsync(c => c.Id == Id && c.UpdateState != UpdateState.Deleted);
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
        ViewData["Breadcrumb"] = IsEdit ? "System / Credentials / Edit" : "System / Credentials / New";
    }
}
