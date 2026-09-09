using System.Text;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Search;
using MailKit.Security;
using MatPaper.Data;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MatPaper.Services;

/// <summary>
/// Executes an <see cref="ImportTask"/>: pulls documents from a watched folder or a
/// mailbox (IMAP/POP3) and feeds each file into <see cref="DocumentIngestService"/>.
/// </summary>
public sealed class ImportRunner
{
    private readonly AppDbContext _db;
    private readonly DocumentIngestService _ingest;
    private readonly SecretProtector _secrets;
    private readonly HtmlToPdfConverter _htmlToPdf;
    private readonly ILogger<ImportRunner> _logger;

    public ImportRunner(
        AppDbContext db,
        DocumentIngestService ingest,
        SecretProtector secrets,
        HtmlToPdfConverter htmlToPdf,
        ILogger<ImportRunner> logger)
    {
        _db = db;
        _ingest = ingest;
        _secrets = secrets;
        _htmlToPdf = htmlToPdf;
        _logger = logger;
    }

    public Task<RunReport> RunAsync(ImportTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task.Type switch
        {
            ImportTaskType.Filesystem => RunFilesystemAsync(task, ct),
            ImportTaskType.Imap => RunMailAsync(task, isPop3: false, ct),
            ImportTaskType.Pop3 => RunMailAsync(task, isPop3: true, ct),
            _ => Task.FromResult(new RunReport(false, 0, $"Unsupported import type '{task.Type}'."))
        };
    }

    // ----- Filesystem -------------------------------------------------------

    private async Task<RunReport> RunFilesystemAsync(ImportTask task, CancellationToken ct)
    {
        var settings = TaskSettingsJson.Read<FilesystemImportSettings>(task.SettingsJson);

        if (string.IsNullOrWhiteSpace(settings.SourcePath) || !Directory.Exists(settings.SourcePath))
        {
            return new RunReport(false, 0, "Source folder not found");
        }

        var locationId = await ResolveStorageLocationIdAsync(settings.StorageLocationId, ct).ConfigureAwait(false);
        if (locationId is null)
        {
            return new RunReport(false, 0, "No storage location configured");
        }

        var log = new StringBuilder();
        var count = 0;

        var searchOption = settings.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var pattern = string.IsNullOrWhiteSpace(settings.Pattern) ? "*" : settings.Pattern;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(settings.SourcePath, pattern, searchOption);
        }
        catch (Exception ex)
        {
            return new RunReport(false, 0, $"Could not enumerate source folder: {ex.Message}");
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(file);
            try
            {
                IngestResult result;
                await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    result = await _ingest.IngestAsync(
                        stream,
                        fileName,
                        locationId.Value,
                        settings.CorrespondentId,
                        settings.DocumentTypeId,
                        settings.ProjectId,
                        settings.TagIds ?? new List<long>(),
                        actingUserId: null,
                        ct).ConfigureAwait(false);
                }

                switch (result.Status)
                {
                    case IngestStatus.Created:
                        count++;
                        log.AppendLine($"Imported: {fileName}");
                        ApplyFilesystemPostAction(settings, file, log);
                        break;
                    case IngestStatus.Duplicate:
                        log.AppendLine($"Skipped (duplicate): {fileName}");
                        ApplyFilesystemPostAction(settings, file, log);
                        break;
                    case IngestStatus.NoStorage:
                        log.AppendLine($"Skipped (no storage): {fileName}");
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to import file '{File}'.", file);
                log.AppendLine($"Error ({fileName}): {ex.Message}");
            }
        }

