using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.System.Connections;

/// <summary>
/// The redirect target the OAuth provider sends the admin back to after consent. Validates the
/// signed state, exchanges the authorization code for a refresh token and stores it on the
/// connection. Lives under /System so it carries the admin session cookie (top-level GET, Lax).
/// </summary>
public class OAuthCallbackModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly SecretProtector _secrets;
    private readonly OAuthService _oauth;
    private readonly CurrentUser _currentUser;
    private readonly IStringLocalizer<SharedResource> _l;
    private readonly ILogger<OAuthCallbackModel> _logger;

    public OAuthCallbackModel(
        AppDbContext db, SecretProtector secrets, OAuthService oauth,
        CurrentUser currentUser, IStringLocalizer<SharedResource> l, ILogger<OAuthCallbackModel> logger)
    {
        _db = db;
        _secrets = secrets;
        _oauth = oauth;
        _currentUser = currentUser;
        _l = l;
        _logger = logger;
    }

    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error, CancellationToken ct)
    {
        var expectedNonce = TempData["OAuthNonce"] as string;

        if (!string.IsNullOrEmpty(error))
        {
            this.Notify(_l["The provider declined the authorization: {0}", error].Value, NoticeKind.Danger);
            return RedirectToPage("Index");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            this.Notify(_l["The authorization response was incomplete."].Value, NoticeKind.Danger);
            return RedirectToPage("Index");
        }

        EditModel.OAuthState? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<EditModel.OAuthState>(_secrets.Unprotect(state));
        }
        catch
        {
            parsed = null;
        }

        if (parsed is null
            || parsed.ExpiresUtc < DateTime.UtcNow
            || !string.Equals(parsed.Nonce, expectedNonce, StringComparison.Ordinal)
            || parsed.UserId != (_currentUser.UserId ?? 0))
        {
            this.Notify(_l["The authorization could not be verified. Please try again."].Value, NoticeKind.Danger);
            return RedirectToPage("Index");
        }

        var entity = await _db.Connections.FirstOrDefaultAsync(c => c.Id == parsed.ConnectionId && c.UpdateState != UpdateState.Deleted, ct);
        if (entity is null)
        {
            this.Notify(_l["The connection no longer exists."].Value, NoticeKind.Danger);
            return RedirectToPage("Index");
        }

        var redirectUri = _oauth.RedirectUri();
        if (string.IsNullOrEmpty(redirectUri))
        {
            this.Notify(_l["No public base URL is configured."].Value, NoticeKind.Danger);
            return RedirectToPage("Edit", new { id = entity.Id });
        }

        try
        {
            var tokens = await _oauth.ExchangeCodeAsync(entity, code, redirectUri, ct);
            if (string.IsNullOrEmpty(tokens.RefreshToken))
            {
                this.Notify(_l["The provider returned no refresh token. Remove the app's access and connect again."].Value, NoticeKind.Danger);
                return RedirectToPage("Edit", new { id = entity.Id });
            }

            entity.ProtectedRefreshToken = _secrets.Protect(tokens.RefreshToken);
            entity.OAuthScopes = tokens.Scope;
            entity.OAuthConnectedUtc = DateTime.UtcNow;
            entity.UpdateState = UpdateState.Updated;
            entity.UpdateDate = DateTime.UtcNow;
            entity.UpdateUserId = _currentUser.UserId;
            await _db.SaveChangesAsync(ct);
            _oauth.Forget(entity.Id);

            this.Notify(_l["Account connected."].Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OAuth code exchange failed for connection {ConnectionId}.", entity.Id);
            this.Notify(_l["Connecting failed: {0}", ex.Message].Value, NoticeKind.Danger);
        }

        return RedirectToPage("Edit", new { id = entity.Id });
    }
}
