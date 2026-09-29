namespace MatPaper.Data;

/// <summary>
/// A whole, reusable connection: what kind of endpoint, where it is, and how to sign in.
/// One connection ("my NAS", "my Gmail") is referenced by storage locations and import tasks
/// alike, so it is defined and tested once.
/// <para>
/// The endpoint (host, share, port, root folder) lives in <see cref="SettingsJson"/> because
/// its shape differs per <see cref="Kind"/> and grows with new kinds. The authentication,
/// which is common to every kind and always secret-protected, lives in first-class columns.
/// </para>
/// </summary>
public class Connection : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Which protocol or service carries the bytes.</summary>
    public ConnectionKind Kind { get; set; }

    /// <summary>Password-style sign-in, or an OAuth2 authorization.</summary>
    public ConnectionAuthMode AuthMode { get; set; }

    /// <summary>The OAuth identity provider, when <see cref="AuthMode"/> is OAuth2.</summary>
    public OAuthProvider Provider { get; set; }

    /// <summary>Per-kind endpoint data as tolerant JSON (see the endpoint DTOs).</summary>
    public string SettingsJson { get; set; } = string.Empty;

    // ----- Password auth (also holds the account identity for OAuth) -----

    /// <summary>SMB/IMAP/POP3 login; for OAuth this is the connected account's address.</summary>
    public string? Username { get; set; }

    /// <summary>Windows/SMB domain, SMB only.</summary>
    public string? Domain { get; set; }

    /// <summary>Password or app-password, DataProtection-encrypted. Empty for OAuth.</summary>
    public string ProtectedPassword { get; set; } = string.Empty;

    // ----- OAuth2 auth -----

    /// <summary>OAuth client id the self-hoster registered. Not secret, stored plainly.</summary>
    public string? OAuthClientId { get; set; }

    /// <summary>OAuth client secret, DataProtection-encrypted.</summary>
    public string ProtectedClientSecret { get; set; } = string.Empty;

    /// <summary>OAuth refresh token, DataProtection-encrypted. Set after the consent flow.</summary>
    public string ProtectedRefreshToken { get; set; } = string.Empty;

    /// <summary>Scopes the provider granted, kept for diagnostics.</summary>
    public string? OAuthScopes { get; set; }

    /// <summary>When the account was last connected (UTC). Null = never connected.</summary>
    public DateTime? OAuthConnectedUtc { get; set; }

    public UpdateState UpdateState { get; set; }

    /// <summary>True once an OAuth connection holds a refresh token.</summary>
    public bool IsOAuthConnected => AuthMode == ConnectionAuthMode.OAuth2
        && !string.IsNullOrEmpty(ProtectedRefreshToken);
}
