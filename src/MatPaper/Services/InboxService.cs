using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>The review inbox as the signed-in user sees it: the documents waiting to be taken over (see <see cref="DocumentAccess.InInboxOf"/>).</summary>
public sealed class InboxService(AppDbContext db, CurrentUser user)
{
    /// <summary>How many documents wait in the user's inbox. Zero when nobody is signed in, because the layout that shows the number also wraps the login page.</summary>
    public async Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        if (user.UserId is not long uid)
        {
            return 0;
        }

        return await db.Documents.InInboxOf(uid, user.IsAdmin).CountAsync(ct);
    }
}
