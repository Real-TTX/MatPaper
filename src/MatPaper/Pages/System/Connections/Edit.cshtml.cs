using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.System.Connections;

/// <summary>
/// Creates and edits a connection. A password connection needs a username and secret; an OAuth
/// connection needs the self-hoster's own client id and secret, then a one-off "Connect" that
/// runs the provider consent flow and stores a refresh token. Secrets are never rendered back.
/// </summary>
public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly SecretProtector _secrets;
    private readonly OAuthService _oauth;
    private readonly CurrentUser _currentUser;
    private readonly IStringLocalizer<SharedResource> _l;

    public EditModel(AppDbContext db, SecretProtector secrets, OAuthService oauth, CurrentUser currentUser, IStringLocalizer<SharedResource> l)
    {
        _db = db;
        _secrets = secrets;
        _oauth = oauth;
        _currentUser = currentUser;
        _l = l;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;

    /// <summary>Whether this saved connection already holds a refresh token.</summary>
    public bool IsConnected { get; private set; }
    public string? ConnectedAccount { get; private set; }
    public DateTime? ConnectedUtc { get; private set; }

    /// <summary>The redirect URI to register with the provider, or null when no public URL is set.</summary>
    public string? RedirectUri => _oauth.RedirectUri();

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>0 SMB, 1 IMAP, 2 POP3 (int for plain select binding).</summary>
        public int Kind { get; set; } = 1;

        /// <summary>0 Password, 1 OAuth2.</summary>
        public int AuthMode { get; set; }

        /// <summary>0 None, 1 Google, 2 Microsoft.</summary>
        public int Provider { get; set; }

        public string? Host { get; set; }
        public int Port { get; set; } = 993;
        public bool UseSsl { get; set; } = true;
        public string? Share { get; set; }
        public string? Domain { get; set; }

        public string? Username { get; set; }
        public string? Password { get; set; }

        public string? OAuthClientId { get; set; }
        public string? OAuthClientSecret { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        SetBreadcrumb();

        if (IsEdit)
        {
            var entity = await _db.Connections
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == Id && c.UpdateState != UpdateState.Deleted);
            if (entity is null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Name = entity.Name,
                Kind = (int)entity.Kind,
                AuthMode = (int)entity.AuthMode,
                Provider = (int)entity.Provider,
                Username = entity.Username,
                Domain = entity.Domain,
                OAuthClientId = entity.OAuthClientId
                // Password + client secret intentionally left blank.
            };

            switch (entity.Kind)
            {
                case ConnectionKind.Smb:
                    var smb = TaskSettingsJson.Read<SmbEndpoint>(entity.SettingsJson);
                    Input.Host = smb.Host;
                    Input.Share = smb.Share;
                    break;
                default:
                    var mail = TaskSettingsJson.Read<MailEndpoint>(entity.SettingsJson);
                    Input.Host = mail.Host;
                    Input.Port = mail.Port == 0 ? 993 : mail.Port;
                    Input.UseSsl = mail.UseSsl;
                    break;
            }

            IsConnected = entity.IsOAuthConnected;
            ConnectedAccount = entity.Username;
            ConnectedUtc = entity.OAuthConnectedUtc;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetBreadcrumb();

        var entity = await ValidateAndBindAsync();
        if (entity is null)
        {
            await ReloadStatusAsync();
            return Page();
        }

        await _db.SaveChangesAsync();

        // OAuth connections need the consent flow next, so land back on the editor with the
        // Connect button instead of the list.
        if (entity.AuthMode == ConnectionAuthMode.OAuth2 && !entity.IsOAuthConnected)
        {
            this.Notify(_l["Saved. Now press Connect to authorize the account."].Value);
            return RedirectToPage("Edit", new { id = entity.Id });
        }

        return RedirectToPage("Index");
    }

    /// <summary>Saves the draft (if needed) and redirects to the provider's consent screen.</summary>
    public async Task<IActionResult> OnPostConnectAsync()
    {
        SetBreadcrumb();

        var entity = await ValidateAndBindAsync();
        if (entity is null)
        {
            await ReloadStatusAsync();
            return Page();
        }

        if (entity.AuthMode != ConnectionAuthMode.OAuth2)
        {
            ModelState.AddModelError(string.Empty, _l["Connecting only applies to OAuth connections."]);
            await ReloadStatusAsync();
            return Page();
        }

        await _db.SaveChangesAsync();

        var redirectUri = _oauth.RedirectUri();
        if (string.IsNullOrEmpty(redirectUri))
        {
            this.Notify(_l["Set the public base URL in the configuration before connecting."].Value, NoticeKind.Danger);
            return RedirectToPage("Edit", new { id = entity.Id });
        }

        // State ties the callback to this connection, this admin and a short window, and carries
        // a nonce echoed via TempData so a forged callback cannot complete the flow.
        var nonce = Guid.NewGuid().ToString("N");
        var state = _secrets.Protect(JsonSerializer.Serialize(new OAuthState(
            entity.Id, nonce, _currentUser.UserId ?? 0, DateTime.UtcNow.AddMinutes(10))));
        TempData["OAuthNonce"] = nonce;

        return Redirect(_oauth.BuildAuthorizationUrl(entity, state, redirectUri));
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!IsEdit)
        {
            return RedirectToPage("Index");
        }

        var entity = await _db.Connections.FirstOrDefaultAsync(c => c.Id == Id && c.UpdateState != UpdateState.Deleted);
        if (entity is null)
        {
            return NotFound();
        }

        entity.UpdateState = UpdateState.Deleted;
        entity.UpdateDate = DateTime.UtcNow;
        entity.UpdateUserId = _currentUser.UserId;
        await _db.SaveChangesAsync();
        _oauth.Forget(entity.Id);

        return RedirectToPage("Index");
    }

    /// <summary>Validates the form and returns the tracked entity to save, or null on error.</summary>
    private async Task<Connection?> ValidateAndBindAsync()
    {
        var name = Input.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            ModelState.AddModelError("Input.Name", _l["Name is required."]);
        }
        else
        {
            var taken = await _db.Connections.AnyAsync(c =>
                c.Id != Id && c.UpdateState != UpdateState.Deleted && c.Name.ToLower() == name.ToLower());
            if (taken)
            {
                ModelState.AddModelError("Input.Name", _l["A connection with that name already exists."]);
            }
        }

        var kind = (ConnectionKind)Input.Kind;
        var authMode = (ConnectionAuthMode)Input.AuthMode;
        var provider = authMode == ConnectionAuthMode.OAuth2 ? (OAuthProvider)Input.Provider : OAuthProvider.None;

        if (kind == ConnectionKind.Smb && authMode == ConnectionAuthMode.OAuth2)
        {
            ModelState.AddModelError("Input.AuthMode", _l["SMB does not support OAuth."]);
        }

        if (authMode == ConnectionAuthMode.OAuth2 && provider == OAuthProvider.None)
        {
            ModelState.AddModelError("Input.Provider", _l["Choose a provider."]);
        }

        if (string.IsNullOrWhiteSpace(Input.Username))
        {
            ModelState.AddModelError("Input.Username", authMode == ConnectionAuthMode.OAuth2
                ? _l["The account e-mail address is required."]
                : _l["Username is required."]);
        }

        string settingsJson;
        if (kind == ConnectionKind.Smb)
        {
            if (string.IsNullOrWhiteSpace(Input.Host) || string.IsNullOrWhiteSpace(Input.Share))
            {
                ModelState.AddModelError("Input.Host", _l["Host and share are required."]);
            }
            settingsJson = TaskSettingsJson.Write(new SmbEndpoint
            {
                Host = Input.Host?.Trim() ?? string.Empty,
                Share = Input.Share?.Trim() ?? string.Empty
            });
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Input.Host))
            {
                ModelState.AddModelError("Input.Host", _l["Host is required."]);
            }
            settingsJson = TaskSettingsJson.Write(new MailEndpoint
            {
                Host = Input.Host?.Trim() ?? string.Empty,
                Port = Input.Port <= 0 ? 993 : Input.Port,
                UseSsl = Input.UseSsl
            });
        }

        if (authMode == ConnectionAuthMode.Password && !IsEdit && string.IsNullOrEmpty(Input.Password))
        {
            ModelState.AddModelError("Input.Password", _l["A password is required for a new connection."]);
        }

        if (authMode == ConnectionAuthMode.OAuth2)
        {
            if (string.IsNullOrWhiteSpace(Input.OAuthClientId))
            {
                ModelState.AddModelError("Input.OAuthClientId", _l["The client id is required."]);
            }
            if (!IsEdit && string.IsNullOrEmpty(Input.OAuthClientSecret))
            {
                ModelState.AddModelError("Input.OAuthClientSecret", _l["The client secret is required."]);
            }
        }

        if (!ModelState.IsValid)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        Connection entity;
        if (IsEdit)
        {
            entity = await _db.Connections.FirstOrDefaultAsync(c => c.Id == Id && c.UpdateState != UpdateState.Deleted)
                ?? throw new InvalidOperationException("Connection vanished.");
        }
        else
        {
            entity = new Connection { CreateDate = now, CreateUserId = _currentUser.UserId, UpdateState = UpdateState.Created };
            _db.Connections.Add(entity);
        }

        // Changing provider or client invalidates any stored token.
        if (entity.AuthMode == ConnectionAuthMode.OAuth2
            && (entity.Provider != provider || (Input.OAuthClientId?.Trim() ?? string.Empty) != (entity.OAuthClientId ?? string.Empty)))
        {
            entity.ProtectedRefreshToken = string.Empty;
            entity.OAuthConnectedUtc = null;
            _oauth.Forget(entity.Id);
        }

        entity.Name = name;
        entity.Kind = kind;
        entity.AuthMode = authMode;
        entity.Provider = provider;
        entity.SettingsJson = settingsJson;
        entity.Username = Input.Username?.Trim();
        entity.Domain = kind == ConnectionKind.Smb ? (string.IsNullOrWhiteSpace(Input.Domain) ? null : Input.Domain.Trim()) : null;

        if (authMode == ConnectionAuthMode.Password)
        {
            if (!string.IsNullOrEmpty(Input.Password))
            {
                entity.ProtectedPassword = _secrets.Protect(Input.Password);
            }
            entity.OAuthClientId = null;
            entity.ProtectedClientSecret = string.Empty;
            entity.ProtectedRefreshToken = string.Empty;
        }
        else
        {
            entity.OAuthClientId = Input.OAuthClientId?.Trim();
            if (!string.IsNullOrEmpty(Input.OAuthClientSecret))
            {
                entity.ProtectedClientSecret = _secrets.Protect(Input.OAuthClientSecret);
            }
            entity.ProtectedPassword = string.Empty;
        }

        if (IsEdit)
        {
            entity.UpdateState = UpdateState.Updated;
        }
        entity.UpdateDate = now;
        entity.UpdateUserId = _currentUser.UserId;

        Id = entity.Id;
        return entity;
    }

    private async Task ReloadStatusAsync()
    {
        if (!IsEdit)
        {
            return;
        }

        var entity = await _db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == Id);
        IsConnected = entity?.IsOAuthConnected ?? false;
        ConnectedAccount = entity?.Username;
        ConnectedUtc = entity?.OAuthConnectedUtc;
    }

    private void SetBreadcrumb()
    {
        ViewData["Breadcrumb"] = IsEdit ? "System / Connections / Edit" : "System / Connections / New";
        ViewData["Title"] = IsEdit ? Input.Name : null;
    }

    public sealed record OAuthState(long ConnectionId, string Nonce, long UserId, DateTime ExpiresUtc);
}
