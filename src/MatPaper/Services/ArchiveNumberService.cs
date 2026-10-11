using MatPaper.Configuration;
using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>
/// The archive serial numbers (ASN) of the paper originals: which number comes next, whether one is taken, and what
/// has been printed on labels so far. A number belongs to one document that is not deleted; a deleted document gives
/// it up (it can be given again by hand), but the next free number is always behind everything that was ever used.
/// </summary>
public sealed class ArchiveNumberService(AppDbContext db, AppConfig config, ILogger<ArchiveNumberService> logger)
{
    /// <summary>
    /// The number to give next: one behind the highest number ever given to a document (deleted ones included, their
    /// sticker may still be on a sheet in a folder) and behind the labels printed so far (those stickers are on paper
    /// whose scan has not arrived yet).
    /// </summary>
    public async Task<long> NextFreeAsync(CancellationToken ct)
    {
        var used = await db.Documents.AsNoTracking()
            .Where(d => d.ArchiveSerialNumber != null)
            .MaxAsync(d => d.ArchiveSerialNumber, ct).ConfigureAwait(false) ?? 0;
        var printed = config.Barcodes?.LastPrintedAsn ?? 0;
        return Math.Max(used, printed) + 1;
    }

    /// <summary>The number the labels were printed up to (0 = no label has been printed).</summary>
    public long LastPrinted => config.Barcodes?.LastPrintedAsn ?? 0;

    /// <summary>The document that has the number, or null when it is free. <paramref name="exceptDocumentId"/> is the one that asks.</summary>
    public Task<long?> HolderAsync(long number, long? exceptDocumentId, CancellationToken ct)
        => db.Documents.AsNoTracking()
            .Where(d => d.ArchiveSerialNumber == number && d.UpdateState != UpdateState.Deleted && d.Id != exceptDocumentId)
            .Select(d => (long?)d.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>Remembers that labels were printed up to this number. Never goes down; a config file that cannot be written is logged, not fatal.</summary>
    public void NotePrinted(long last)
    {
        var barcodes = config.Barcodes ??= new BarcodeConfig();
        if (last <= barcodes.LastPrintedAsn)
        {
            return;
        }

        barcodes.LastPrintedAsn = last;
        try
        {
            AppConfigLoader.Save(AppInfo.DataDir, config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remember the last printed archive number in {ConfigPath}.", AppConfigLoader.ConfigPath(AppInfo.DataDir));
        }
    }
}
