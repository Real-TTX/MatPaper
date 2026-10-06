using MatPaper.Data;
using MailKit;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>A mailbox endpoint plus the means to sign in, resolved from a connection or inline settings.</summary>
public sealed record ResolvedMail(
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    ConnectionAuthMode AuthMode,
    string? Password,
    Connection? Connection);

/// <summary>
/// Turns a saved <see cref="Connection"/> (or a task's inline settings) into a usable endpoint and
/// applies its sign-in. Every mail auth site goes through <see cref="AuthenticateAsync"/>, so
/// password and OAuth2 (XOAUTH2) live in one place and the OAuth branch is reused by the import
/// runner, the mailbox browser and the connection test. Every SMB import site goes through
/// <see cref="ResolveSmbAsync"/>.
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
    /// else falls back to the task's inline host/login ("enter manually" in the wizard).
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

        return new ResolvedMail(
            s.Host, s.Port, s.UseSsl, s.Username,
            ConnectionAuthMode.Password, _secrets.Unprotect(s.ProtectedPassword), null);
    }

    /// <summary>
    /// Resolves the share an SMB import reads from. With a saved connection, host and sign-in come
    /// from the connection; the share is the task's own (the wizard browses shares on the
    /// connection's host), falling back to the connection's share. Without one, everything is the
    /// task's inline settings, and <paramref name="plaintextPassword"/> — a password typed into the
    /// wizard but not saved yet — overrides the stored one for a test or browse.
    /// </summary>
    public async Task<SmbConnection> ResolveSmbAsync(SmbImportSettings s, string? plaintextPassword, CancellationToken ct)
    {
        var connection = await LoadAsync(s.ConnectionId, ct).ConfigureAwait(false);
        if (connection is not null)
        {
            var endpoint = TaskSettingsJson.Read<SmbEndpoint>(connection.SettingsJson);
            // The connection's folder only applies to the connection's own share, not to another
            // share the wizard picked on the same host.
            var ownShare = string.IsNullOrWhiteSpace(s.Share) || string.Equals(s.Share, endpoint.Share, StringComparison.OrdinalIgnoreCase);
            return new SmbConnection(
                endpoint.Host,
                ownShare ? endpoint.Share : s.Share,
                connection.Domain,
                connection.Username ?? string.Empty,
                _secrets.Unprotect(connection.ProtectedPassword),
                ownShare ? endpoint.Path ?? string.Empty : string.Empty);
        }

        var password = string.IsNullOrEmpty(plaintextPassword)
            ? _secrets.Unprotect(s.ProtectedPassword)
            : plaintextPassword;
        return new SmbConnection(s.Host, s.Share, s.Domain, s.Username, password);
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
}
