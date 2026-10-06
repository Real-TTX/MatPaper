using MatPaper.Data;
using MatPaper.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.Account;

/// <summary>
/// The signed-in user's own account: name and e-mail, password. The rest of "mine" (look)
/// sits on the sibling tab. /Account is open to anonymous visitors for the sign-in pages, so
/// this page asks for authentication itself.
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    private const int MinPasswordLength = 8;

    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;
    private readonly PasswordHasher<User> _hasher;
    private readonly IStringLocalizer<SharedResource> _l;

    public IndexModel(AppDbContext db, CurrentUser currentUser, PasswordHasher<User> hasher, IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _currentUser = currentUser;
        _hasher = hasher;
        _l = l;
    }

    public string Username { get; private set; } = string.Empty;
    public string? RoleName { get; private set; }

    [BindProperty]
    public ProfileInput Profile { get; set; } = new();

    [BindProperty]
    public PasswordInput Password { get; set; } = new();

    public class ProfileInput
    {
        public string DisplayName { get; set; } = string.Empty;
        public string? Email { get; set; }
    }

    public class PasswordInput
    {
        public string? Current { get; set; }
        public string? New { get; set; }
        public string? Confirm { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var user = await LoadAsync(ct);
        if (user is null)
        {
            return Challenge();
        }

        Show(user, keepProfileInput: false);
        return Page();
    }

    public async Task<IActionResult> OnPostProfileAsync(CancellationToken ct)
    {
        var user = await LoadAsync(ct, tracked: true);
        if (user is null)
        {
            return Challenge();
        }

        var name = (Profile.DisplayName ?? string.Empty).Trim();
        var email = string.IsNullOrWhiteSpace(Profile.Email) ? null : Profile.Email.Trim();
        if (name.Length == 0)
        {
            ModelState.AddModelError("Profile.DisplayName", _l["Display name is required."]);
        }
        if (email is not null && !email.Contains('@'))
        {
            ModelState.AddModelError("Profile.Email", _l["Enter a valid e-mail address."]);
        }
        if (!ModelState.IsValid)
        {
            Show(user, keepProfileInput: true);
            return Page();
        }

        user.DisplayName = name;
        user.Email = email;
        user.UpdateDate = DateTime.UtcNow;
        user.UpdateUserId = _currentUser.UserId;
        await _db.SaveChangesAsync(ct);

        this.Notify(_l["Saved"].Value);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPasswordAsync(CancellationToken ct)
    {
        var user = await LoadAsync(ct, tracked: true);
        if (user is null)
        {
            return Challenge();
        }

        if (string.IsNullOrEmpty(Password.Current)
            || _hasher.VerifyHashedPassword(user, user.PasswordHash, Password.Current) == PasswordVerificationResult.Failed)
        {
            ModelState.AddModelError("Password.Current", _l["The current password is wrong."]);
        }
        if (string.IsNullOrEmpty(Password.New) || Password.New.Length < MinPasswordLength)
        {
            ModelState.AddModelError("Password.New", _l["Password must be at least {0} characters.", MinPasswordLength]);
        }
        else if (Password.New != Password.Confirm)
        {
            ModelState.AddModelError("Password.Confirm", _l["Passwords do not match."]);
        }
        if (!ModelState.IsValid)
        {
            Show(user, keepProfileInput: false);
            return Page();
        }

        user.PasswordHash = _hasher.HashPassword(user, Password.New!);
        user.UpdateDate = DateTime.UtcNow;
        user.UpdateUserId = _currentUser.UserId;
        await _db.SaveChangesAsync(ct);

        this.Notify(_l["Password changed."].Value);
        return RedirectToPage();
    }

    private void Show(User user, bool keepProfileInput)
    {
        Username = user.Username;
        RoleName = user.Role?.Name;
        if (!keepProfileInput)
        {
            Profile = new ProfileInput { DisplayName = user.DisplayName, Email = user.Email };
        }
    }

    private async Task<User?> LoadAsync(CancellationToken ct, bool tracked = false)
    {
        if (_currentUser.UserId is not long id)
        {
            return null;
        }

        var query = (tracked ? _db.Users : _db.Users.AsNoTracking()).Include(u => u.Role);
        return await query.FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct);
    }
}
