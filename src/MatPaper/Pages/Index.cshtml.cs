using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;

    public IndexModel(AppDbContext db, CurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public int DocumentCount { get; private set; }
    public int Last7DaysCount { get; private set; }
    public int PendingReviewCount { get; private set; }
    public int OcrQueueCount { get; private set; }
    public int FailedTaskCount { get; private set; }

    public IReadOnlyList<RecentDoc> Recent { get; private set; } = Array.Empty<RecentDoc>();
    public IReadOnlyList<TypeCount> TypeBreakdown { get; private set; } = Array.Empty<TypeCount>();
    public IReadOnlyList<RunRow> RecentRuns { get; private set; } = Array.Empty<RunRow>();
    public bool IsAdmin => _currentUser.IsAdmin;

    public int TypeBreakdownMax => TypeBreakdown.Count == 0 ? 1 : TypeBreakdown.Max(t => t.Count);

    public record RecentDoc(long Id, Guid Token, string Title, string? TypeName, string? CorrespondentName, DateTime Date, string? ThumbnailPath);
    public record TypeCount(string Name, int Count);
    public record RunRow(TaskRunKind Kind, string TaskName, TaskRunState State, DateTime StartedAt, int ItemsProcessed);

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "Dashboard";

        var documents = _db.Documents
            .Where(d => d.UpdateState != UpdateState.Deleted)
            .AccessibleTo(_currentUser);
        var since = DateTime.UtcNow.AddDays(-7);
        var uid = _currentUser.UserId;

        DocumentCount = await documents.CountAsync();
        Last7DaysCount = await documents.CountAsync(d => d.CreateDate >= since);
        OcrQueueCount = await documents.CountAsync(d => d.OcrState == OcrState.Pending);
        FailedTaskCount = await documents.CountAsync(d => d.OcrState == OcrState.Failed);
        PendingReviewCount = await _db.Documents.CountAsync(d =>
            d.OwnerId == uid && d.ReviewState == ReviewState.Pending && d.UpdateState != UpdateState.Deleted);

        Recent = await documents
            .AsNoTracking()
            .OrderByDescending(d => d.CreateDate)
            .Take(8)
            .Select(d => new RecentDoc(
                d.Id,
                d.Token,
                d.Title,
                d.DocumentType != null ? d.DocumentType.Name : null,
                d.Correspondent != null ? d.Correspondent.Name : null,
                d.DocumentDate ?? d.CreateDate,
                d.ThumbnailPath))
            .ToListAsync();

        var typeGroups = await documents
            .AsNoTracking()
            .Where(d => d.DocumentTypeId != null)
            .GroupBy(d => d.DocumentType!.Name)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(6)
            .ToListAsync();
        TypeBreakdown = typeGroups.Select(x => new TypeCount(x.Name, x.Count)).ToList();

        if (_currentUser.IsAdmin)
        {
            var runs = await _db.TaskRuns
                .AsNoTracking()
                .OrderByDescending(r => r.StartedAt)
                .Take(5)
                .Select(r => new { r.Kind, r.TaskId, r.State, r.StartedAt, r.ItemsProcessed })
                .ToListAsync();

            var importIds = runs.Where(r => r.Kind == TaskRunKind.Import).Select(r => r.TaskId).ToList();
            var exportIds = runs.Where(r => r.Kind == TaskRunKind.Export).Select(r => r.TaskId).ToList();

            var importNames = await _db.ImportTasks
                .Where(t => importIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Name })
                .ToDictionaryAsync(x => x.Id, x => x.Name);
            var exportNames = await _db.ExportTasks
                .Where(t => exportIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Name })
                .ToDictionaryAsync(x => x.Id, x => x.Name);

            RecentRuns = runs
                .Select(r => new RunRow(
                    r.Kind,
                    (r.Kind == TaskRunKind.Import
                        ? importNames.GetValueOrDefault(r.TaskId)
                        : exportNames.GetValueOrDefault(r.TaskId)) ?? $"#{r.TaskId}",
                    r.State,
                    r.StartedAt,
                    r.ItemsProcessed))
                .ToList();
        }
    }
}
