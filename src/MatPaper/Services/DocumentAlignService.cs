using System.Text;
using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>One document whose file does not lie where the location's path template says it should.</summary>
public sealed record AlignItem(long Id, string Title, string From, string To);

/// <summary>What aligning a location would do: <see cref="Items"/> is the full list, <see cref="Total"/> its length.</summary>
public sealed record AlignPlan(
    StorageLocation Location,
    IReadOnlyList<AlignItem> Items,
    int Checked,
    int FoundSkipped,
    bool IncludeFound)
{
    public int Total => Items.Count;
}

/// <summary>
/// "Align to template": moves the files of a storage location to the path its template produces for their
/// current metadata - for documents filed before the template changed, before a type or correspondent was
/// set, or by an older version. Files MatPaper only found where they lay (storage search) keep their place
/// unless the caller asks for them explicitly. Companion files (metadata, XRechnung XML) move along.
/// </summary>
public sealed class DocumentAlignService
{
    private const int MaxLoggedMoves = 100;
    private const int MaxLoggedFailures = 60;

    private readonly AppDbContext _db;
    private readonly DocumentStorageService _storage;
    private readonly DocumentSidecarService _sidecars;
    private readonly ILogger<DocumentAlignService> _logger;

    public DocumentAlignService(AppDbContext db, DocumentStorageService storage, DocumentSidecarService sidecars, ILogger<DocumentAlignService> logger)
    {
        _db = db;
        _storage = storage;
        _sidecars = sidecars;
        _logger = logger;
    }

    private StringBuilder? _log;

    /// <summary>The run's log so far, readable while it is still going.</summary>
    public string? LogSoFar()
    {
        try { return _log?.ToString(); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Works out what would move. Reads only; nothing is changed.</summary>
    public async Task<AlignPlan?> PlanAsync(long locationId, bool includeFound, CancellationToken ct)
    {
        var location = await _db.StorageLocations.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == locationId && s.UpdateState != UpdateState.Deleted, ct);
        if (location is null)
        {
            return null;
        }

        var documents = await _db.Documents.AsNoTracking()
            .Where(d => d.StorageLocationId == locationId && !d.IsStaged && d.UpdateState != UpdateState.Deleted && d.RelativePath != "")
            .Select(d => new
            {
                d.Id, d.Title, d.OriginalFileName, d.RelativePath, d.DocumentDate, d.FileModifiedUtc, d.CreateDate,
                d.CorrespondentId, d.DocumentTypeId, d.Origin
            })
            .ToListAsync(ct);

        var correspondents = await _db.Correspondents.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var types = await _db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var items = new List<AlignItem>();
        var skippedFound = 0;
        foreach (var d in documents)
        {
            var found = d.Origin == DocumentOrigin.StorageScan;
            var desired = _storage.BuildRelativePath(
                location, d.Title, d.DocumentDate ?? d.FileModifiedUtc ?? d.CreateDate,
                d.CorrespondentId is long cid && correspondents.TryGetValue(cid, out var cn) ? cn : null,
                d.DocumentTypeId is long tid && types.TryGetValue(tid, out var tn) ? tn : null,
                d.OriginalFileName);

            if (string.Equals(desired, d.RelativePath, StringComparison.Ordinal))
            {
                continue;
            }

            if (found && !includeFound)
            {
                skippedFound++;
                continue;
            }

            items.Add(new AlignItem(d.Id, d.Title, d.RelativePath, desired));
        }

        return new AlignPlan(location, items.OrderBy(i => i.From, StringComparer.OrdinalIgnoreCase).ToList(), documents.Count, skippedFound, includeFound);
    }

    /// <summary>Moves every file of the plan. A file that cannot move is logged and left alone; the run goes on.</summary>
    public async Task<RunReport> RunAsync(long locationId, bool includeFound, CancellationToken ct)
    {
        _log = new StringBuilder();
        var plan = await PlanAsync(locationId, includeFound, ct);
        if (plan is null)
        {
            return new RunReport(false, 0, "The storage location is gone.");
        }

        _log.AppendLine($"Aligning \"{plan.Location.Name}\": {plan.Total} of {plan.Checked} file(s) are not where the template puts them.");
        if (plan.FoundSkipped > 0)
        {
            _log.AppendLine($"{plan.FoundSkipped} file(s) found in place were left alone.");
        }

        var location = await _db.StorageLocations.Include(s => s.Connection)
            .FirstAsync(s => s.Id == locationId, ct);

        int moved = 0, failed = 0, unchanged = 0;
        foreach (var chunk in plan.Items.Chunk(50))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var item in chunk)
            {
                try
                {
                    var document = await _db.Documents.FirstOrDefaultAsync(d => d.Id == item.Id && d.UpdateState != UpdateState.Deleted, ct);
                    if (document is null || document.IsStaged || document.StorageLocationId != locationId
                        || !string.Equals(document.RelativePath, item.From, StringComparison.Ordinal))
                    {
                        unchanged++; // changed since the plan was made
                        continue;
                    }

                    var actual = await _storage.MoveAsync(location, item.From, item.To, ct,
                        cleanupEmptyDirectories: document.Origin != DocumentOrigin.StorageScan);
                    if (string.Equals(actual, item.From, StringComparison.Ordinal))
                    {
                        unchanged++;
                        continue;
                    }

                    document.RelativePath = actual;
                    document.UpdateDate = DateTime.UtcNow;
                    try
                    {
                        await _db.SaveChangesAsync(ct);
                    }
                    catch
                    {
                        // keep file and database in step: put the file back
                        await _storage.MoveAsync(location, actual, item.From, CancellationToken.None, cleanupEmptyDirectories: false);
                        throw;
                    }

                    moved++;
                    if (moved <= MaxLoggedMoves) { _log.AppendLine($"Moved: {item.From} -> {actual}"); }
                    else if (moved == MaxLoggedMoves + 1) { _log.AppendLine("… (more moves not listed)"); }
                    await _sidecars.WriteAsync(document, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(ex, "Could not align document {DocumentId} ({From}).", item.Id, item.From);
                    if (failed <= MaxLoggedFailures) { _log.AppendLine($"Failed ({item.From}): {ex.Message}"); }
                }
            }

            _db.ChangeTracker.Clear();
            location = await _db.StorageLocations.Include(s => s.Connection).FirstAsync(s => s.Id == locationId, ct);
        }

        _log.AppendLine($"Done. {moved} moved, {unchanged} unchanged, {failed} failed.");
        return new RunReport(failed == 0, moved, _log.ToString());
    }
}
