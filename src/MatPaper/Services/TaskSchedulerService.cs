using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cronos;
using MatPaper.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MatPaper.Services;

/// <summary>
/// Background service that (1) consumes queued task triggers and executes the matching
/// import runner, export runner or storage search, recording a <see cref="TaskRun"/>, and
/// (2) evaluates cron schedules once a minute and enqueues due work. A storage search is a
/// task like any other here, so it gets history, a run log and the same "already running"
/// protection; its <see cref="TaskRun.TaskId"/> is the storage location id.
/// </summary>
public class TaskSchedulerService : BackgroundService
{
    private const int MaxLogLength = 20000;
    private static readonly TimeSpan SchedulerInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TaskTriggerQueue _queue;
    private readonly ILogger<TaskSchedulerService> _logger;

    private readonly Fmt _fmt;

    public TaskSchedulerService(
        IServiceScopeFactory scopeFactory,
        TaskTriggerQueue queue,
        Fmt fmt,
        ILogger<TaskSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _fmt = fmt;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CloseInterruptedRunsAsync(stoppingToken);
        var consumer = ConsumeAsync(stoppingToken);
        var scheduler = ScheduleAsync(stoppingToken);
        await Task.WhenAll(consumer, scheduler);
    }

    /// <summary>
    /// A run that was still going when the application stopped never wrote its end. Close those
    /// at startup, otherwise they stay "running" forever and block the next run of the task.
    /// </summary>
    private async Task CloseInterruptedRunsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var closed = await db.TaskRuns
                .Where(r => r.State == TaskRunState.Running)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.State, TaskRunState.Failed)
                    .SetProperty(r => r.FinishedAt, now)
                    .SetProperty(r => r.UpdateDate, now), ct);
            if (closed > 0)
            {
                _logger.LogWarning("Closed {Count} task run(s) left running by an earlier shutdown.", closed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not close interrupted task runs.");
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var trigger in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await ExecuteTriggerAsync(trigger, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error while executing task trigger {Kind}/{TaskId}",
                        trigger.Kind, trigger.TaskId);
                }
                finally
                {
                    // Every path ends here, including the early returns for a task that is
                    // gone; without this the task would look busy until the next restart.
                    _queue.Release(trigger.Kind, trigger.TaskId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // graceful shutdown
        }
    }

    private async Task ExecuteTriggerAsync(TaskTrigger trigger, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        ImportTask? importTask = null;
        ImportGroup? importGroup = null;
        ExportTask? exportTask = null;
        StorageLocation? location = null;

        switch (trigger.Kind)
        {
            case TaskRunKind.Import:
                // Untracked: a rule in a group runs with the group's target filled in, and that
                // completed copy must never be saved back onto the stored rule.
                var storedRule = await db.ImportTasks.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == trigger.TaskId && t.UpdateState != UpdateState.Deleted, ct);
                if (storedRule is null)
                {
                    _logger.LogWarning("Import task {TaskId} not found or deleted; skipping run.", trigger.TaskId);
                    return;
                }

                var ruleGroup = storedRule.GroupId is long gid
                    ? await db.ImportGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gid && g.UpdateState != UpdateState.Deleted, ct)
                    : null;
                importTask = ImportGroupDefaults.Effective(storedRule, ruleGroup);
                break;

            case TaskRunKind.ImportGroup:
                importGroup = await db.ImportGroups.AsNoTracking()
                    .FirstOrDefaultAsync(g => g.Id == trigger.TaskId && g.UpdateState != UpdateState.Deleted, ct);
                if (importGroup is null)
                {
                    _logger.LogWarning("Import group {GroupId} not found or deleted; skipping run.", trigger.TaskId);
                    return;
                }
                break;

            case TaskRunKind.Export:
                exportTask = await db.ExportTasks
                    .FirstOrDefaultAsync(t => t.Id == trigger.TaskId && t.UpdateState != UpdateState.Deleted, ct);
                if (exportTask is null)
                {
                    _logger.LogWarning("Export task {TaskId} not found or deleted; skipping run.", trigger.TaskId);
                    return;
                }
                break;

            default:
                location = await db.StorageLocations
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == trigger.TaskId && s.UpdateState != UpdateState.Deleted, ct);
                if (location is null)
                {
                    _logger.LogWarning("Storage location {LocationId} not found or deleted; skipping search.", trigger.TaskId);
                    return;
                }
                break;
        }

        var run = new TaskRun
        {
            Kind = trigger.Kind,
            TaskId = trigger.TaskId,
            StartedAt = DateTime.UtcNow,
            State = TaskRunState.Running,
            ItemsProcessed = 0,
            CreateDate = DateTime.UtcNow,
            UpdateDate = DateTime.UtcNow
        };

        db.TaskRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            RunReport report;
            if (importGroup is not null)
            {
                using var progress = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var groupLog = new StringBuilder();
                var flusher = FlushProgressAsync(run.Id, () => { lock (groupLog) { return groupLog.ToString(); } }, progress.Token);
                try
                {
                    report = await RunGroupAsync(importGroup, groupLog, ct);
                }
                finally
                {
                    progress.Cancel();
                    await flusher;
                }
            }
            else if (importTask is not null)
            {
                var runner = scope.ServiceProvider.GetRequiredService<ImportRunner>();
                using var progress = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var flusher = FlushProgressAsync(run.Id, runner.LogSoFar, progress.Token);
                try
                {
                    report = await runner.RunAsync(importTask, ct);
                }
                finally
                {
                    progress.Cancel();
                    await flusher;
                }
            }
            else if (exportTask is not null)
            {
                var runner = scope.ServiceProvider.GetRequiredService<ExportRunner>();
                report = await runner.RunAsync(exportTask, ct);
            }
            else if (trigger.Kind == TaskRunKind.Align)
            {
                // Its own scope on purpose: the alignment clears its change tracker between batches,
                // which would detach the TaskRun this method is holding.
                using var alignScope = _scopeFactory.CreateScope();
                var align = alignScope.ServiceProvider.GetRequiredService<DocumentAlignService>();
                using var progress = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var flusher = FlushProgressAsync(run.Id, align.LogSoFar, progress.Token);
                try
                {
                    report = await align.RunAsync(location!.Id, trigger.Options == "found", ct);
                }
                finally
                {
                    progress.Cancel();
                    await flusher;
                }
            }
            else
            {
                // Its own scope on purpose: the search clears its change tracker between
                // chunks, which would detach the TaskRun this method is holding.
                using var scanScope = _scopeFactory.CreateScope();
                var scan = scanScope.ServiceProvider.GetRequiredService<StorageScanService>();
                var result = await scan.ScanAsync(location!.Id, trigger.ActingUserId, ct);
                report = ScanReport(location!, result);
            }

            run.State = report.Success ? TaskRunState.Success : TaskRunState.Failed;
            run.ItemsProcessed = report.ItemsProcessed;
            run.Log = Truncate(report.Log);
        }
        catch (Exception ex)
        {
            run.State = TaskRunState.Failed;
            run.Log = Truncate(ex.ToString());
            _logger.LogError(ex, "Task run {Kind}/{TaskId} failed.", trigger.Kind, trigger.TaskId);
        }
        finally
        {
            run.FinishedAt = DateTime.UtcNow;
            run.UpdateDate = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Runs the rules of an import group one after the other, top priority first. Every rule records its
    /// own run as well (so its page shows it), and a rule that fails does not stop the ones after it.
    /// </summary>
    private async Task<RunReport> RunGroupAsync(ImportGroup group, StringBuilder log, CancellationToken ct)
    {
        List<ImportTask> rules;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            rules = await db.ImportTasks.AsNoTracking()
                .Where(t => t.GroupId == group.Id && t.UpdateState != UpdateState.Deleted)
                .OrderBy(t => t.Priority).ThenBy(t => t.Id)
                .ToListAsync(ct);
        }

        void Say(string line) { lock (log) { log.AppendLine(line); } }

        Say($"Group \"{group.Name}\": {rules.Count} rule(s), highest priority first.");
        int items = 0, failed = 0, ran = 0, n = 0;
        foreach (var rule in rules)
        {
            ct.ThrowIfCancellationRequested();
            n++;
            if (!rule.IsEnabled)
            {
                Say($"{n}. {rule.Name}: skipped (switched off).");
                continue;
            }

            if (_queue.IsBusy(TaskRunKind.Import, rule.Id))
            {
                Say($"{n}. {rule.Name}: skipped (already running).");
                continue;
            }

            Say($"{n}. {rule.Name} ...");
            using var ruleScope = _scopeFactory.CreateScope();
            var ruleDb = ruleScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var runner = ruleScope.ServiceProvider.GetRequiredService<ImportRunner>();
            var ruleRun = new TaskRun
            {
                Kind = TaskRunKind.Import,
                TaskId = rule.Id,
                StartedAt = DateTime.UtcNow,
                State = TaskRunState.Running,
                CreateDate = DateTime.UtcNow,
                UpdateDate = DateTime.UtcNow
            };
            ruleDb.TaskRuns.Add(ruleRun);
            await ruleDb.SaveChangesAsync(ct);

            using var progress = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var flusher = FlushProgressAsync(ruleRun.Id, runner.LogSoFar, progress.Token);
            RunReport report;
            try
            {
                report = await runner.RunAsync(ImportGroupDefaults.Effective(rule, group), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rule {RuleId} of group {GroupId} failed.", rule.Id, group.Id);
                report = new RunReport(false, 0, ex.ToString());
            }
            finally
            {
                progress.Cancel();
                await flusher;
            }

            ruleRun.State = report.Success ? TaskRunState.Success : TaskRunState.Failed;
            ruleRun.ItemsProcessed = report.ItemsProcessed;
            ruleRun.Log = Truncate(report.Log);
            ruleRun.FinishedAt = DateTime.UtcNow;
            ruleRun.UpdateDate = DateTime.UtcNow;
            await ruleDb.SaveChangesAsync(ct);

            ran++;
            items += report.ItemsProcessed;
            if (!report.Success) { failed++; }
            Say($"   {(report.Success ? "ok" : "FAILED")}: {report.ItemsProcessed} document(s) imported.");
        }

        Say($"Done. {ran} rule(s) ran, {items} document(s) imported, {failed} failed.");
        string text;
        lock (log) { text = log.ToString(); }
        return new RunReport(failed == 0, items, text);
    }

    /// <summary>While an import runs, copies its log into the run every few seconds so the page can show progress.</summary>
    private async Task FlushProgressAsync(long runId, Func<string?> logSoFar, CancellationToken ct)
    {
        string? last = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                var text = logSoFar();
                if (text is null || text == last)
                {
                    continue;
                }

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var snapshot = Truncate(text);
                await db.TaskRuns.Where(r => r.Id == runId && r.State == TaskRunState.Running)
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.Log, snapshot), CancellationToken.None);
                last = text;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not store the progress of run {RunId}.", runId);
        }
    }

    /// <summary>Turns a search result into the same report shape the runners produce.</summary>
    private static RunReport ScanReport(StorageLocation location, StorageScanService.ScanResult result)
    {
        if (result.LocationMissing)
        {
            return new RunReport(false, 0, $"Storage location \"{location.Name}\" is gone.");
        }

        if (result.Failure is not null)
        {
            return new RunReport(false, 0, $"Storage location \"{location.Name}\" could not be searched: {result.Failure}");
        }

        return new RunReport(true, result.Added,
            $"Searched \"{location.Name}\": {result.Scanned} file(s) checked, {result.Added} new, {result.Skipped} already known.");
    }

    private async Task ScheduleAsync(CancellationToken ct)
    {
        // Small startup delay so the app can finish initializing before the first pass.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EvaluateSchedulesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while evaluating task schedules.");
            }

            try
            {
                await Task.Delay(SchedulerInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task EvaluateSchedulesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        // A rule in a group runs when its group runs; only rules on their own keep a schedule of their own.
        var importTasks = await db.ImportTasks
            .Where(t => t.IsEnabled
                && t.GroupId == null
                && t.UpdateState != UpdateState.Deleted
                && t.CronExpression != null
                && t.CronExpression != "")
            .Select(t => new { t.Id, t.CronExpression, t.CreateDate })
            .ToListAsync(ct);

        foreach (var t in importTasks)
        {
            await EvaluateOneAsync(db, TaskRunKind.Import, t.Id, t.CronExpression!, t.CreateDate, now, ct);
        }

        var importGroups = await db.ImportGroups
            .Where(g => g.IsEnabled
                && g.UpdateState != UpdateState.Deleted
                && g.CronExpression != null
                && g.CronExpression != "")
            .Select(g => new { g.Id, g.CronExpression, g.CreateDate })
            .ToListAsync(ct);

        foreach (var g in importGroups)
        {
            await EvaluateOneAsync(db, TaskRunKind.ImportGroup, g.Id, g.CronExpression!, g.CreateDate, now, ct);
        }

        var exportTasks = await db.ExportTasks
            .Where(t => t.IsEnabled
                && t.UpdateState != UpdateState.Deleted
                && t.CronExpression != null
                && t.CronExpression != "")
            .Select(t => new { t.Id, t.CronExpression, t.CreateDate })
            .ToListAsync(ct);

        foreach (var t in exportTasks)
        {
            await EvaluateOneAsync(db, TaskRunKind.Export, t.Id, t.CronExpression!, t.CreateDate, now, ct);
        }

        var locations = await db.StorageLocations
            .Where(s => s.UpdateState != UpdateState.Deleted
                && s.ScanCron != null
                && s.ScanCron != "")
            .Select(s => new { s.Id, s.ScanCron, s.CreateDate })
            .ToListAsync(ct);

        foreach (var s in locations)
        {
            await EvaluateOneAsync(db, TaskRunKind.Scan, s.Id, s.ScanCron!, s.CreateDate, now, ct);
        }
    }

    private async Task EvaluateOneAsync(
        AppDbContext db,
        TaskRunKind kind,
        long taskId,
        string cron,
        DateTime createDateUtc,
        DateTime nowUtc,
        CancellationToken ct)
    {
        CronExpression expression;
        try
        {
            expression = CronExpression.Parse(cron);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invalid cron expression '{Cron}' for {Kind} task {TaskId}; skipping.",
                cron, kind, taskId);
            return;
        }

        var lastStarted = await db.TaskRuns
            .Where(r => r.Kind == kind && r.TaskId == taskId)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => (DateTime?)r.StartedAt)
            .FirstOrDefaultAsync(ct);

        var lastRunUtc = DateTime.SpecifyKind(lastStarted ?? createDateUtc, DateTimeKind.Utc);

        // Schedules are wall-clock times in the configured zone ("daily at 08:00" is
        // 08:00 local, not UTC), so DST shifts are handled by Cronos.
        var next = expression.GetNextOccurrence(lastRunUtc, _fmt.TimeZone);
        if (next is not null && next.Value <= nowUtc)
        {
            // Skip if a run for this task is still in progress, so a task whose runtime
            // exceeds the scheduler interval (e.g. a large backup) does not pile up
            // queued triggers on every tick.
            var isRunning = await db.TaskRuns
                .AnyAsync(r => r.Kind == kind && r.TaskId == taskId && r.State == TaskRunState.Running, ct);
            if (!isRunning)
            {
                _queue.Enqueue(kind, taskId);
            }
        }
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value.Length <= MaxLogLength ? value : value[..MaxLogLength];
    }
}
