using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>
/// Data for the "Runs" block shown at the top of a task's editor (import task, export task,
/// storage location): the button that starts it now, what it remembers, and its latest runs.
/// </summary>
/// <param name="Kind">Which kind of task the runs belong to (<c>TaskRun.TaskId</c> is that task's id).</param>
/// <param name="RunAction">URL the "Run now" form posts to.</param>
/// <param name="RunLabel">Label of the run button ("Run now", "Search now").</param>
/// <param name="Memory">One line about what the task remembers between runs, or null.</param>
/// <param name="ResetAction">URL of the "start over" form, or null when there is nothing to forget.</param>
public sealed record TaskRunsPanel(
    TaskRunKind Kind,
    long TaskId,
    string RunAction,
    string RunLabel,
    string? Memory,
    string? ResetAction,
    IReadOnlyList<TaskRun> Runs)
{
    public const int Shown = 8;

    public bool IsRunning => Runs.Count > 0 && Runs[0].State == TaskRunState.Running;

    public static async Task<IReadOnlyList<TaskRun>> LoadRunsAsync(AppDbContext db, TaskRunKind kind, long taskId, CancellationToken ct = default)
        => await db.TaskRuns
            .AsNoTracking()
            .Where(r => r.Kind == kind && r.TaskId == taskId)
            .OrderByDescending(r => r.StartedAt)
            .Take(Shown)
            .ToListAsync(ct);
}
