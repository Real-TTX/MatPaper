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

    /// <summary>"all" or an <see cref="ImportTaskType"/> name.</summary>
    [BindProperty(SupportsGet = true)]
    public string Type { get; set; } = "all";

    /// <summary>"all", "enabled" or "disabled".</summary>
    [BindProperty(SupportsGet = true)]
    public string Status { get; set; } = "all";

    /// <summary>"all", "none" (rules without a group) or the id of a group.</summary>
    [BindProperty(SupportsGet = true)]
    public string Group { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name_asc";

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

    public record GroupOption(long Id, string Name);

    public IReadOnlyList<GroupRow> Groups { get; private set; } = Array.Empty<GroupRow>();
    public IReadOnlyList<Row> Singles { get; private set; } = Array.Empty<Row>();
    public IReadOnlyList<GroupOption> GroupOptions { get; private set; } = Array.Empty<GroupOption>();
    public int TotalCount { get; private set; }

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "System / Import tasks";

        var search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        bool Matches(string name) => search == null || name.Contains(search, StringComparison.OrdinalIgnoreCase);

        IQueryable<ImportTask> ruleQuery = _db.ImportTasks.AsNoTracking()
            .Where(t => t.UpdateState != UpdateState.Deleted);

        var ruleFilter = false;
        if (Enum.TryParse<ImportTaskType>(Type, true, out var type) && Enum.IsDefined(type))
        {
            ruleQuery = ruleQuery.Where(t => t.Type == type);
            ruleFilter = true;
        }

        if (Status == "enabled")
        {
            ruleQuery = ruleQuery.Where(t => t.IsEnabled);
            ruleFilter = true;
        }
        else if (Status == "disabled")
        {
            ruleQuery = ruleQuery.Where(t => !t.IsEnabled);
            ruleFilter = true;
        }

        var rules = await ruleQuery
            .OrderBy(t => t.Priority).ThenBy(t => t.Name)
            .Select(t => new { t.Id, t.Name, t.Type, t.IsEnabled, t.CronExpression, t.GroupId })
            .ToListAsync();

        var allGroups = await _db.ImportGroups.AsNoTracking()
            .Where(g => g.UpdateState != UpdateState.Deleted)
            .OrderBy(g => g.Name)
            .Select(g => new { g.Id, g.Name, g.IsEnabled, g.CronExpression })
            .ToListAsync();
        GroupOptions = allGroups.Select(g => new GroupOption(g.Id, g.Name)).ToList();

        // Which groups and which single rules the group filter lets through.
        var onlySingles = Group == "none";
        var onlyGroup = long.TryParse(Group, out var groupId) ? groupId : (long?)null;
        var groups = allGroups.Where(g => !onlySingles && (onlyGroup == null || g.Id == onlyGroup)).ToList();
        var includeSingles = onlyGroup == null;

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

        // Sorting orders the groups among themselves and the single rules among themselves; the rules inside a
        // group keep the order they run in. What never ran comes last.
        IEnumerable<T> Ordered<T>(IEnumerable<T> items, Func<T, string> name, Func<T, DateTime?> lastRun) => Sort switch
        {
            "name_desc" => items.OrderByDescending(name, StringComparer.CurrentCultureIgnoreCase),
            "run_desc" => items.OrderBy(i => lastRun(i) == null).ThenByDescending(lastRun).ThenBy(name, StringComparer.CurrentCultureIgnoreCase),
            "run_asc" => items.OrderBy(i => lastRun(i) == null).ThenBy(lastRun).ThenBy(name, StringComparer.CurrentCultureIgnoreCase),
            _ => items.OrderBy(name, StringComparer.CurrentCultureIgnoreCase)
        };

        Singles = !includeSingles
            ? Array.Empty<Row>()
            : Ordered(
                    rules.Where(r => r.GroupId == null && Matches(r.Name))
                        .Select(r => ToRow(r.Id, r.Name, r.Type, r.IsEnabled, r.CronExpression)),
                    r => r.Name, r => r.LastStartedAt)
                .ToList();

        var result = new List<GroupRow>();
        foreach (var g in groups)
        {
            // A group whose own name matches the search shows all its rules.
            var nameMatches = Matches(g.Name);
            var members = rules.Where(r => r.GroupId == g.Id && (nameMatches || Matches(r.Name)))
                .Select(r => ToRow(r.Id, r.Name, r.Type, r.IsEnabled, r.CronExpression))
                .ToList();

            // An empty group stays visible unless a search or a rule filter has taken its rules away.
            if (members.Count == 0 && (ruleFilter || !nameMatches))
            {
                continue;
            }

            groupRuns.TryGetValue(g.Id, out var run);
            result.Add(new GroupRow(g.Id, g.Name, g.IsEnabled, g.CronExpression, run?.State, run?.StartedAt, members));
        }

        Groups = Ordered(result, g => g.Name, g => g.LastStartedAt).ToList();
        TotalCount = Singles.Count + Groups.Sum(g => g.Rules.Count);
    }
}
