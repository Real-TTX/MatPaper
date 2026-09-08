using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.System.Users;

public class EditModel : PageModel
{
    private const int MinPasswordLength = 8;

    private readonly AppDbContext _db;
    private readonly SignInService _signIn;
    private readonly CurrentUser _currentUser;
    private readonly PasswordHasher<User> _hasher;

    public EditModel(AppDbContext db, SignInService signIn, CurrentUser currentUser, PasswordHasher<User> hasher)
    {
        _db = db;
        _signIn = signIn;
        _currentUser = currentUser;
        _hasher = hasher;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;
    public IReadOnlyList<SelectListItem> RoleOptions { get; private set; } = Array.Empty<SelectListItem>();

    public class InputModel
    {
        public string Username { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public long RoleId { get; set; }
        public bool IsActive { get; set; } = true;
        public string? Password { get; set; }
        public string? ConfirmPassword { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        SetBreadcrumb();
        await LoadRoleOptionsAsync();

        if (IsEdit)
        {
            var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == Id);
            if (user == null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Username = user.Username,
                DisplayName = user.DisplayName,
                Email = user.Email,
                RoleId = user.RoleId,
                IsActive = user.IsActive
            };
        }
        else
        {
            var defaultRole = await _db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Name == "User");
            if (defaultRole != null)
            {
                Input.RoleId = defaultRole.Id;
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetBreadcrumb();
        await LoadRoleOptionsAsync();

        var username = Input.Username?.Trim() ?? string.Empty;
        var displayName = Input.DisplayName?.Trim() ?? string.Empty;
        var email = string.IsNullOrWhiteSpace(Input.Email) ? null : Input.Email.Trim();

        if (string.IsNullOrWhiteSpace(username))
        {
            ModelState.AddModelError("Input.Username", "Username is required.");
        }
        if (string.IsNullOrWhiteSpace(displayName))
        {
            ModelState.AddModelError("Input.DisplayName", "Display name is required.");
        }

        var selectedRole = await _db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == Input.RoleId);
        if (selectedRole == null)
        {
            ModelState.AddModelError("Input.RoleId", "A valid role is required.");
        }

        var usernameTaken = await _db.Users
            .AnyAsync(u => u.Id != Id && u.Username.ToLower() == username.ToLower());
        if (usernameTaken)
        {
            ModelState.AddModelError("Input.Username", "That username is already in use.");
        }

        var passwordProvided = !string.IsNullOrEmpty(Input.Password);
        if (!IsEdit || passwordProvided)
        {
            if (!IsEdit && !passwordProvided)
            {
                ModelState.AddModelError("Input.Password", "A password is required.");
            }
            else if (passwordProvided)
            {
                if (Input.Password!.Length < MinPasswordLength)
                {
                    ModelState.AddModelError("Input.Password", $"Password must be at least {MinPasswordLength} characters.");
                }
                if (Input.Password != Input.ConfirmPassword)
                {
                    ModelState.AddModelError("Input.ConfirmPassword", "Passwords do not match.");
                }
            }
        }

        User? existing = null;
        if (IsEdit)
        {
            existing = await _db.Users.FirstOrDefaultAsync(u => u.Id == Id);
            if (existing == null)
            {
                return NotFound();
            }

            if (!Input.IsActive)
            {
                if (existing.Id == _currentUser.UserId)
                {
                    ModelState.AddModelError(string.Empty, "You cannot deactivate your own account.");
                }
                else if (existing.IsActive && await IsLastActiveAdminAsync(existing))
                {
                    ModelState.AddModelError(string.Empty, "You cannot deactivate the last active administrator.");
                }
            }

            // Prevent demoting the last active administrator out of the Admin role,
            // which would otherwise leave the system with no one able to reach /System.
            if (Input.IsActive && existing.IsActive)
            {
                var adminRole = await _db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Name == "Admin");
                if (adminRole != null
                    && existing.RoleId == adminRole.Id
                    && Input.RoleId != adminRole.Id
                    && await IsLastActiveAdminAsync(existing))
                {
                    ModelState.AddModelError("Input.RoleId", "You cannot change the role of the last active administrator.");
                }
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (IsEdit)
        {
            existing!.Username = username;
            existing.DisplayName = displayName;
            existing.Email = email;
            existing.RoleId = Input.RoleId;
            existing.IsActive = Input.IsActive;
            existing.UpdateDate = DateTime.UtcNow;
            existing.UpdateUserId = _currentUser.UserId;

            if (passwordProvided)
            {
                existing.PasswordHash = _hasher.HashPassword(existing, Input.Password!);
            }

            await _db.SaveChangesAsync();
        }
        else
        {
            try
            {
                await _signIn.CreateUserAsync(
                    username,
                    displayName,
                    email,
                    Input.Password!,
                    selectedRole!.Name,
                    Input.IsActive,
                    _currentUser.UserId);
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError("Input.Username", ex.Message);
                return Page();
            }
        }

        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!IsEdit)
        {
            return RedirectToPage("Index");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == Id);
        if (user == null)
        {
            return NotFound();
        }

        if (user.Id == _currentUser.UserId)
        {
            SetBreadcrumb();
            await LoadRoleOptionsAsync();
            PopulateInputFrom(user);
            ModelState.AddModelError(string.Empty, "You cannot delete your own account.");
            return Page();
        }

        if (user.IsActive && await IsLastActiveAdminAsync(user))
        {
            SetBreadcrumb();
            await LoadRoleOptionsAsync();
            PopulateInputFrom(user);
            ModelState.AddModelError(string.Empty, "You cannot delete the last active administrator.");
            return Page();
        }

        _db.Users.Remove(user);
        await _db.SaveChangesAsync();

        return RedirectToPage("Index");
    }

    private async Task<bool> IsLastActiveAdminAsync(User target)
    {
        var adminRole = await _db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Name == "Admin");
        if (adminRole == null || target.RoleId != adminRole.Id)
        {
            return false;
        }

        var activeAdminCount = await _db.Users
            .CountAsync(u => u.RoleId == adminRole.Id && u.IsActive);

        return activeAdminCount <= 1;
    }

    private void PopulateInputFrom(User user)
    {
        Input = new InputModel
        {
            Username = user.Username,
            DisplayName = user.DisplayName,
            Email = user.Email,
            RoleId = user.RoleId,
            IsActive = user.IsActive
        };
    }

    private async Task LoadRoleOptionsAsync()
    {
        var roles = await _db.Roles.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        RoleOptions = roles
            .Select(r => new SelectListItem(r.Name, r.Id.ToString()))
            .ToList();
    }

    private void SetBreadcrumb()
    {
        ViewData["Breadcrumb"] = IsEdit ? "System / Users / Edit" : "System / Users / New";
    }
}
