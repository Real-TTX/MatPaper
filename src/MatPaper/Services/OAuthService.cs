using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using MatPaper.Configuration;
using MatPaper.Data;

namespace MatPaper.Services;

/// <summary>Endpoints and scopes for one OAuth identity provider.</summary>
public sealed record OAuthProviderInfo(
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string MailScopes,
    string DriveScopes,
    string ExtraAuthParams);

/// <summary>Result of exchanging or refreshing an OAuth authorization.</summary>
public sealed record OAuthTokens(
    string AccessToken,
    string? RefreshToken,
    DateTime ExpiresAtUtc,
    string? Scope);

/// <summary>Raised when a refresh token is rejected (revoked, or expired in Google "testing").</summary>
public sealed class OAuthReconnectRequiredException : Exception
{
    public OAuthReconnectRequiredException(string message) : base(message) { }
}

/// <summary>
/// Runs the OAuth2 authorization-code flow for mail and cloud-file connections. There is no
/// shared MatPaper OAuth app: each self-hoster registers their own Google/Azure app and stores
/// its client id and secret on the <see cref="Connection"/>. This service builds the consent
/// URL, exchanges the code for a refresh token, and mints short-lived access tokens on demand.
/// It is headless-safe: token refresh works from the stored refresh token without any HttpContext,
/// so scheduled imports and storage searches can authenticate.
/// </summary>
public sealed class OAuthService
{
    private const string CallbackPath = "/System/Connections/OAuthCallback";

    private static readonly IReadOnlyDictionary<OAuthProvider, OAuthProviderInfo> Providers =
        new Dictionary<OAuthProvider, OAuthProviderInfo>
        {
            [OAuthProvider.Google] = new(
                "https://accounts.google.com/o/oauth2/v2/auth",
                "https://oauth2.googleapis.com/token",
                MailScopes: "https://mail.google.com/ openid email",
                DriveScopes: "https://www.googleapis.com/auth/drive openid email",
                // offline + consent so Google actually returns a refresh token.
                ExtraAuthParams: "access_type=offline&prompt=consent"),
            [OAuthProvider.Microsoft] = new(
                "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
                "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                MailScopes: "offline_access openid email https://outlook.office365.com/IMAP.AccessAsUser.All https://outlook.office365.com/POP.AccessAsUser.All",
                DriveScopes: "offline_access openid email Files.ReadWrite.All",
                ExtraAuthParams: "prompt=consent"),
        };

    private readonly IHttpClientFactory _httpFactory;
    private readonly SecretProtector _secrets;
    private readonly AppConfig _config;
    private readonly ILogger<OAuthService> _logger;

    // access token cache keyed by connection id; refreshed shortly before expiry.
    private readonly ConcurrentDictionary<long, OAuthTokens> _accessCache = new();

    public OAuthService(IHttpClientFactory httpFactory, SecretProtector secrets, AppConfig config, ILogger<OAuthService> logger)
    {
        _httpFactory = httpFactory;
        _secrets = secrets;
        _config = config;
        _logger = logger;
    }

    public static OAuthProviderInfo? Info(OAuthProvider provider) =>
        Providers.TryGetValue(provider, out var info) ? info : null;

    /// <summary>The redirect URI to register with the provider, or null if no public URL is configured.</summary>
    public string? RedirectUri()
    {
        var baseUrl = _config.ResolvePublicBaseUrl();
        return baseUrl is null ? null : baseUrl + CallbackPath;
    }

    /// <summary>Scopes appropriate for a connection's kind.</summary>
    public string ScopesFor(Connection c)
    {
        var info = Info(c.Provider);
        if (info is null)
        {
            return string.Empty;
        }

        return c.Kind is ConnectionKind.GoogleDrive or ConnectionKind.OneDrive
            ? info.DriveScopes
            : info.MailScopes;
    }

    /// <summary>Builds the provider consent URL the "Connect" button sends the admin to.</summary>
    public string BuildAuthorizationUrl(Connection c, string state, string redirectUri)
    {
        var info = Info(c.Provider) ?? throw new InvalidOperationException("Unsupported OAuth provider.");
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = c.OAuthClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = ScopesFor(c),
            ["state"] = state,
        };

        var q = string.Join("&", query
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));

        return $"{info.AuthorizationEndpoint}?{q}&{info.ExtraAuthParams}";
    }

    /// <summary>Exchanges the authorization code for tokens after the user granted consent.</summary>
    public async Task<OAuthTokens> ExchangeCodeAsync(Connection c, string code, string redirectUri, CancellationToken ct)
    {
        var info = Info(c.Provider) ?? throw new InvalidOperationException("Unsupported OAuth provider.");
        var form = new Dictionary<string, string>
        {
            ["client_id"] = c.OAuthClientId ?? string.Empty,
            ["client_secret"] = _secrets.Unprotect(c.ProtectedClientSecret),
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        };

        return await PostTokenAsync(info.TokenEndpoint, form, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a valid access token for the connection, refreshing from the stored refresh token
    /// when the cached one is missing or about to expire. Throws
    /// <see cref="OAuthReconnectRequiredException"/> when the refresh token is no longer accepted.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(Connection c, CancellationToken ct)
    {
        if (_accessCache.TryGetValue(c.Id, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow.AddSeconds(60))
        {
            return cached.AccessToken;
        }

        var info = Info(c.Provider) ?? throw new InvalidOperationException("Unsupported OAuth provider.");
        var refreshToken = _secrets.Unprotect(c.ProtectedRefreshToken);
        if (string.IsNullOrEmpty(refreshToken))
        {
            throw new OAuthReconnectRequiredException($"Connection '{c.Name}' is not connected. Open it and press Connect.");
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = c.OAuthClientId ?? string.Empty,
            ["client_secret"] = _secrets.Unprotect(c.ProtectedClientSecret),
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        };

        OAuthTokens tokens;
        try
        {
            tokens = await PostTokenAsync(info.TokenEndpoint, form, ct).ConfigureAwait(false);
        }
        catch (OAuthReconnectRequiredException)
        {
            _accessCache.TryRemove(c.Id, out _);
            throw;
        }

        _accessCache[c.Id] = tokens;
        return tokens.AccessToken;
    }

    /// <summary>Drops a cached access token, e.g. after the connection was re-connected or deleted.</summary>
    public void Forget(long connectionId) => _accessCache.TryRemove(connectionId, out _);

    private async Task<OAuthTokens> PostTokenAsync(string endpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient();
        using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode || payload is null || string.IsNullOrEmpty(payload.AccessToken))
        {
            var error = payload?.Error ?? response.StatusCode.ToString();
            if (string.Equals(error, "invalid_grant", StringComparison.OrdinalIgnoreCase))
            {
                throw new OAuthReconnectRequiredException(
                    "The saved authorization is no longer valid. Re-connect the account. " +
                    "For Google, the Cloud project must be published as \"In production\", " +
                    "otherwise the authorization expires after seven days.");
            }

            _logger.LogWarning("OAuth token request to {Endpoint} failed: {Error}", endpoint, error);
            throw new InvalidOperationException($"OAuth token request failed: {error}");
        }

        var expires = DateTime.UtcNow.AddSeconds(payload.ExpiresIn > 0 ? payload.ExpiresIn : 3600);
        return new OAuthTokens(payload.AccessToken!, payload.RefreshToken, expires, payload.Scope);
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}
