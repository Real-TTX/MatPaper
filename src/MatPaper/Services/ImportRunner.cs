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
    private readonly HtmlToPdfConverter _htmlToPdf;
    private readonly ConnectionService _connections;
    private readonly ILogger<ImportRunner> _logger;

    public ImportRunner(
        AppDbContext db,
        DocumentIngestService ingest,
        HtmlToPdfConverter htmlToPdf,
        ConnectionService connections,
        ILogger<ImportRunner> logger)
    {
        _db = db;
        _ingest = ingest;
        _htmlToPdf = htmlToPdf;
        _connections = connections;
        _logger = logger;
    }

    private StringBuilder? _liveLog;

    /// <summary>The run's log so far, readable while the run is still going (null before it starts).</summary>
    public string? LogSoFar()
    {
        try { return _liveLog?.ToString(); }
        catch (ArgumentOutOfRangeException) { return null; } // read while the run appended: next poll gets it
    }

    private StringBuilder StartLog() => _liveLog = new StringBuilder();

    public async Task<RunReport> RunAsync(ImportTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        // Imported documents are owned by the user picked on the task, else by whoever
        // created it, falling back to the first active administrator so they never end
        // up ownerless.
        var shared = TaskSettingsJson.Read<CommonImportSettings>(task.SettingsJson);
        var ownerId = await ResolveOwnerIdAsync(task, shared.OwnerUserId, ct).ConfigureAwait(false);

        return task.Type switch
        {
            ImportTaskType.Filesystem => await RunFilesystemAsync(task, ownerId, ct).ConfigureAwait(false),
            ImportTaskType.Smb => await RunSmbAsync(task, ownerId, ct).ConfigureAwait(false),
            ImportTaskType.Imap => await RunMailAsync(task, isPop3: false, ownerId, ct).ConfigureAwait(false),
            ImportTaskType.Pop3 => await RunMailAsync(task, isPop3: true, ownerId, ct).ConfigureAwait(false),
            _ => new RunReport(false, 0, $"Unsupported import type '{task.Type}'.")
        };
    }

    // ----- SMB / CIFS network share ----------------------------------------

    private async Task<RunReport> RunSmbAsync(ImportTask task, long? ownerId, CancellationToken ct)
    {
        var settings = TaskSettingsJson.Read<SmbImportSettings>(task.SettingsJson);
        var share = await _connections.ResolveSmbAsync(settings, null, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(share.Host) || string.IsNullOrWhiteSpace(share.Share))
        {
            return new RunReport(false, 0, "SMB host or share not configured");
        }

        var locationId = await ResolveStorageLocationIdAsync(settings.StorageLocationId, ct).ConfigureAwait(false);
        if (locationId is null && settings.SkipInbox)
        {
            return new RunReport(false, 0, "No storage location configured (required when skipping the inbox)");
        }

        var reviewState = settings.SkipInbox ? ReviewState.Reviewed : ReviewState.Pending;
        var log = StartLog();
        var count = 0;

        SmbSession session;
        try
        {
            session = SmbSession.Connect(share);
        }
        catch (Exception ex)
        {
            return new RunReport(false, 0, "SMB connection failed: " + ex.Message);
        }

        using (session)
        {
            IReadOnlyList<SmbEntry> files;
            try
            {
                files = session.ListEntries(settings.Path, settings.Pattern, settings.Recursive);
            }
            catch (Exception ex)
            {
                return new RunReport(false, 0, "SMB listing failed: " + ex.Message);
            }

            var cutoff = ImportSync.Cutoff(settings.LookbackMode, settings.LookbackDays, settings.LookbackDate, DateTime.UtcNow);
            var state = ImportSync.Load(task);
            var known = new HashSet<string>(state.Seen, StringComparer.Ordinal);
            var keep = new HashSet<string>(StringComparer.Ordinal);
            var skipped = 0;
            var leavesFile = string.IsNullOrWhiteSpace(settings.PostAction) || settings.PostAction.Equals("none", StringComparison.OrdinalIgnoreCase);

            foreach (var entry in files)
            {
                ct.ThrowIfCancellationRequested();

                var file = entry.Path;
                var fileName = file.Contains('\\') ? file[(file.LastIndexOf('\\') + 1)..] : file;

                // Metadata files and the XML of an XRechnung PDF belong to the file they are named after.
                if (file.EndsWith(DocumentStorageService.MetadataCompanion, StringComparison.OrdinalIgnoreCase)
                    || (file.EndsWith(DocumentStorageService.XmlCompanion, StringComparison.OrdinalIgnoreCase)
                        && session.FileExists(file[..^DocumentStorageService.XmlCompanion.Length])))
                {
                    continue;
                }

                if (cutoff is DateTime since && entry.ModifiedUtc < since)
                {
                    skipped++;
                    continue;
                }

                var fingerprint = ImportSync.Fingerprint(file, entry.Size, entry.ModifiedUtc);
                if (known.Contains(fingerprint))
                {
                    keep.Add(fingerprint);
                    skipped++;
                    continue;
                }

                try
                {
                    var bytes = session.ReadAllBytes(file);
                    var sidecar = session.FileExists(file + DocumentStorageService.MetadataCompanion)
                        ? DocumentSidecar.TryParse(session.ReadAllBytes(file + DocumentStorageService.MetadataCompanion))
                        : null;
                    await using var stream = new MemoryStream(bytes);
                    var result = await _ingest.IngestAsync(
                        stream, fileName, locationId,
                        settings.CorrespondentId, settings.DocumentTypeId, settings.ProjectId,
                        settings.TagIds ?? new List<long>(), ownerId, ct, reviewState, settings.IsCommon,
                        origin: DocumentOrigin.ImportFolder, importTaskId: task.Id, sidecar: sidecar).ConfigureAwait(false);

                    switch (result.Status)
                    {
                        case IngestStatus.Created:
                            count++;
                            log.AppendLine($"Imported: {fileName}");
                            ApplySmbPostAction(session, settings, file, log);
                            if (leavesFile) { keep.Add(fingerprint); }
                            break;
                        case IngestStatus.Duplicate:
                            log.AppendLine($"Skipped (duplicate): {fileName}");
                            ApplySmbPostAction(session, settings, file, log);
                            if (leavesFile) { keep.Add(fingerprint); }
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
                    _logger.LogWarning(ex, "Failed to import SMB file '{File}'.", file);
                    log.AppendLine($"Error ({fileName}): {ex.Message}");
                }

                if (LimitReached(count, settings.MaxPerRun, log)) { break; }
            }

            state.Seen = keep.ToList();
            await ImportSync.SaveAsync(_db, task.Id, state).ConfigureAwait(false);
            if (skipped > 0)
            {
                log.AppendLine($"{skipped} file(s) skipped (unchanged since an earlier run or older than the chosen period).");
            }
        }

        log.AppendLine($"Done. {count} document(s) imported.");
        return new RunReport(true, count, log.ToString());
    }

    private static void ApplySmbPostAction(SmbSession session, SmbImportSettings settings, string file, StringBuilder log)
    {
        // The companions of a file (metadata, XRechnung XML) follow it.
        foreach (var suffix in new[] { DocumentStorageService.MetadataCompanion, DocumentStorageService.XmlCompanion })
        {
            if (settings.PostAction is not ("delete" or "move") || !session.FileExists(file + suffix))
            {
                continue;
            }

            if (settings.PostAction == "delete")
            {
                session.TryDelete(file + suffix);
            }
            else if (!string.IsNullOrWhiteSpace(settings.MoveToPath))
            {
                session.TryMove(file + suffix, settings.MoveToPath!, out _);
            }
        }

        if (settings.PostAction == "delete")
        {
            if (!session.TryDelete(file))
            {
                log.AppendLine($"Could not delete on the share: {file}");
            }
        }
        else if (settings.PostAction == "move" && !string.IsNullOrWhiteSpace(settings.MoveToPath))
        {
            // A file left behind would be picked up (and skipped as a duplicate) on every
            // following run, so report it instead of failing silently.
            if (!session.TryMove(file, settings.MoveToPath!, out var error))
            {
                log.AppendLine($"Could not move on the share: {file} ({error})");
            }
        }
    }

    public async Task<(bool Ok, string Message)> TestSmbConnectionAsync(
        SmbImportSettings settings, string? plaintextPasswordOverride, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var share = await _connections.ResolveSmbAsync(settings, plaintextPasswordOverride, ct).ConfigureAwait(false);
            using var session = SmbSession.Connect(share);
            return (true, "Connected to the share.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<long?> ResolveOwnerIdAsync(ImportTask task, long? preferredUserId, CancellationToken ct)
    {
        if (preferredUserId is { } preferred)
        {
            var preferredActive = await _db.Users
                .AsNoTracking()
                .AnyAsync(u => u.Id == preferred && u.IsActive, ct)
                .ConfigureAwait(false);
            if (preferredActive)
            {
                return preferred;
            }
        }

        if (task.CreateUserId is { } creator)
        {
            var stillActive = await _db.Users
                .AsNoTracking()
                .AnyAsync(u => u.Id == creator && u.IsActive, ct)
                .ConfigureAwait(false);
            if (stillActive)
            {
                return creator;
            }
        }

        return await _db.Users
            .AsNoTracking()
            .Where(u => u.IsActive && u.Role != null && u.Role.Name == "Admin")
            .OrderBy(u => u.Id)
            .Select(u => (long?)u.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    // ----- Filesystem -------------------------------------------------------

    private async Task<RunReport> RunFilesystemAsync(ImportTask task, long? ownerId, CancellationToken ct)
    {
        var settings = TaskSettingsJson.Read<FilesystemImportSettings>(task.SettingsJson);

        if (string.IsNullOrWhiteSpace(settings.SourcePath) || !Directory.Exists(settings.SourcePath))
        {
            return new RunReport(false, 0, "Source folder not found");
        }

        var locationId = await ResolveStorageLocationIdAsync(settings.StorageLocationId, ct).ConfigureAwait(false);
        if (locationId is null && settings.SkipInbox)
        {
            return new RunReport(false, 0, "No storage location configured (required when skipping the inbox)");
        }

        var log = StartLog();
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

        var cutoff = ImportSync.Cutoff(settings.LookbackMode, settings.LookbackDays, settings.LookbackDate, DateTime.UtcNow);
        var state = ImportSync.Load(task);
        var known = new HashSet<string>(state.Seen, StringComparer.Ordinal);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        var leavesFile = string.IsNullOrWhiteSpace(settings.PostAction) || settings.PostAction.Equals("none", StringComparison.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(file);
            if (file.EndsWith(DocumentStorageService.MetadataCompanion, StringComparison.OrdinalIgnoreCase)
                || (file.EndsWith(DocumentStorageService.XmlCompanion, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(file[..^DocumentStorageService.XmlCompanion.Length])))
            {
                continue; // belongs to the file it is named after
            }

            try
            {
                // Unchanged since a run that handled it: skip without reading it again.
                var info = new FileInfo(file);
                if (cutoff is DateTime since && info.LastWriteTimeUtc < since)
                {
                    skipped++;
                    continue;
                }

                var fingerprint = ImportSync.Fingerprint(file, info.Length, info.LastWriteTimeUtc);
                if (known.Contains(fingerprint))
                {
                    keep.Add(fingerprint);
                    skipped++;
                    continue;
                }

                var sidecarFile = file + DocumentStorageService.MetadataCompanion;
                var sidecar = File.Exists(sidecarFile) ? DocumentSidecar.TryParse(await File.ReadAllBytesAsync(sidecarFile, ct).ConfigureAwait(false)) : null;

                IngestResult result;
                await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    result = await _ingest.IngestAsync(
                        stream,
                        fileName,
                        locationId,
                        settings.CorrespondentId,
                        settings.DocumentTypeId,
                        settings.ProjectId,
                        settings.TagIds ?? new List<long>(),
                        actingUserId: ownerId,
                        ct,
                        reviewState: settings.SkipInbox ? ReviewState.Reviewed : ReviewState.Pending,
                        isCommon: settings.IsCommon,
                        origin: DocumentOrigin.ImportFolder, importTaskId: task.Id, sidecar: sidecar).ConfigureAwait(false);
                }

                switch (result.Status)
                {
                    case IngestStatus.Created:
                        count++;
                        log.AppendLine($"Imported: {fileName}");
                        ApplyFilesystemPostAction(settings, file, log);
                        if (leavesFile) { keep.Add(fingerprint); }
                        break;
                    case IngestStatus.Duplicate:
                        log.AppendLine($"Skipped (duplicate): {fileName}");
                        ApplyFilesystemPostAction(settings, file, log);
                        if (leavesFile) { keep.Add(fingerprint); }
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

            if (LimitReached(count, settings.MaxPerRun, log)) { break; }
        }

        state.Seen = keep.ToList();
        await ImportSync.SaveAsync(_db, task.Id, state).ConfigureAwait(false);
        if (skipped > 0)
        {
            log.AppendLine($"{skipped} file(s) skipped (unchanged since an earlier run or older than the chosen period).");
        }

        log.AppendLine($"Done. {count} document(s) imported.");
        return new RunReport(true, count, log.ToString());
    }

    private void ApplyFilesystemPostAction(FilesystemImportSettings settings, string file, StringBuilder log)
    {
        try
        {
            // The companions of a file (metadata, XRechnung XML) follow it.
            foreach (var suffix in new[] { DocumentStorageService.MetadataCompanion, DocumentStorageService.XmlCompanion })
            {
                if (!File.Exists(file + suffix))
                {
                    continue;
                }

                switch (settings.PostAction?.ToLowerInvariant())
                {
                    case "delete":
                        File.Delete(file + suffix);
                        break;
                    case "move" when !string.IsNullOrWhiteSpace(settings.MoveToPath):
                        MoveFile(file + suffix, settings.MoveToPath, log);
                        break;
                }
            }

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

    /// <summary>
    /// A mailbox connection that a group run opens once and hands to every rule of the group: one connect and one
    /// sign-in instead of one per rule. Each rule still filters on the server with its own search.
    /// </summary>
    public sealed class SharedImapSession : IAsyncDisposable
    {
        internal SharedImapSession(ImapClient client) => Client = client;

        internal ImapClient Client { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Client.IsConnected)
                {
                    await Client.DisconnectAsync(true).ConfigureAwait(false);
                }
            }
            catch
            {
                // closing is best effort
            }

            Client.Dispose();
        }
    }

    /// <summary>When set, IMAP rules use this connection instead of opening their own.</summary>
    public SharedImapSession? SharedImap { get; set; }

    /// <summary>Connects and signs in once for the mailbox the given (group) rule reads from.</summary>
    public async Task<SharedImapSession> OpenSharedImapAsync(ImportTask rule, CancellationToken ct)
    {
        var settings = TaskSettingsJson.Read<MailImportSettings>(rule.SettingsJson);
        var mail = await _connections.ResolveMailAsync(settings, ct).ConfigureAwait(false);
        var client = new ImapClient();
        try
        {
            await client.ConnectAsync(mail.Host, mail.Port, SecureOption(mail.UseSsl), ct).ConfigureAwait(false);
            await _connections.AuthenticateAsync(client, mail, ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return new SharedImapSession(client);
    }

    /// <summary>True (and says so in the log) once a run has imported as many documents as the rule allows.</summary>
    private static bool LimitReached(int count, int max, StringBuilder log)
    {
        if (max <= 0 || count < max) { return false; }
        log.AppendLine($"Limit reached: {count} document(s) imported (at most {max} per run). The next run continues from here.");
        return true;
    }

    private async Task<RunReport> RunMailAsync(ImportTask task, bool isPop3, long? ownerId, CancellationToken ct)
    {
        var settings = TaskSettingsJson.Read<MailImportSettings>(task.SettingsJson);

        var locationId = await ResolveStorageLocationIdAsync(settings.StorageLocationId, ct).ConfigureAwait(false);
        if (locationId is null && settings.SkipInbox)
        {
            return new RunReport(false, 0, "No storage location configured (required when skipping the inbox)");
        }

        var mail = await _connections.ResolveMailAsync(settings, ct).ConfigureAwait(false);
        var extensions = ParseExtensions(settings.AttachmentExtensions);
        var senderRegex = CompileRegex(settings.SenderRegex);
        var subjectRegex = CompileRegex(settings.SubjectRegex);

        var log = StartLog();
        var count = 0;

        try
        {
            count = isPop3
                ? await RunPop3Async(task, settings, mail, extensions, senderRegex, subjectRegex, locationId, ownerId, log, ct).ConfigureAwait(false)
                : await RunImapAsync(task, settings, mail, extensions, senderRegex, subjectRegex, locationId, ownerId, log, ct).ConfigureAwait(false);
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
        ImportTask task,
        MailImportSettings settings,
        ResolvedMail mail,
        IReadOnlyCollection<string> extensions,
        Regex? senderRegex,
        Regex? subjectRegex,
        long? locationId,
        long? ownerId,
        StringBuilder log,
        CancellationToken ct)
    {
        var count = 0;
        var reviewState = settings.SkipInbox ? ReviewState.Reviewed : ReviewState.Pending;
        // Inside a group run the connection is shared (and closed by the group); alone the rule opens its own.
        var ownClient = SharedImap is null ? new ImapClient() : null;
        var client = SharedImap?.Client ?? ownClient!;
        try
        {
            if (ownClient is not null)
            {
                await client.ConnectAsync(mail.Host, mail.Port, SecureOption(mail.UseSsl), ct).ConfigureAwait(false);
                await _connections.AuthenticateAsync(client, mail, ct).ConfigureAwait(false);
            }

            var folderName = string.IsNullOrWhiteSpace(settings.Folder) ? "INBOX" : settings.Folder;
            var folder = string.Equals(folderName, "INBOX", StringComparison.OrdinalIgnoreCase)
                ? client.Inbox
                : await client.GetFolderAsync(folderName, ct).ConfigureAwait(false);

            await folder.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);

            var cutoff = ImportSync.Cutoff(settings.LookbackMode, settings.LookbackDays, settings.LookbackDate, DateTime.UtcNow);
            var state = ImportSync.Load(task);
            var resume = state.UidValidity == folder.UidValidity && state.LastUid > 0;

            // Only what is new: UIDs above the last one handled, and not older than the lookback.
            var query = ServerFilter(settings, client);
            if (cutoff is DateTime since)
            {
                query = query.And(SearchQuery.DeliveredAfter(since.Date));
            }
            if (resume)
            {
                query = query.And(SearchQuery.Uids(new UniqueIdRange(new UniqueId((uint)(state.LastUid + 1)), UniqueId.MaxValue)));
            }

            // "UID n:*" with n above the highest UID still returns the LAST message (RFC 3501 treats
            // the range as n:max in either order), so anything not above the stored UID is dropped.
            log.AppendLine("Searching the mailbox ...");
            var found = await folder.SearchAsync(query, ct).ConfigureAwait(false);
            IList<UniqueId> uids = resume ? found.Where(u => u.Id > state.LastUid).ToList() : found;
            log.AppendLine(resume
                ? $"Continuing after message {state.LastUid}: {uids.Count} new message(s)."
                : $"No earlier position stored: {uids.Count} message(s) to check.");

            // Headers first (envelope only, no body): a mailbox with thousands of mails should not
            // be downloaded just to find the few that match.
            var wanted = new HashSet<uint>();
            for (var i = 0; i < uids.Count; i += 200)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = uids.Skip(i).Take(200).ToList();
                var summaries = await folder.FetchAsync(chunk, MessageSummaryItems.Envelope, ct).ConfigureAwait(false);
                foreach (var summary in summaries)
                {
                    var env = summary.Envelope;
                    if (HeadersMatch(env?.From?.ToString() ?? string.Empty, env?.To?.ToString() ?? string.Empty,
                        env?.Subject ?? string.Empty, settings, senderRegex, subjectRegex))
                    {
                        wanted.Add(summary.UniqueId.Id);
                    }
                }
                log.AppendLine($"Read headers of {Math.Min(i + 200, uids.Count)} of {uids.Count} message(s): {wanted.Count} match.");
            }

            var post = MailPostActions.Parse(settings.PostAction);
            var moveDest = post.Move ? GetOrCreateFolder(client, settings.MoveToFolder) : null;

            // The position only advances past messages that were fully handled, so one that
            // fails is tried again next time; it is saved even when the run is cut short.
            long lastDone = resume ? state.LastUid : 0;
            try
            {
                var checkedCount = 0;
                foreach (var uid in uids)
                {
                    ct.ThrowIfCancellationRequested();
                    if (++checkedCount % 25 == 0)
                    {
                        log.AppendLine($"Checked {checkedCount} of {uids.Count} message(s) ...");
                    }

                    if (!wanted.Contains(uid.Id))
                    {
                        lastDone = Math.Max(lastDone, uid.Id);
                        continue;
                    }

                    var message = await folder.GetMessageAsync(uid, ct).ConfigureAwait(false);
                    if (!MessageMatches(message, settings, senderRegex, subjectRegex))
                    {
                        lastDone = Math.Max(lastDone, uid.Id);
                        continue;
                    }

                    count += await ImportAttachmentsAsync(message, extensions, locationId, ownerId, reviewState, settings.IsCommon, task.Id, log, ct).ConfigureAwait(false);
                    if (settings.ImportBodyAsPdf)
                    {
                        count += await ImportBodyAsPdfAsync(message, locationId, ownerId, reviewState, settings.IsCommon, task.Id, log, ct).ConfigureAwait(false);
                    }

                    var flags = MessageFlags.None;
                    if (post.MarkSeen) { flags |= MessageFlags.Seen; }
                    if (post.Flag) { flags |= MessageFlags.Flagged; }
                    if (post.Delete) { flags |= MessageFlags.Deleted; }
                    if (flags != MessageFlags.None)
                    {
                        await folder.AddFlagsAsync(uid, flags, silent: true, ct).ConfigureAwait(false);
                    }

                    if (moveDest is not null)
                    {
                        await folder.MoveToAsync(uid, moveDest, ct).ConfigureAwait(false);
                    }

                    lastDone = Math.Max(lastDone, uid.Id);
                    if (LimitReached(count, settings.MaxPerRun, log)) { break; }
                }
            }
            finally
            {
                if (lastDone > 0)
                {
                    state.UidValidity = folder.UidValidity;
                    state.LastUid = lastDone;
                    await ImportSync.SaveAsync(_db, task.Id, state).ConfigureAwait(false);
                }
            }

            if (post.Delete)
            {
                await folder.ExpungeAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (ownClient is not null)
            {
                if (ownClient.IsConnected)
                {
                    await ownClient.DisconnectAsync(true, ct).ConfigureAwait(false);
                }

                ownClient.Dispose();
            }
        }

        return count;
    }

    private async Task<int> RunPop3Async(
        ImportTask task,
        MailImportSettings settings,
        ResolvedMail mail,
        IReadOnlyCollection<string> extensions,
        Regex? senderRegex,
        Regex? subjectRegex,
        long? locationId,
        long? ownerId,
        StringBuilder log,
        CancellationToken ct)
    {
        var count = 0;
        var reviewState = settings.SkipInbox ? ReviewState.Reviewed : ReviewState.Pending;
        using var client = new Pop3Client();
        try
        {
            await client.ConnectAsync(mail.Host, mail.Port, SecureOption(mail.UseSsl), ct).ConfigureAwait(false);
            await _connections.AuthenticateAsync(client, mail, ct).ConfigureAwait(false);

            var post = MailPostActions.Parse(settings.PostAction);
            var total = client.Count;

            // POP3 has no folder positions, but every message has a stable UIDL: remember the
            // ones already handled and skip them. Only UIDLs still on the server are kept.
            var cutoff = ImportSync.Cutoff(settings.LookbackMode, settings.LookbackDays, settings.LookbackDate, DateTime.UtcNow);
            var state = ImportSync.Load(task);
            var known = new HashSet<string>(state.Seen, StringComparer.Ordinal);
            var handled = new HashSet<string>(StringComparer.Ordinal);
            var uidls = total > 0 ? await client.GetMessageUidsAsync(ct).ConfigureAwait(false) : new List<string>();
            var skipped = 0;

            try
            {
                for (var index = 0; index < total; index++)
                {
                    ct.ThrowIfCancellationRequested();

                    var id = index < uidls.Count ? uidls[index] : null;
                    if (id is not null && known.Contains(id))
                    {
                        handled.Add(id);
                        skipped++;
                        continue;
                    }

                    if (cutoff is DateTime since)
                    {
                        // The headers alone are enough to see how old a message is.
                        var headers = await client.GetMessageHeadersAsync(index, ct).ConfigureAwait(false);
                        var dateText = headers[HeaderId.Date];
                        if (dateText is not null && MimeKit.Utils.DateUtils.TryParse(dateText, out var sent) && sent.UtcDateTime < since)
                        {
                            if (id is not null) { handled.Add(id); }
                            skipped++;
                            continue;
                        }
                    }

                    var message = await client.GetMessageAsync(index, ct).ConfigureAwait(false);
                    if (!MessageMatches(message, settings, senderRegex, subjectRegex))
                    {
                        if (id is not null) { handled.Add(id); }
                        continue;
                    }

                    count += await ImportAttachmentsAsync(message, extensions, locationId, ownerId, reviewState, settings.IsCommon, task.Id, log, ct).ConfigureAwait(false);
                    if (settings.ImportBodyAsPdf)
                    {
                        count += await ImportBodyAsPdfAsync(message, locationId, ownerId, reviewState, settings.IsCommon, task.Id, log, ct).ConfigureAwait(false);
                    }

                    if (post.Delete)
                    {
                        await client.DeleteMessageAsync(index, ct).ConfigureAwait(false);
                    }

                    if (id is not null) { handled.Add(id); }
                    if (LimitReached(count, settings.MaxPerRun, log)) { break; }
                }
            }
            finally
            {
                state.Seen = handled.ToList();
                await ImportSync.SaveAsync(_db, task.Id, state).ConfigureAwait(false);
                if (skipped > 0)
                {
                    log.AppendLine($"{skipped} message(s) skipped (already handled or older than the chosen period).");
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
        long? locationId,
        long? ownerId,
        ReviewState reviewState,
        bool isCommon,
        long importTaskId,
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
                stream, fileName, locationId, null, null, null, Array.Empty<long>(), ownerId, ct, reviewState, isCommon,
                origin: DocumentOrigin.Mail, importTaskId: importTaskId).ConfigureAwait(false);

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
        long? locationId,
        long? ownerId,
        ReviewState reviewState,
        bool isCommon,
        long importTaskId,
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
                    actingUserId: ownerId,
                    ct,
                    reviewState: reviewState,
                    isCommon: isCommon,
                    origin: DocumentOrigin.Mail, importTaskId: importTaskId).ConfigureAwait(false);

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

        var mail = await _connections.ResolveMailAsync(settings, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(plaintextPasswordOverride) && mail.Connection is null)
        {
            mail = mail with { Password = plaintextPasswordOverride };
        }

        if (isPop3)
        {
            using var client = new Pop3Client();
            try
            {
                await client.ConnectAsync(mail.Host, mail.Port, SecureOption(mail.UseSsl), ct).ConfigureAwait(false);
                await _connections.AuthenticateAsync(client, mail, ct).ConfigureAwait(false);
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
            await imap.ConnectAsync(mail.Host, mail.Port, SecureOption(mail.UseSsl), ct).ConfigureAwait(false);
            await _connections.AuthenticateAsync(imap, mail, ct).ConfigureAwait(false);
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

    internal static SecureSocketOptions SecureOption(bool useSsl)
        => useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;

    /// <summary>Compiles a user-supplied filter pattern. The timeout keeps a pathological
    /// expression from pinning a thread (the wizard preview runs these in a request).</summary>
    internal static Regex? CompileRegex(string? pattern)
        => string.IsNullOrWhiteSpace(pattern)
            ? null
            : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static bool MessageMatches(MimeMessage message, MailImportSettings settings, Regex? senderRegex, Regex? subjectRegex)
        => HeadersMatch(
            message.From?.ToString() ?? string.Empty,
            message.To?.ToString() ?? string.Empty,
            message.Subject ?? string.Empty,
            settings, senderRegex, subjectRegex);

    /// <summary>
    /// The From/To/Subject "contains" filters as an IMAP SEARCH, so the server returns only the
    /// candidates instead of every message in the folder. Several words in one filter are
    /// alternatives (OR), the filters themselves must all hold (AND) - the same rule
    /// <see cref="HeadersMatch"/> applies afterwards as the safety net. Regex filters stay local.
    /// </summary>
    internal static SearchQuery ServerFilter(MailImportSettings settings, ImapClient client)
    {
        var query = SearchQuery.All;

        // Gmail matches whole words only ("Rechnung" misses "Rechnungen", "vodafone" misses
        // "x@kunde.vodafone.de"), so a server filter would drop mails the local check keeps.
        var where = settings.FilterWhere?.ToLowerInvariant();
        if (where == "local" || (where != "server" && client.Capabilities.HasFlag(ImapCapabilities.GMailExt1)))
        {
            return query;
        }

        query = AndAny(query, settings.FromFilter, SearchQuery.FromContains);
        query = AndAny(query, settings.ToFilter, SearchQuery.ToContains);
        query = AndAny(query, settings.SubjectFilter, SearchQuery.SubjectContains);
        return query;
    }

    private static SearchQuery AndAny(SearchQuery query, string? filter, Func<string, SearchQuery> contains)
    {
        SearchQuery? any = null;
        foreach (var word in (filter ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            any = any is null ? contains(word) : any.Or(contains(word));
        }
        return any is null ? query : query.And(any);
    }

    /// <summary>
    /// The filter rules on their own, so the wizard preview can judge an IMAP summary
    /// (envelope only, no body download) exactly like the runner judges a full message.
    /// </summary>
    internal static bool HeadersMatch(
        string from, string to, string subject,
        MailImportSettings settings, Regex? senderRegex, Regex? subjectRegex)
    {
        // Friendly "contains any of" filters (comma-separated).
        if (!ContainsAny(from, settings.FromFilter))
        {
            return false;
        }
        if (!ContainsAny(to, settings.ToFilter))
        {
            return false;
        }
        if (!ContainsAny(subject, settings.SubjectFilter))
        {
            return false;
        }

        // Optional advanced regex filters. A pattern that runs into its timeout counts
        // as "no match" instead of failing the whole run.
        try
        {
            if (senderRegex is not null && !senderRegex.IsMatch(from))
            {
                return false;
            }
            if (subjectRegex is not null && !subjectRegex.IsMatch(subject))
            {
                return false;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        return true;
    }

    /// <summary>True when <paramref name="csv"/> is empty, or the text contains any comma-separated term.</summary>
    private static bool ContainsAny(string text, string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return true;
        }

        foreach (var term in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Resolves an IMAP folder by path, creating it under the personal namespace when missing.</summary>
    private static IMailFolder? GetOrCreateFolder(ImapClient client, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return client.GetFolder(path);
        }
        catch
        {
            try
            {
                var root = client.GetFolder(client.PersonalNamespaces[0]);
                return root.Create(path, true);
            }
            catch
            {
                return null;
            }
        }
    }

    internal static IReadOnlyCollection<string> ParseExtensions(string? raw)
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
