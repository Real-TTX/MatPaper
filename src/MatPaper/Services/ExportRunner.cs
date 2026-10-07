using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using MatPaper.Configuration;
using MatPaper.Data;

namespace MatPaper.Services;

/// <summary>
/// Executes <see cref="ExportTask"/>s. Currently every export type produces a full backup:
/// a <c>pg_dump</c> of the database plus (optionally) the configuration directory and the
/// managed document storage, written as a single gzip-compressed tarball into the target
/// path. Old backups beyond the configured retention count are pruned.
/// </summary>
public sealed class ExportRunner
{
    private static readonly TimeSpan PgDumpTimeout = TimeSpan.FromMinutes(10);

    private readonly AppDbContext _db;
    private readonly AppConfig _config;
    private readonly DocumentStorageService _storage;
    private readonly ILogger<ExportRunner> _logger;

    public ExportRunner(AppDbContext db, AppConfig config, DocumentStorageService storage, ILogger<ExportRunner> logger)
    {
        _db = db;
        _config = config;
        _storage = storage;
        _logger = logger;
    }

    public async Task<RunReport> RunAsync(ExportTask task, CancellationToken ct)
    {
        try
        {
            if (task.Type == ExportTaskType.Export)
            {
                return await RunDocumentExportAsync(task, ct);
            }

            var settings = TaskSettingsJson.Read<BackupSettings>(task.SettingsJson);

            if (string.IsNullOrWhiteSpace(settings.TargetPath))
            {
                return new RunReport(false, 0, "Target path not writable");
            }

            if (!TryEnsureDirectory(settings.TargetPath))
            {
                return new RunReport(false, 0, "Target path not writable");
            }

            var tempSqlPath = Path.Combine(Path.GetTempPath(), $"matpaper-dump-{Guid.NewGuid():N}.sql");

            try
            {
                var dump = await RunPgDumpAsync(tempSqlPath, ct);
                if (!dump.Ok)
                {
                    return new RunReport(false, 0, "pg_dump failed: " + dump.Stderr);
                }

                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                var backupPath = Path.Combine(settings.TargetPath, $"matpaper-backup-{timestamp}.tar.gz");

                await WriteBackupArchiveAsync(backupPath, tempSqlPath, settings, ct);

                long bytes = new FileInfo(backupPath).Length;

                PruneOldBackups(settings.TargetPath, settings.Retention);

                return new RunReport(true, 1, $"Backup written to {backupPath} ({bytes} bytes)");
            }
            finally
            {
                TryDelete(tempSqlPath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export task {TaskId} failed.", task.Id);
            return new RunReport(false, 0, ex.Message);
        }
    }

    /// <summary>
    /// Copies the documents that match the rule into the target storage location, each under the
    /// path the rule's template gives it. A copy that already exists is left alone, so every run
    /// only adds what is new.
    /// </summary>
    private async Task<RunReport> RunDocumentExportAsync(ExportTask task, CancellationToken ct)
    {
        var s = TaskSettingsJson.Read<DocumentExportSettings>(task.SettingsJson);
        if (s.TargetLocationId is not long targetId)
        {
            return new RunReport(false, 0, "No target storage location chosen.");
        }

        var target = await _db.StorageLocations.Include(l => l.Connection)
            .FirstOrDefaultAsync(l => l.Id == targetId && l.UpdateState != UpdateState.Deleted, ct);
        if (target is null)
        {
            return new RunReport(false, 0, "The target storage location no longer exists.");
        }

        var query = _db.Documents.AsNoTracking()
            .Include(d => d.StorageLocation).ThenInclude(l => l!.Connection)
            .Include(d => d.Correspondent)
            .Include(d => d.DocumentType)
            .Where(d => d.UpdateState != UpdateState.Deleted && !d.IsStaged && d.RelativePath != "" && d.StorageLocationId != targetId);

        if (s.TagIds.Count > 0) { var tags = s.TagIds; query = query.Where(d => d.DocumentTags.Any(t => tags.Contains(t.TagId))); }
        if (s.CorrespondentId is long cid) { query = query.Where(d => d.CorrespondentId == cid); }
        if (s.DocumentTypeId is long tid) { query = query.Where(d => d.DocumentTypeId == tid); }
        if (s.ProjectId is long pid) { query = query.Where(d => d.ProjectId == pid); }
        if (s.OwnerUserId is long oid) { query = query.Where(d => d.OwnerId == oid); }
        if (s.SourceLocationId is long lid) { query = query.Where(d => d.StorageLocationId == lid); }
        var folder = (s.SourceFolder ?? string.Empty).Trim().Trim('/', '\\').Replace('\\', '/');
        if (folder.Length > 0) { var prefix = folder + "/"; query = query.Where(d => d.RelativePath.StartsWith(prefix)); }

        var documents = await query.OrderBy(d => d.Id).ToListAsync(ct);

        // The template is a storage location's template: same placeholders, same cleaning of names.
        var template = new StorageLocation { PathTemplate = string.IsNullOrWhiteSpace(s.PathTemplate) ? "{Year}/{Title}{Ext}" : s.PathTemplate };
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int copied = 0, existing = 0, failed = 0;
        var problems = new List<string>();

        foreach (var d in documents)
        {
            ct.ThrowIfCancellationRequested();
            var relative = _storage.BuildRelativePath(template, d.Title,
                d.DocumentDate ?? d.FileModifiedUtc ?? d.CreateDate,
                d.Correspondent?.Name, d.DocumentType?.Name, d.OriginalFileName, d.Token);

            // Two documents that end up on the same path: the later one gets its id in the name.
            if (!used.Add(relative))
            {
                var ext = Path.GetExtension(relative);
                relative = relative[..^ext.Length] + " [" + DocumentStorageService.ShortId(d.Token) + "]" + ext;
                used.Add(relative);
            }

            try
            {
                if (await _storage.ExistsAsync(target, relative, ct))
                {
                    existing++;
                    continue;
                }

                await using var source = await _storage.OpenDocumentReadAsync(d, ct);
                await _storage.SaveNewAsync(target, relative, source, ct);
                copied++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Export of document {DocumentId} failed.", d.Id);
                if (problems.Count < 5) { problems.Add($"{d.Title}: {ex.Message}"); }
            }
        }

        var log = $"{documents.Count} document(s) match, {copied} copied, {existing} already there"
            + (failed > 0 ? $", {failed} failed" : "") + $" (target: {target.Name}).";
        if (problems.Count > 0) { log += "\n" + string.Join("\n", problems); }
        return new RunReport(failed == 0, copied, log);
    }

    private async Task<(bool Ok, string Stderr)> RunPgDumpAsync(string tempSqlPath, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pg_dump",
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-h");
        startInfo.ArgumentList.Add(_config.Database.Host);
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(_config.Database.Port.ToString());
        startInfo.ArgumentList.Add("-U");
        startInfo.ArgumentList.Add(_config.Database.Username);
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(_config.Database.Database);
        startInfo.ArgumentList.Add("-F");
        startInfo.ArgumentList.Add("p");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(tempSqlPath);

        startInfo.Environment["PGPASSWORD"] = _config.Database.Password;

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PgDumpTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return (false, "pg_dump timed out");
        }

        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            return (false, string.IsNullOrWhiteSpace(stderr) ? $"exit code {process.ExitCode}" : stderr.Trim());
        }

        return (true, stderr);
    }

