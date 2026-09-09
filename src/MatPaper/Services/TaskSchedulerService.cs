using System;
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
/// import/export runner, recording a <see cref="TaskRun"/>, and (2) evaluates cron
/// schedules once a minute and enqueues due tasks.
/// </summary>
public class TaskSchedulerService : BackgroundService
{
    private const int MaxLogLength = 20000;
    private static readonly TimeSpan SchedulerInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TaskTriggerQueue _queue;
    private readonly ILogger<TaskSchedulerService> _logger;

    public TaskSchedulerService(
        IServiceScopeFactory scopeFactory,
        TaskTriggerQueue queue,
        ILogger<TaskSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumer = ConsumeAsync(stoppingToken);
        var scheduler = ScheduleAsync(stoppingToken);
        await Task.WhenAll(consumer, scheduler);
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
        ExportTask? exportTask = null;

        if (trigger.Kind == TaskRunKind.Import)
        {
            importTask = await db.ImportTasks
                .FirstOrDefaultAsync(t => t.Id == trigger.TaskId && t.UpdateState != UpdateState.Deleted, ct);
            if (importTask is null)
            {
                _logger.LogWarning("Import task {TaskId} not found or deleted; skipping run.", trigger.TaskId);
                return;
            }
        }
        else
        {
            exportTask = await db.ExportTasks
                .FirstOrDefaultAsync(t => t.Id == trigger.TaskId && t.UpdateState != UpdateState.Deleted, ct);
            if (exportTask is null)
            {
                _logger.LogWarning("Export task {TaskId} not found or deleted; skipping run.", trigger.TaskId);
                return;
            }
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
            if (importTask is not null)
            {
                var runner = scope.ServiceProvider.GetRequiredService<ImportRunner>();
                report = await runner.RunAsync(importTask, ct);
            }
            else
            {
                var runner = scope.ServiceProvider.GetRequiredService<ExportRunner>();
                report = await runner.RunAsync(exportTask!, ct);
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

        var importTasks = await db.ImportTasks
            .Where(t => t.IsEnabled
                && t.UpdateState != UpdateState.Deleted
                && t.CronExpression != null
                && t.CronExpression != "")
            .Select(t => new { t.Id, t.CronExpression, t.CreateDate })
            .ToListAsync(ct);

        foreach (var t in importTasks)
        {
            await EvaluateOneAsync(db, TaskRunKind.Import, t.Id, t.CronExpression!, t.CreateDate, now, ct);
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

        var next = expression.GetNextOccurrence(lastRunUtc, TimeZoneInfo.Utc);
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