        log.AppendLine($"Done. {count} document(s) imported.");
        return new RunReport(true, count, log.ToString());
    }

    private void ApplyFilesystemPostAction(FilesystemImportSettings settings, string file, StringBuilder log)
    {
        try
        {
            switch (settings.PostAction?.ToLowerInvariant())
            {
                case "delete":
                    File.Delete(file);
                    break;
                case "move":
                    MoveFile(file, settings.MoveToPath, log);
                    break;
                default:
                    // "none" (or unknown) -> leave the file in place.
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post action '{Action}' failed for '{File}'.", settings.PostAction, file);
            log.AppendLine($"Post-action failed ({Path.GetFileName(file)}): {ex.Message}");
        }
    }

    private static void MoveFile(string file, string? moveToPath, StringBuilder log)
    {
        if (string.IsNullOrWhiteSpace(moveToPath))
        {
            log.AppendLine($"Move skipped (no target) for {Path.GetFileName(file)}.");
            return;
        }

        Directory.CreateDirectory(moveToPath);

        var fileName = Path.GetFileName(file);
        var destination = Path.Combine(moveToPath, fileName);

        if (File.Exists(destination))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            var suffix = 1;
            do
            {
                destination = Path.Combine(moveToPath, $"{stem}_{suffix}{ext}");
                suffix++;
            }
            while (File.Exists(destination));
        }

        File.Move(file, destination);
    }

    // ----- Mail (IMAP / POP3) ----------------------------------------------

    private async Task<RunReport> RunMailAsync(ImportTask task, bool isPop3, CancellationToken ct)
    {
        var settings = TaskSettingsJson.Read<MailImportSettings>(task.SettingsJson);

        var locationId = await ResolveStorageLocationIdAsync(settings.StorageLocationId, ct).ConfigureAwait(false);
        if (locationId is null)
        {
            return new RunReport(false, 0, "No storage location configured");
        }

        var password = _secrets.Unprotect(settings.ProtectedPassword);
        var extensions = ParseExtensions(settings.AttachmentExtensions);
        var senderRegex = CompileRegex(settings.SenderRegex);
        var subjectRegex = CompileRegex(settings.SubjectRegex);

        var log = new StringBuilder();
        var count = 0;

        try
        {
            count = isPop3
                ? await RunPop3Async(settings, password, extensions, senderRegex, subjectRegex, locationId.Value, log, ct).ConfigureAwait(false)
                : await RunImapAsync(settings, password, extensions, senderRegex, subjectRegex, locationId.Value, log, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mail import for task {TaskId} failed.", task.Id);
            return new RunReport(false, count, "Mail import failed: " + ex.Message);
        }

        log.AppendLine($"Done. {count} attachment(s) imported.");
        return new RunReport(true, count, log.ToString());
    }

    private async Task<int> RunImapAsync(
        MailImportSettings settings,
        string password,
        IReadOnlyCollection<string> extensions,
        Regex? senderRegex,
        Regex? subjectRegex,
        long locationId,
        StringBuilder log,
        CancellationToken ct)
    {
        var count = 0;
        using var client = new ImapClient();
        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, SecureOption(settings.UseSsl), ct).ConfigureAwait(false);
            await client.AuthenticateAsync(settings.Username, password, ct).ConfigureAwait(false);

            var folderName = string.IsNullOrWhiteSpace(settings.Folder) ? "INBOX" : settings.Folder;
            var folder = string.Equals(folderName, "INBOX", StringComparison.OrdinalIgnoreCase)
                ? client.Inbox
                : await client.GetFolderAsync(folderName, ct).ConfigureAwait(false);

            await folder.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);

            var uids = await folder.SearchAsync(SearchQuery.All, ct).ConfigureAwait(false);
            var postAction = settings.PostAction?.ToLowerInvariant();

            foreach (var uid in uids)
            {
                ct.ThrowIfCancellationRequested();

                var message = await folder.GetMessageAsync(uid, ct).ConfigureAwait(false);
                if (!MessageMatches(message, senderRegex, subjectRegex))
                {
                    continue;
                }

                count += await ImportAttachmentsAsync(message, extensions, locationId, log, ct).ConfigureAwait(false);
                if (settings.ImportBodyAsPdf)
                {
                    count += await ImportBodyAsPdfAsync(message, locationId, log, ct).ConfigureAwait(false);
                }

                switch (postAction)
                {
                    case "delete":
                        await folder.AddFlagsAsync(uid, MessageFlags.Deleted, silent: true, ct).ConfigureAwait(false);
                        break;
                    case "none":
                        break;
                    default: // "markseen"
                        await folder.AddFlagsAsync(uid, MessageFlags.Seen, silent: true, ct).ConfigureAwait(false);
                        break;
                }
            }

            if (postAction == "delete")
            {
                await folder.ExpungeAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(true, ct).ConfigureAwait(false);
            }
        }

        return count;
    }

    private async Task<int> RunPop3Async(
        MailImportSettings settings,
        string password,
        IReadOnlyCollection<string> extensions,
        Regex? senderRegex,
        Regex? subjectRegex,
        long locationId,
        StringBuilder log,
        CancellationToken ct)
    {
        var count = 0;
        using var client = new Pop3Client();
        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, SecureOption(settings.UseSsl), ct).ConfigureAwait(false);
            await client.AuthenticateAsync(settings.Username, password, ct).ConfigureAwait(false);

            var postAction = settings.PostAction?.ToLowerInvariant();
            var total = client.Count;

            for (var index = 0; index < total; index++)
            {
                ct.ThrowIfCancellationRequested();

                var message = await client.GetMessageAsync(index, ct).ConfigureAwait(false);
                if (!MessageMatches(message, senderRegex, subjectRegex))
                {
                    continue;
                }

                count += await ImportAttachmentsAsync(message, extensions, locationId, log, ct).ConfigureAwait(false);
                if (settings.ImportBodyAsPdf)
                {
                    count += await ImportBodyAsPdfAsync(message, locationId, log, ct).ConfigureAwait(false);
                }

                if (postAction == "delete")
                {
                    await client.DeleteMessageAsync(index, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(true, ct).ConfigureAwait(false);
            }
        }

        return count;
    }

    private async Task<int> ImportBodyAsPdfAsync(
        MimeMessage message,
        long locationId,
        StringBuilder log,
        CancellationToken ct)
    {
        try
        {
            var html = message.HtmlBody ?? message.TextBody ?? string.Empty;
            var subject = string.IsNullOrWhiteSpace(message.Subject) ? "E-Mail" : message.Subject;
            var from = message.From?.ToString() ?? string.Empty;
            var to = message.To?.ToString() ?? string.Empty;

            var pdf = _htmlToPdf.Convert(html, subject, from, to, message.Date);
            var fileName = SanitizeFileName(subject) + ".pdf";

            await using var stream = new MemoryStream(pdf);
            var result = await _ingest.IngestAsync(
                stream, fileName, locationId, null, null, null, Array.Empty<long>(), null, ct).ConfigureAwait(false);

            if (result.Status == IngestStatus.Created)
            {
                log.AppendLine("Imported mail body as PDF: " + fileName);
                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            log.AppendLine("Mail body -> PDF failed: " + ex.Message);
            return 0;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var cleaned = new string(name.Select(ch => Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch).ToArray()).Trim();
        if (cleaned.Length > 120)
        {
            cleaned = cleaned[..120];
        }
        return string.IsNullOrWhiteSpace(cleaned) ? "E-Mail" : cleaned;
    }

    private async Task<int> ImportAttachmentsAsync(
        MimeMessage message,
        IReadOnlyCollection<string> extensions,
        long locationId,
        StringBuilder log,
        CancellationToken ct)
    {
        var count = 0;

        foreach (var attachment in message.Attachments)
        {
            ct.ThrowIfCancellationRequested();

            var fileName = attachment.ContentDisposition?.FileName ?? attachment.ContentType?.Name;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext.Length == 0 || !extensions.Contains(ext))
            {
                continue;
            }

            try
            {
                await using var buffer = new MemoryStream();
                if (attachment is MessagePart messagePart && messagePart.Message is not null)
                {
                    await messagePart.Message.WriteToAsync(buffer, ct).ConfigureAwait(false);
                }
                else if (attachment is MimePart mimePart && mimePart.Content is not null)
                {
                    await mimePart.Content.DecodeToAsync(buffer, ct).ConfigureAwait(false);
                }
                else
                {
                    continue;
                }

                buffer.Position = 0;

                var result = await _ingest.IngestAsync(
                    buffer,
                    fileName,
                    locationId,
                    correspondentId: null,
                    documentTypeId: null,
                    projectId: null,
                    tagIds: new List<long>(),
                    actingUserId: null,
                    ct).ConfigureAwait(false);

                switch (result.Status)
                {
                    case IngestStatus.Created:
                        count++;
                        log.AppendLine($"Imported attachment: {fileName}");
                        break;
                    case IngestStatus.Duplicate:
                        log.AppendLine($"Skipped attachment (duplicate): {fileName}");
                        break;
                    case IngestStatus.NoStorage:
                        log.AppendLine($"Skipped attachment (no storage): {fileName}");
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to import attachment '{FileName}'.", fileName);
                log.AppendLine($"Error ({fileName}): {ex.Message}");
            }
        }

        return count;
    }

    // ----- Connection test --------------------------------------------------

    public async Task<(bool Ok, string Message)> TestMailConnectionAsync(
        MailImportSettings settings,
        bool isPop3,
        string? plaintextPasswordOverride,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var password = string.IsNullOrEmpty(plaintextPasswordOverride)
            ? _secrets.Unprotect(settings.ProtectedPassword)
            : plaintextPasswordOverride;

        if (isPop3)
        {
            using var client = new Pop3Client();
            try
            {
                await client.ConnectAsync(settings.Host, settings.Port, SecureOption(settings.UseSsl), ct).ConfigureAwait(false);
                await client.AuthenticateAsync(settings.Username, password, ct).ConfigureAwait(false);
                return (true, "Connected and authenticated.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                if (client.IsConnected)
                {
                    await client.DisconnectAsync(true, ct).ConfigureAwait(false);
                }
            }
        }

        using var imap = new ImapClient();
        try
        {
            await imap.ConnectAsync(settings.Host, settings.Port, SecureOption(settings.UseSsl), ct).ConfigureAwait(false);
            await imap.AuthenticateAsync(settings.Username, password, ct).ConfigureAwait(false);
            return (true, "Connected and authenticated.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            if (imap.IsConnected)
            {
                await imap.DisconnectAsync(true, ct).ConfigureAwait(false);
            }
        }
    }

    // ----- Helpers ----------------------------------------------------------

    private async Task<long?> ResolveStorageLocationIdAsync(long? preferred, CancellationToken ct)
    {
        if (preferred is { } id)
        {
            var exists = await _db.StorageLocations
                .AsNoTracking()
                .AnyAsync(s => s.Id == id && s.UpdateState != UpdateState.Deleted, ct)
                .ConfigureAwait(false);

            if (exists)
            {
                return id;
            }
        }

        var defaultId = await _db.StorageLocations
            .AsNoTracking()
            .Where(s => s.UpdateState != UpdateState.Deleted && s.IsDefault)
            .Select(s => (long?)s.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (defaultId is not null)
        {
            return defaultId;
        }

        return await _db.StorageLocations
            .AsNoTracking()
            .Where(s => s.UpdateState != UpdateState.Deleted)
            .Select(s => (long?)s.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    private static SecureSocketOptions SecureOption(bool useSsl)
        => useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;

    private static Regex? CompileRegex(string? pattern)
        => string.IsNullOrWhiteSpace(pattern)
            ? null
            : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool MessageMatches(MimeMessage message, Regex? senderRegex, Regex? subjectRegex)
    {
        if (senderRegex is not null)
        {
            var from = message.From?.ToString() ?? string.Empty;
            if (!senderRegex.IsMatch(from))
            {
                return false;
            }
        }

        if (subjectRegex is not null)
        {
            var subject = message.Subject ?? string.Empty;
            if (!subjectRegex.IsMatch(subject))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyCollection<string> ParseExtensions(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return set;
        }

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ext = part.StartsWith('.') ? part : "." + part;
            set.Add(ext.ToLowerInvariant());
        }

        return set;
    }
}