    private async Task WriteBackupArchiveAsync(
        string backupPath, string tempSqlPath, BackupSettings settings, CancellationToken ct)
    {
        await using var fileStream = new FileStream(
            backupPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var gzip = new GZipStream(fileStream, CompressionLevel.Optimal);
        await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);

        await tar.WriteEntryAsync(tempSqlPath, "database.sql", ct);

        if (settings.IncludeConfig)
        {
            var configDir = Path.Combine(DataDir, "config");
            await AddDirectoryAsync(tar, configDir, "config", ct);
        }

        if (settings.IncludeDocuments)
        {
            // Documents still in the review inbox live in the local staging area.
            await AddDirectoryAsync(tar, _config.ResolveInboxPath(DataDir), "documents/_inbox", ct);

            var locations = await _db.StorageLocations
                .AsNoTracking()
                .Where(s => s.UpdateState != UpdateState.Deleted)
                .Select(s => new { s.Name, s.RootPath, s.Kind })
                .ToListAsync(ct);

            foreach (var loc in locations)
            {
                if (loc.Kind == StorageKind.Smb)
                {
                    _logger.LogInformation(
                        "Backup: SMB storage location '{Name}' is not included; back it up on the file server.", loc.Name);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(loc.RootPath) || !Directory.Exists(loc.RootPath))
                {
                    continue;
                }

                var prefix = "documents/" + SanitizeSegment(loc.Name);
                await AddDirectoryAsync(tar, loc.RootPath, prefix, ct);
            }
        }
    }

    private async Task AddDirectoryAsync(TarWriter tar, string directory, string entryPrefix, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            string relative;
            try
            {
                relative = Path.GetRelativePath(directory, file).Replace('\\', '/').TrimStart('/');
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not compute archive entry name for '{File}'; skipping.", file);
                continue;
            }

            var entryName = entryPrefix + "/" + relative;

            try
            {
                await tar.WriteEntryAsync(file, entryName, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add '{File}' to backup archive; skipping.", file);
            }
        }
    }

    private void PruneOldBackups(string targetPath, int retention)
    {
        if (retention <= 0)
        {
            return;
        }

        try
        {
            var backups = Directory
                .EnumerateFiles(targetPath, "matpaper-backup-*.tar.gz", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .ToList();

            foreach (var stale in backups.Skip(retention))
            {
                TryDelete(stale);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retention cleanup in '{TargetPath}' failed.", targetPath);
        }
    }

    private static bool TryEnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete '{Path}'.", path);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static string SanitizeSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "location";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray();
        return new string(chars);
    }

    private static string DataDir =>
        Environment.GetEnvironmentVariable("MATPAPER_DATA") ?? "/data";
}
