using Microsoft.EntityFrameworkCore;
using MatPaper.Data;

namespace MatPaper.Services;

/// <summary>
/// Manages anonymous share links for documents: creating, revoking, listing and
/// resolving them to the underlying document for public access.
/// </summary>
public sealed class ShareLinkService
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;

    public ShareLinkService(AppDbContext db, CurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Creates a new active share link for the given document with an optional
    /// UTC expiry.
    /// </summary>
    public async Task<ShareLink> CreateAsync(long documentId, DateTime? expiresAtUtc, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var userId = _currentUser.UserId;

        var link = new ShareLink
        {
            Token = Guid.NewGuid(),
            DocumentId = documentId,
            ExpiresAt = expiresAtUtc,
            UpdateState = UpdateState.Created,
            CreateDate = now,
            CreateUserId = userId,
            UpdateDate = now,
            UpdateUserId = userId,
        };

        _db.ShareLinks.Add(link);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return link;
    }

    /// <summary>
    /// Marks the given share link as deleted so it can no longer be resolved.
    /// </summary>
    public async Task RevokeAsync(long shareLinkId, CancellationToken ct)
    {
        var link = await _db.ShareLinks
            .FirstOrDefaultAsync(l => l.Id == shareLinkId, ct)
            .ConfigureAwait(false);

        if (link is null)
        {
            return;
        }

        link.UpdateState = UpdateState.Deleted;
        link.UpdateDate = DateTime.UtcNow;
        link.UpdateUserId = _currentUser.UserId;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns all non-deleted share links for the given document, newest first.
    /// </summary>
    public async Task<List<ShareLink>> ListForDocumentAsync(long documentId, CancellationToken ct)
    {
        return await _db.ShareLinks
            .AsNoTracking()
            .Where(l => l.DocumentId == documentId && l.UpdateState != UpdateState.Deleted)
            .OrderByDescending(l => l.CreateDate)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a share token to its document (including storage location) when
    /// the link is active (non-deleted, not expired) and the document is not
    /// deleted; otherwise returns null.
    /// </summary>
    public async Task<Document?> ResolveAsync(Guid token, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var link = await _db.ShareLinks
            .AsNoTracking()
            .Include(l => l.Document!)
                .ThenInclude(d => d.StorageLocation)
            .FirstOrDefaultAsync(
                l => l.Token == token
                    && l.UpdateState != UpdateState.Deleted
                    && (l.ExpiresAt == null || l.ExpiresAt > now),
                ct)
            .ConfigureAwait(false);

        var document = link?.Document;
        if (document is null || document.UpdateState == UpdateState.Deleted)
        {
            return null;
        }

        return document;
    }
}
