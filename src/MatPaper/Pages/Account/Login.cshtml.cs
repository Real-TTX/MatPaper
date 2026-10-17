using System.ComponentModel.DataAnnotations;
using System.Globalization;
using MatPaper.Services;
using MatPlatform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.Account;

/// <summary>
/// The local sign-in with user name and password. With MatIdentity configured (<c>MATIDENTITY_AUTHORITY</c>) a visit goes straight on to
/// MatIdentity; the local form stays reachable with <c>?local=1</c> (a way in for the administrator when MatIdentity is down), and
/// <c>?signedOut=1</c> / <c>?ssoError=1</c> show it too instead of signing in again at once.
/// After a sign-in through MatIdentity whose e-mail address matches an account that is not linked yet, the page shows <c>?local=1&amp;link=1</c>:
/// a successful sign-in of exactly that account links it (<see cref="MatIdentityPendingLink"/>, <see cref="ExternalSignInService.LinkExternalAsync"/>);
/// another account signs in without linking anything; Cancel deletes the pending link.
/// </summary>
public class LoginModel : PageModel
{
    private readonly SignInService _signInService;
    private readonly ExternalSignInService _externalSignIn;
    private readonly MatPlatformSettings _platform;
    private readonly IStringLocalizer<SharedResource> _l;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(
        SignInService signInService,
        ExternalSignInService externalSignIn,
        MatPlatformSettings platform,
        IStringLocalizer<SharedResource> l,
        ILogger<LoginModel> logger)
    {
        _signInService = signInService;
        _externalSignIn = externalSignIn;
        _platform = platform;
        _l = l;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    /// <summary><c>?local=1</c>: the local form even when MatIdentity is configured.</summary>
    public bool Local { get; private set; }

    /// <summary><c>?signedOut=1</c>: back from the sign-out at MatIdentity, no automatic sign-in.</summary>
    public bool SignedOut { get; private set; }

    /// <summary><c>?ssoError=1</c>: the sign-in at MatIdentity failed or MatIdentity is not reachable.</summary>
    public bool SsoError { get; private set; }

    /// <summary>The pending link to an account of this address, when MatIdentity sent the person here to prove it (<c>?link=1</c>).</summary>
    public MatIdentityPendingLink? PendingLink { get; private set; }

    /// <summary><c>?link=1</c> without a pending link: it expired (10 minutes) or was cancelled.</summary>
    public bool LinkExpired { get; private set; }

    /// <summary>True when the sign-in through MatIdentity is configured.</summary>
    public bool IdentityEnabled => _platform.IdentityEnabled;

    /// <summary>The address that starts the sign-in at MatIdentity and comes back to <see cref="ReturnUrl"/>.</summary>
    public string IdentitySignInUrl => string.IsNullOrWhiteSpace(ReturnUrl)
        ? "/Account/Login"
        : "/Account/Login?returnUrl=" + Uri.EscapeDataString(ReturnUrl);

    /// <summary>The address of the local form, with the same <see cref="ReturnUrl"/>.</summary>
    public string LocalSignInUrl => string.IsNullOrWhiteSpace(ReturnUrl)
        ? "/Account/Login?local=1"
        : "/Account/Login?local=1&returnUrl=" + Uri.EscapeDataString(ReturnUrl);

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;
        Prepare();

        // Auto login: straight on to MatIdentity, no local form in the way.
        if (IdentityEnabled && !Local && !SignedOut && !SsoError)
        {
            try
            {
                await HttpContext.ChallengeAsync(
                    MatIdentitySignIn.Scheme,
                    new AuthenticationProperties { RedirectUri = ResolveReturnUrl() });
                return new EmptyResult();
            }
            catch (InvalidOperationException ex)
            {
                // MatIdentity cannot be reached (its configuration cannot be read): the local form is the way in.
                _logger.LogWarning(ex, "MatIdentity is not reachable, showing the local sign-in");
                Local = true;
                SsoError = true;
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null, CancellationToken ct = default)
    {
        ReturnUrl = returnUrl;
        // A posted form is the local sign-in; on an error it shows the form again.
        Prepare();
        Local = true;

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await _signInService.ValidateCredentialsAsync(Input.Username, Input.Password);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, _l["Invalid username or password."]);
            return Page();
        }

        // A pending link of MatIdentity is only ever for the account that matched; it is deleted either way.
        string? identityToken = null;
        var linked = false;
        if (MatIdentityPendingLink.TakeFor(HttpContext, user.Id.ToString(CultureInfo.InvariantCulture)) is { } pending)
        {
            var link = await _externalSignIn.LinkExternalAsync(
                user.Id,
                new ExternalIdentity(pending.Subject, pending.Email, pending.Name, pending.PreferredUserName, pending.Role),
                ct);

            if (link.User is { } linkedUser)
            {
                user = linkedUser;
                identityToken = pending.IdToken;
                linked = true;
            }
            else
            {
                _logger.LogWarning("User {UserId} signed in, but was not linked with MatIdentity: {Error}", user.Id, link.Error);
            }
        }

        await _signInService.SignInAsync(user, Input.RememberMe, identityToken);

        if (linked)
        {
            this.Notify(_l["Your account is now connected with MatIdentity."].Value);
        }

        return Redirect(ResolveReturnUrl());
    }

    /// <summary>Cancel on <c>?link=1</c>: the account is not linked, the pending link is deleted.</summary>
    public IActionResult OnPostCancelLink(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;
        MatIdentityPendingLink.Clear(HttpContext);
        return Redirect(LocalSignInUrl);
    }

    private void Prepare()
    {
        Local = QueryFlag("local");
        SignedOut = QueryFlag("signedOut");
        SsoError = QueryFlag("ssoError");
        PendingLink = MatIdentityPendingLink.Peek(HttpContext);
        LinkExpired = PendingLink is null && QueryFlag("link");
    }

    /// <summary>A flag in the query: <c>1</c> or <c>true</c>.</summary>
    private bool QueryFlag(string name)
        => Request.Query[name].ToString() is "1" or "true" or "True";

    /// <summary>Only local addresses are accepted, otherwise the start page.</summary>
    private string ResolveReturnUrl()
        => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";

    public class InputModel
    {
        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Username")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "The {0} field is required.")]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Remember me")]
        public bool RememberMe { get; set; }
    }
}
