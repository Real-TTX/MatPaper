using MatPaper.Data;
using MailKit;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>A mailbox endpoint plus the means to sign in, resolved from a connection or legacy settings.</summary>
public sealed record ResolvedMail(
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    ConnectionAuthMode AuthMode,
    string? Password,
    Connection? Connection);

/// <summary>An SMB endpoint plus the sub-folder that acts as the root.</summary>
public sealed record ResolvedSmb(SmbConnection Connection, string? BasePath);

/// <summary>
/// Turns a saved <see cref="Connection"/> (or the legacy inline settings/credential) into a
/// usable endpoint and applies its sign-in. Every mail auth site goes through
/// <see cref="AuthenticateAsync"/>, so password and OAuth2 (XOAUTH2) live in one place and the
/// OAuth branch is reused by the import runner, the mailbox browser and the connection test.
/// </summary>
public sealed class ConnectionService
{
    private readonly AppDbContext _db;
    private readonly SecretProtector _secrets;
    private readonly OAuthService _oauth;

    public ConnectionService(AppDbContext db, SecretProtector secrets, OAuthService oauth)
    {
        _db = db;
        _secrets = secrets;
        _oauth = oauth;
    }

    public async Task<Connection?> LoadAsync(long? connectionId, CancellationToken ct)
    {
        if (connectionId is not long id)
        {
            return null;
        }

        return await _db.Connections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.UpdateState != UpdateState.Deleted, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a mailbox: prefers the connection referenced by <see cref="MailImportSettings.ConnectionId"/>,
    /// else falls back to the legacy inline host/login (or its referenced credential).
    /// </summary>
    public async Task<ResolvedMail> ResolveMailAsync(MailImportSettings s, CancellationToken ct)
    {
        var connection = await LoadAsync(s.ConnectionId, ct).ConfigureAwait(false);
        if (connection is not null)
        {
            var endpoint = TaskSettingsJson.Read<MailEndpoint>(connection.SettingsJson);
            var password = connection.AuthMode == ConnectionAuthMode.Password
                ? _secrets.Unprotect(connection.ProtectedPassword)
                : null;
            return new ResolvedMail(
                endpoint.Host, endpoint.Port, endpoint.UseSsl,
                connection.Username ?? string.Empty,
                connection.AuthMode, password, connection);
        }

        // Legacy: inline settings, optionally a referenced credential.
        var user = s.Username;
        var secret = _secrets.Unprotect(s.ProtectedPassword);
        var cred = await LoadCredentialAsync(s.CredentialId, ct).ConfigureAwait(false);
        if (cred is not null)
        {
            user = cred.Username;
            secret = _secrets.Unprotect(cred.ProtectedPassword);
        }

        return new ResolvedMail(s.Host, s.Port, s.UseSsl, user, ConnectionAuthMode.Password, secret, null);
    }

    /// <summary>Resolves an SMB share: connection first, else legacy inline/credential.</summary>
    public async Task<ResolvedSmb> ResolveSmbAsync(SmbImportSettings s, CancellationToken ct)
    {
        var connection = await LoadAsync(s.ConnectionId, ct).ConfigureAwait(false);
        if (connection is not null)
        {
            var endpoint = TaskSettingsJson.Read<SmbEndpoint>(connection.SettingsJson);
            return new ResolvedSmb(
                new SmbConnection(
                    endpoint.Host, endpoint.Share, connection.Domain,
                    connection.Username ?? string.Empty,
                    _secrets.Unprotect(connection.ProtectedPassword)),
                s.Path);
        }

        var user = s.Username;
        var domain = s.Domain;
        var secret = _secrets.Unprotect(s.ProtectedPassword);
        var cred = await LoadCredentialAsync(s.CredentialId, ct).ConfigureAwait(false);
        if (cred is not null)
        {
            user = cred.Username;
            domain = cred.Domain;
            secret = _secrets.Unprotect(cred.ProtectedPassword);
        }

        return new ResolvedSmb(new SmbConnection(s.Host, s.Share, domain, user, secret), s.Path);
    }

    /// <summary>Signs the (already connected) MailKit client in, using password or XOAUTH2.</summary>
    public async Task AuthenticateAsync(IMailService client, ResolvedMail mail, CancellationToken ct)
    {
        if (mail.AuthMode == ConnectionAuthMode.OAuth2 && mail.Connection is not null)
        {
            var token = await _oauth.GetAccessTokenAsync(mail.Connection, ct).ConfigureAwait(false);
            await client.AuthenticateAsync(new SaslMechanismOAuth2(mail.Username, token), ct).ConfigureAwait(false);
            return;
        }

        await client.AuthenticateAsync(mail.Username, mail.Password ?? string.Empty, ct).ConfigureAwait(false);
    }

    private async Task<Credential?> LoadCredentialAsync(long? credentialId, CancellationToken ct)
    {
        if (credentialId is not long id)
        {
            return null;
        }

        return await _db.Credentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.UpdateState != UpdateState.Deleted, ct)
            .ConfigureAwait(false);
    }
}
