using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;

namespace MatPaper.Pages.System.ImportTasks;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db)
    {
        _db = db;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    public record Row(
        long Id,
        string Name,
        ImportTaskType Type,
        bool IsEnabled,
        string? CronExpression,
        TaskRunState? LastState,
        DateTime? LastStartedAt);

    /// <summary>An import group with the rules in it (top priority first).</summary>
    public record GroupRow(
        long Id,
        string Name,
        bool IsEnabled,
        string? CronExpression,
        TaskRunState? LastState,
        DateTime? LastStartedAt,
        IReadOnlyList<Row> Rules);

    /// <summary>The rows of one table: a group's rules or the single rules.</summary>
    public record RuleTable(IReadOnlyList<Row> Rows, bool InGroup);

    public IReadOnlyList<GroupRow> Groups { get; private set; } = Array.Empty<GroupRow>();
    public IReadOnlyList<Row> Singles { get; private set; } = Array.Empty<Row>();
    public int TotalCount { get; private set; }

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "System / Import tasks";

        var term = string.IsNullOrWhiteSpace(Search) ? null : $"%{Search.Trim()}%";

        var rules = await _db.ImportTasks.AsNoTracking()
            .Where(t => t.UpdateState != UpdateState.Deleted)
            .Where(t => term == null || EF.Functions.ILike(t.Name, term))
            .OrderBy(t => t.Priority).ThenBy(t => t.Name)
            .Select(t => new { t.Id, t.Name, t.Type, t.IsEnabled, t.CronExpression, t.GroupId })
            .ToListAsync();

        var groups = await _db.ImportGroups.AsNoTracking()
            .Where(g => g.UpdateState != UpdateState.Deleted)
            .OrderBy(g => g.Name)
            .Select(g => new { g.Id, g.Name, g.IsEnabled, g.CronExpression })
            .ToListAsync();

        // Latest run per rule and per group.
        var ruleIds = rules.Select(r => r.Id).ToList();
        var ruleRuns = (await _db.TaskRuns.AsNoTracking()
                .Where(r => r.Kind == TaskRunKind.Import && ruleIds.Contains(r.TaskId))
                .Select(r => new { r.TaskId, r.State, r.StartedAt }).ToListAsync())
            .GroupBy(r => r.TaskId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StartedAt).First());
        var groupRuns = (await _db.TaskRuns.AsNoTracking()
                .Where(r => r.Kind == TaskRunKind.ImportGroup)
                .Select(r => new { r.TaskId, r.State, r.StartedAt }).ToListAsync())
            .GroupBy(r => r.TaskId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StartedAt).First());

        Row ToRow(long id, string name, ImportTaskType type, bool enabled, string? cron)
        {
            ruleRuns.TryGetValue(id, out var run);
            return new Row(id, name, type, enabled, cron, run?.State, run?.StartedAt);
        }

        Singles = rules.Where(r => r.GroupId == null)
            .OrderBy(r => r.Name)
            .Select(r => ToRow(r.Id, r.Name, r.Type, r.IsEnabled, r.CronExpression))
            .ToList();

        var result = new List<GroupRow>();
        foreach (var g in groups)
        {
            var members = rules.Where(r => r.GroupId == g.Id)
                .Select(r => ToRow(r.Id, r.Name, r.Type, r.IsEnabled, r.CronExpression))
                .ToList();
            var nameMatches = term == null || (Search is not null && g.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));
            if (members.Count == 0 && !nameMatches)
            {
                continue;
            }

            groupRuns.TryGetValue(g.Id, out var run);
            result.Add(new GroupRow(g.Id, g.Name, g.IsEnabled, g.CronExpression, run?.State, run?.StartedAt, members));
        }

        Groups = result;
        TotalCount = Singles.Count + result.Sum(g => g.Rules.Count);
    }
}
