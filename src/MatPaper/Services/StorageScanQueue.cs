using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MatPaper.Services;

/// <summary>A request to search one storage location, on behalf of the user who started it.</summary>
public sealed record ScanRequest(long LocationId, long? ActingUserId);

/// <summary>
/// Hand-off between the storage-locations page and <see cref="StorageScanWorker"/>. Searching a
/// NAS folder takes far longer than a web request may, so the page only enqueues. One location
/// can only be queued once at a time: without that guard a double click would insert the same
/// files twice.
/// </summary>
public sealed class StorageScanQueue
{
    private readonly Channel<ScanRequest> _channel =
        Channel.CreateUnbounded<ScanRequest>(new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<long, byte> _inFlight = new();

    public ChannelReader<ScanRequest> Reader => _channel.Reader;

    /// <summary>Queues a search. False when one is already queued or running for that location.</summary>
    public bool TryEnqueue(ScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_inFlight.TryAdd(request.LocationId, 0))
        {
            return false;
        }

        if (_channel.Writer.TryWrite(request))
        {
            return true;
        }

        _inFlight.TryRemove(request.LocationId, out _);
        return false;
    }

    /// <summary>Marks a location as searchable again. Called by the worker when a run ends.</summary>
    public void Release(long locationId) => _inFlight.TryRemove(locationId, out _);

    /// <summary>True while a search for this location is queued or running.</summary>
    public bool IsBusy(long locationId) => _inFlight.ContainsKey(locationId);
}
