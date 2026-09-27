using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>
/// Runs queued storage searches one after another in the background. Each request gets its own
/// service scope because <see cref="StorageScanService"/> is scoped, and a failing search never
/// stops the loop — the reason is written to the storage location and shown in the list.
/// </summary>
public sealed class StorageScanWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StorageScanQueue _queue;
    private readonly ILogger<StorageScanWorker> _logger;

    public StorageScanWorker(
        IServiceScopeFactory scopeFactory,
        StorageScanQueue queue,
        ILogger<StorageScanWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(request, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _queue.Release(request.LocationId);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Storage search of location {LocationId} failed.", request.LocationId);
                await RecordFailureAsync(request.LocationId, ex.Message, CancellationToken.None);
            }

            _queue.Release(request.LocationId);
        }
    }

    private async Task RunAsync(ScanRequest request, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var scan = scope.ServiceProvider.GetRequiredService<StorageScanService>();
        await scan.ScanAsync(request.LocationId, request.ActingUserId, ct);
    }

    /// <summary>Stores why a search died so the storage list can show it instead of silence.</summary>
    private async Task RecordFailureAsync(long locationId, string message, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var finishedAt = DateTime.UtcNow;

            await db.StorageLocations
                .Where(s => s.Id == locationId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.LastScanUtc, finishedAt)
                    .SetProperty(s => s.LastScanFound, 0)
                    .SetProperty(s => s.LastScanError, message), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record the search failure for location {LocationId}.", locationId);
        }
    }
}
