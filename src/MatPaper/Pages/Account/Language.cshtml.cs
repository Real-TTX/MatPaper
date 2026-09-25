using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatPaper.Pages.Account;

/// <summary>
/// Stores the picked UI language in the culture cookie and returns to the page the user
/// came from. Anonymous so the language can also be switched on the login screen.
/// </summary>
public class LanguageModel : PageModel
{
    private static readonly string[] Supported = { "de-DE", "en-US" };

    public IActionResult OnPost(string culture, string? returnUrl)
    {
        if (Array.Exists(Supported, c => string.Equals(c, culture, StringComparison.OrdinalIgnoreCase)))
        {
            Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(new CultureInfo(culture))),
                new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true,
                    HttpOnly = false,
                    SameSite = SameSiteMode.Lax
                });
        }

        return LocalRedirect(!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }

    public IActionResult OnGet() => RedirectToPage("/Index");
}
