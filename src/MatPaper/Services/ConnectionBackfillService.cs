using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>
/// One-time, idempotent migration of the old auth-only <see cref="Credential"/> world into the
/// new <see cref="Connection"/> world. Runs once at startup, right after the schema migration
/// and before the app serves requests, so a scheduled task never fires against a not-yet-linked
/// row. Every step skips anything already linked, so re-running is a no-op.
/// <list type="bullet">
/// <item>An SMB <see cref="StorageLocation"/> (its host/share/credential were inline) gets a
/// matching SMB <see cref="Connection"/> and points at it via <see cref="StorageLocation.ConnectionId"/>.</item>
/// <item>An IMAP/POP3/SMB <see cref="ImportTask"/> gets a connection built from its inline
/// endpoint and login, and its settings are rewritten to reference that connection.</item>
/// </list>
/// The legacy columns and the <see cref="Credential"/> table are left in place; a later cleanup
/// migration removes them once this has proven itself in the field.
/// </summary>
public sealed class ConnectionBackfillService
{
    private readonly AppDbContext _db;
    private readonly SecretProtector _secrets;
    private readonly ILogger<ConnectionBackfillService> _logger;

    public ConnectionBackfillService(AppDbContext db, SecretProtector secrets, ILogger<ConnectionBackfillService> logger)
    {
        _db = db;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var created = 0;
        created += await BackfillStorageLocationsAsync(ct);
        created += await BackfillImportTasksAsync(ct);

        if (created > 0)
        {
            _logger.LogInformation("Connection backfill created {Count} connection(s) from legacy data.", created);
        }
    }

    /// <summary>SMB storage locations whose endpoint/credential were inline get a connection.</summary>
    private async Task<int> BackfillStorageLocationsAsync(CancellationToken ct)
    {
        var legacy = await _db.StorageLocations
            .Include(s => s.Credential)
            .Where(s => s.Kind == StorageKind.Smb && s.ConnectionId == null)
            .ToListAsync(ct);

        var created = 0;
        foreach (var loc in legacy)
        {
            var now = DateTime.UtcNow;
            var connection = new Connection
            {
                Name = loc.Name,
                Kind = ConnectionKind.Smb,
                AuthMode = ConnectionAuthMode.Password,
                Provider = OAuthProvider.None,
                SettingsJson = TaskSettingsJson.Write(new SmbEndpoint
                {
                    Host = loc.SmbHost ?? string.Empty,
                    Share = loc.SmbShare ?? string.Empty
                }),
                Username = loc.Credential?.Username ?? string.Empty,
                Domain = loc.Credential?.Domain,
                ProtectedPassword = loc.Credential?.ProtectedPassword ?? string.Empty,
                UpdateState = UpdateState.Created,
                CreateDate = now,
                UpdateDate = now
            };

            _db.Connections.Add(connection);
            await _db.SaveChangesAsync(ct);

            loc.ConnectionId = connection.Id;
            loc.BasePath = loc.SmbPath;
            loc.UpdateDate = now;
            await _db.SaveChangesAsync(ct);
            created++;
        }

        return created;
    }

    /// <summary>Mail and SMB import tasks get a connection built from their inline settings.</summary>
    private async Task<int> BackfillImportTasksAsync(CancellationToken ct)
    {
        var tasks = await _db.ImportTasks
            .Where(t => t.Type == ImportTaskType.Imap
                || t.Type == ImportTaskType.Pop3
                || t.Type == ImportTaskType.Smb)
            .ToListAsync(ct);

        var created = 0;
        foreach (var task in tasks)
        {
            var now = DateTime.UtcNow;

            if (task.Type == ImportTaskType.Smb)
            {
                var s = TaskSettingsJson.Read<SmbImportSettings>(task.SettingsJson);
                if (s.ConnectionId is not null)
                {
                    continue;
                }

                var (user, domain, secret) = await ResolveLoginAsync(s.CredentialId, s.Username, s.Domain, s.ProtectedPassword, ct);
                var connection = new Connection
                {
                    Name = task.Name,
                    Kind = ConnectionKind.Smb,
                    AuthMode = ConnectionAuthMode.Password,
                    Provider = OAuthProvider.None,
                    SettingsJson = TaskSettingsJson.Write(new SmbEndpoint { Host = s.Host, Share = s.Share }),
                    Username = user,
                    Domain = domain,
                    ProtectedPassword = secret,
                    UpdateState = UpdateState.Created,
                    CreateDate = now,
                    UpdateDate = now
                };
                _db.Connections.Add(connection);
                await _db.SaveChangesAsync(ct);

                s.ConnectionId = connection.Id;
                task.SettingsJson = TaskSettingsJson.Write(s);
                task.UpdateDate = now;
                await _db.SaveChangesAsync(ct);
                created++;
            }
            else
            {
                var s = TaskSettingsJson.Read<MailImportSettings>(task.SettingsJson);
                if (s.ConnectionId is not null)
                {
                    continue;
                }

                var (user, _, secret) = await ResolveLoginAsync(s.CredentialId, s.Username, null, s.ProtectedPassword, ct);
                var connection = new Connection
                {
                    Name = task.Name,
                    Kind = task.Type == ImportTaskType.Pop3 ? ConnectionKind.Pop3 : ConnectionKind.Imap,
                    AuthMode = ConnectionAuthMode.Password,
                    Provider = OAuthProvider.None,
                    SettingsJson = TaskSettingsJson.Write(new MailEndpoint { Host = s.Host, Port = s.Port, UseSsl = s.UseSsl }),
                    Username = user,
                    Domain = null,
                    ProtectedPassword = secret,
                    UpdateState = UpdateState.Created,
                    CreateDate = now,
                    UpdateDate = now
                };
                _db.Connections.Add(connection);
                await _db.SaveChangesAsync(ct);

                s.ConnectionId = connection.Id;
                task.SettingsJson = TaskSettingsJson.Write(s);
                task.UpdateDate = now;
                await _db.SaveChangesAsync(ct);
                created++;
            }
        }

        return created;
    }

    /// <summary>Prefers a referenced credential, else the inline login. Returns (user, domain, protected secret).</summary>
    private async Task<(string user, string? domain, string secret)> ResolveLoginAsync(
        long? credentialId, string inlineUser, string? inlineDomain, string inlineSecret, CancellationToken ct)
    {
        if (credentialId is long id)
        {
            var cred = await _db.Credentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
            if (cred is not null)
            {
                return (cred.Username, cred.Domain, cred.ProtectedPassword);
            }
        }

        return (inlineUser, inlineDomain, inlineSecret);
    }
}
