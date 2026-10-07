using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>Puts the content of a document on the blocklist (see <see cref="BlockedDocument"/>).</summary>
public static class BlocklistService
{
    /// <summary>Adds the document's content hash (once per owner). Returns false when it has no hash to block.</summary>
    public static async Task<bool> BlockAsync(AppDbContext db, Document document, long? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(document.ContentHash))
        {
            return false;
        }

        var owner = document.OwnerId ?? userId;
        var exists = await db.BlockedDocuments.AnyAsync(b => b.ContentHash == document.ContentHash && b.OwnerId == owner, ct).ConfigureAwait(false);
        if (!exists)
        {
            var now = DateTime.UtcNow;
            db.BlockedDocuments.Add(new BlockedDocument
            {
                ContentHash = document.ContentHash,
                Name = string.IsNullOrWhiteSpace(document.Title) ? document.OriginalFileName : document.Title,
                OriginalFileName = document.OriginalFileName,
                OwnerId = owner,
                CreateDate = now,
                CreateUserId = userId,
                UpdateDate = now,
                UpdateUserId = userId
            });
        }

        return true;
    }
}
