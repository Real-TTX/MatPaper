using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>The numbers of the whole installation at one moment (all owners), for the platform tiles (<c>/api/state</c>).</summary>
public sealed record InstanceSnapshot(
    int Documents,
    int InboxWaiting,
    int OcrQueue,
    int OcrFailed,
    long StorageBytes,
    DateTime? LastDocumentUtc,
    int Users);

/// <summary>
/// What the installation looks like as a whole, past the owner and sharing rules of the pages: how many documents, how many wait in
/// an inbox or for text recognition, how much storage they take, whether the database answers and how many administrators there are.
/// Read by the platform contract (Mattiverse's tiles and health) and nothing else; it changes nothing.
/// </summary>
public sealed class InstanceStatusService(AppDbContext db, ILogger<InstanceStatusService> logger)
{
    /// <summary>
    /// The documents that MatPaper manages: not deleted and not waved away (an ignored document is a file MatPaper only knows of).
    /// <see cref="InstanceSnapshot.InboxWaiting"/> is every inbox together, <see cref="InstanceSnapshot.Users"/> the active accounts.
    /// </summary>
    public async Task<InstanceSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var documents = db.Documents.AsNoTracking()
            .Where(d => d.UpdateState != UpdateState.Deleted && d.ReviewState != ReviewState.Ignored);

        return new InstanceSnapshot(
            Documents: await documents.CountAsync(ct),
            InboxWaiting: await documents.CountAsync(d => d.ReviewState == ReviewState.Pending, ct),
            OcrQueue: await documents.CountAsync(d => d.OcrState == OcrState.Pending, ct),
            OcrFailed: await documents.CountAsync(d => d.OcrState == OcrState.Failed, ct),
            StorageBytes: await documents.SumAsync(d => (long?)d.FileSize, ct) ?? 0,
            LastDocumentUtc: await documents.MaxAsync(d => (DateTime?)d.CreateDate, ct),
            Users: await db.Users.AsNoTracking().CountAsync(u => u.IsActive, ct));
    }

    /// <summary>True when the database answers a simple query.</summary>
    public async Task<bool> CanReadDatabaseAsync(CancellationToken ct = default)
    {
        try
        {
            await db.Roles.AsNoTracking().AnyAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The database cannot be read");
            return false;
        }
    }

    /// <summary>How many active accounts have the role Admin (zero before the first setup).</summary>
    public Task<int> CountActiveAdminsAsync(CancellationToken ct = default)
        => db.Users.AsNoTracking().CountAsync(u => u.IsActive && u.Role!.Name == ExternalSignInService.AdminRole, ct);
}
