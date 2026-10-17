using MatPaper.Services;
using MatPlatform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatPaper.Pages.Account;

/// <summary>
/// Signs out (a POST, so no prefetched link can do it). After a sign-in through MatIdentity the session there ends too (its end-session
/// endpoint, then back to <c>/signout-callback-oidc</c> and the sign-in page with <c>?signedOut=1</c>); with MatIdentity configured a local
/// session lands on <c>?signedOut=1</c> as well, so the sign-in page does not sign in again at once.
/// </summary>
public class LogoutModel : PageModel
{
    private readonly SignInService _signInService;
    private readonly MatPlatformSettings _platform;

    public LogoutModel(SignInService signInService, MatPlatformSettings platform)
    {
        _signInService = signInService;
        _platform = platform;
    }

    public IActionResult OnGet()
    {
        return RedirectToPage("/Account/Login");
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        // Read before the local sign-out removes the session: only a sign-in through MatIdentity carries it.
        var identityToken = _platform.IdentityEnabled ? await _signInService.GetIdentityTokenAsync(ct) : null;

        await _signInService.SignOutAsync();

        if (identityToken is not null)
        {
            return MatIdentitySignIn.SignOutAtIdentity(HttpContext, identityToken);
        }

        return _platform.IdentityEnabled
            ? Redirect("/Account/Login?signedOut=1")
            : RedirectToPage("/Account/Login");
    }
}
