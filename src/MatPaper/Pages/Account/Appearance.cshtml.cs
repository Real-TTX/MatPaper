using MatPaper.Data;
using MatPaper.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.Account;

/// <summary>
/// Each user's own theme: mode, colour scheme and accent, stored on the account so it follows
/// them to every device. An empty value means "use the instance default" (System, Settings), so
/// a later change of the default still reaches that user. The sidebar's quick mode switch saves
/// through <see cref="OnPostModeAsync"/>.
/// </summary>
public class AppearanceModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;
    private readonly ThemeService _themes;
    private readonly IStringLocalizer<SharedResource> _l;

    public AppearanceModel(AppDbContext db, CurrentUser currentUser, ThemeService themes, IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _currentUser = currentUser;
        _themes = themes;
        _l = l;
    }

    /// <summary>Own mode, or empty for the instance default.</summary>
    [BindProperty]
    public string? Mode { get; set; }

    [BindProperty]
    public string? Scheme { get; set; }

    [BindProperty]
    public string? Accent { get; set; }

    public ThemeChoice InstanceDefault { get; private set; } = default!;

    /// <summary>What the page currently shows (own value or default), for the scheme previews.</summary>
    public ThemeChoice Effective { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var user = await LoadUserAsync(ct);
        if (user is null)
        {
            return Challenge();
        }

        Mode = user.ThemeMode;
        Scheme = user.ThemeScheme;
        Accent = user.ThemeAccent;
        await LoadThemesAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var user = await LoadUserAsync(ct, tracked: true);
        if (user is null)
        {
            return Challenge();
        }

        var mode = Clean(Mode);
        var scheme = Clean(Scheme);
        var accent = Clean(Accent);
        if ((mode is not null && !ThemeCatalog.IsMode(mode))
            || (scheme is not null && !ThemeCatalog.IsScheme(scheme))
            || (accent is not null && !ThemeCatalog.IsAccent(accent)))
        {
            ModelState.AddModelError(string.Empty, _l["Choose an option from the list."]);
            await LoadThemesAsync(ct);
            return Page();
        }

        user.ThemeMode = mode;
        user.ThemeScheme = scheme;
        user.ThemeAccent = accent;
        user.UpdateDate = DateTime.UtcNow;
        user.UpdateUserId = _currentUser.UserId;
        await _db.SaveChangesAsync(ct);

        this.Notify(_l["Appearance saved."].Value);
        return RedirectToPage();
    }

    /// <summary>Saves only the mode — called by the sidebar switch (fetch, no page change).</summary>
    public async Task<IActionResult> OnPostModeAsync(string? mode, CancellationToken ct)
    {
        if (!ThemeCatalog.IsMode(mode))
        {
            return BadRequest();
        }

        var user = await LoadUserAsync(ct, tracked: true);
        if (user is null)
        {
            return Unauthorized();
        }

        user.ThemeMode = mode;
        user.UpdateDate = DateTime.UtcNow;
        user.UpdateUserId = _currentUser.UserId;
        await _db.SaveChangesAsync(ct);
        return new NoContentResult();
    }

    private async Task LoadThemesAsync(CancellationToken ct)
    {
        InstanceDefault = _themes.InstanceDefault();
        Effective = await _themes.ResolveAsync(ct);
        ViewData["Breadcrumb"] = "Appearance";
    }

    private async Task<User?> LoadUserAsync(CancellationToken ct, bool tracked = false)
    {
        if (_currentUser.UserId is not long id)
        {
            return null;
        }

        var query = tracked ? _db.Users : _db.Users.AsNoTracking();
        return await query.FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
