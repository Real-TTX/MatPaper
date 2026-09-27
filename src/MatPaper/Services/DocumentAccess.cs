using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>
/// Central authorization rules for documents. A document is <b>visible</b> to a
/// user if they are an administrator, they own it, it lives in the common area
/// (<see cref="Document.IsCommon"/>), or it has been shared with them. It is
/// <b>editable</b> only by administrators, its owner, or a user it was shared
/// with using <see cref="DocumentShare.CanEdit"/>. Ownership-only actions
/// (delete, manage shares, toggle common) require owner or admin.
///
/// These helpers are the single source of truth so every query and handler
/// enforces the same rule; background services deliberately query without them.
/// </summary>
public static class DocumentAccess
{
    /// <summary>
    /// Restricts a query to the documents the user is allowed to see. Access is
    /// granted directly (owner / common / shared) or by cascade from the document's
    /// project (project owner / common / shared).
    /// </summary>
    public static IQueryable<Document> AccessibleTo(this IQueryable<Document> query, long? userId, bool isAdmin)
    {
        if (isAdmin)
        {
            return query;
        }

        return query.Where(d =>
            (userId != null && d.OwnerId == userId) ||
            d.IsCommon ||
            d.Shares.Any(s => s.UserId == userId && s.UpdateState != UpdateState.Deleted) ||
            (d.Project != null && d.Project.UpdateState != UpdateState.Deleted && (
                d.Project.OwnerId == userId ||
                d.Project.IsCommon ||
                d.Project.Shares.Any(ps => ps.UserId == userId && ps.UpdateState != UpdateState.Deleted))));
    }

    /// <summary>Restricts a query to the documents the user is allowed to see.</summary>
    public static IQueryable<Document> AccessibleTo(this IQueryable<Document> query, CurrentUser user)
        => query.AccessibleTo(user.UserId, user.IsAdmin);

    /// <summary>
    /// Restricts a query to documents that are part of the archive: everything except files
    /// the user ignored and files a storage search merely found and nobody has taken over yet.
    /// Used by the document list, the dashboard and the vocabulary statistics so one search of
    /// a large share cannot flood them.
    /// </summary>
    public static IQueryable<Document> InArchive(this IQueryable<Document> query)
        => query.Where(d =>
            d.ReviewState != ReviewState.Ignored &&
            !(d.Origin == DocumentOrigin.StorageScan && d.ReviewState == ReviewState.Pending));

    /// <summary>
    /// The inbox predicate: documents waiting to be taken over. Normally the user's own;
    /// an administrator can ask for every owner with <paramref name="allOwners"/>.
    /// </summary>
    public static IQueryable<Document> InInboxOf(
        this IQueryable<Document> query, long? userId, bool isAdmin, bool allOwners = false)
        => isAdmin && allOwners
            ? query.Where(d => d.ReviewState == ReviewState.Pending && d.UpdateState != UpdateState.Deleted)
            : query.Where(d => d.OwnerId == userId && d.ReviewState == ReviewState.Pending && d.UpdateState != UpdateState.Deleted);

    /// <summary>Documents the user waved away. Same owner scoping as the inbox.</summary>
    public static IQueryable<Document> IgnoredOf(
        this IQueryable<Document> query, long? userId, bool isAdmin, bool allOwners = false)
        => isAdmin && allOwners
            ? query.Where(d => d.ReviewState == ReviewState.Ignored && d.UpdateState != UpdateState.Deleted)
            : query.Where(d => d.OwnerId == userId && d.ReviewState == ReviewState.Ignored && d.UpdateState != UpdateState.Deleted);

    /// <summary>
    /// True if the user may change the document's metadata — as the document owner,
    /// a document share with edit rights, or an edit share on the document's project.
    /// </summary>
    public static async Task<bool> CanEditAsync(
        AppDbContext db, Document doc, long? userId, bool isAdmin, CancellationToken ct)
    {
        if (isAdmin)
        {
            return true;
        }

        if (userId is null)
        {
            return false;
        }

        if (doc.OwnerId == userId)
        {
            return true;
        }

        var sharedForEdit = await db.DocumentShares.AnyAsync(
            s => s.DocumentId == doc.Id && s.UserId == userId && s.CanEdit && s.UpdateState != UpdateState.Deleted,
            ct);
        if (sharedForEdit)
        {
            return true;
        }

        if (doc.ProjectId is long projectId)
        {
            return await db.Projects.AnyAsync(
                p => p.Id == projectId && p.UpdateState != UpdateState.Deleted &&
                    (p.OwnerId == userId ||
                     p.Shares.Any(ps => ps.UserId == userId && ps.CanEdit && ps.UpdateState != UpdateState.Deleted)),
                ct);
        }

        return false;
    }

    /// <summary>True if the user may perform owner-only actions (delete, manage shares, toggle common).</summary>
    public static bool IsOwnerOrAdmin(Document doc, long? userId, bool isAdmin)
        => isAdmin || (userId != null && doc.OwnerId == userId);

    // ----- Projects -------------------------------------------------------------

    /// <summary>Restricts a query to the projects the user is allowed to see.</summary>
    public static IQueryable<Project> AccessibleTo(this IQueryable<Project> query, long? userId, bool isAdmin)
    {
        if (isAdmin)
        {
            return query;
        }

        return query.Where(p =>
            (userId != null && p.OwnerId == userId) ||
            p.IsCommon ||
            p.Shares.Any(s => s.UserId == userId && s.UpdateState != UpdateState.Deleted));
    }

    /// <summary>Restricts a query to the projects the user is allowed to see.</summary>
    public static IQueryable<Project> AccessibleTo(this IQueryable<Project> query, CurrentUser user)
        => query.AccessibleTo(user.UserId, user.IsAdmin);

    /// <summary>True if the user may edit the project's own documents (owner, or share with edit).</summary>
    public static async Task<bool> CanEditProjectAsync(
        AppDbContext db, Project project, long? userId, bool isAdmin, CancellationToken ct)
    {
        if (isAdmin)
        {
            return true;
        }

        if (userId is null)
        {
            return false;
        }

        if (project.OwnerId == userId)
        {
            return true;
        }

        return await db.ProjectShares.AnyAsync(
            s => s.ProjectId == project.Id && s.UserId == userId && s.CanEdit && s.UpdateState != UpdateState.Deleted,
            ct);
    }

    /// <summary>True if the user may perform owner-only project actions (rename, delete, manage shares, toggle common).</summary>
    public static bool IsProjectOwnerOrAdmin(Project project, long? userId, bool isAdmin)
        => isAdmin || (userId != null && project.OwnerId == userId);
}
