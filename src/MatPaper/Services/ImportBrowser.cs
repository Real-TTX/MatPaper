using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>An entry the import wizard can show in its source picker.</summary>
/// <param name="Path">Value stored in the task (IMAP full folder name, share-relative or absolute path).</param>
/// <param name="Name">Label shown to the user.</param>
/// <param name="ItemCount">Messages resp. matching files in it, or null when unknown.</param>
/// <param name="HasChildren">Whether the entry can be expanded further.</param>
public sealed record BrowseEntry(string Path, string Name, int? ItemCount, bool HasChildren);

/// <summary>One item the import would pick up right now.</summary>
/// <param name="Title">Subject resp. file name.</param>
/// <param name="Detail">Sender / folder path.</param>
/// <param name="Date">Message date resp. last write time (UTC), if known.</param>
/// <param name="Extra">Attachment names resp. file size.</param>
public sealed record PreviewItem(string Title, string? Detail, DateTime? Date, string? Extra);

/// <summary>Result of a preview run: the first <see cref="Items"/> of <see cref="TotalMatches"/>.</summary>
public sealed record PreviewResult(bool Ok, string? Error, int TotalMatches, IReadOnlyList<PreviewItem> Items, int Scanned, bool Truncated = false);

/// <summary>
/// Read-only inspection of an import source for the wizard: lists mail folders, SMB shares
/// and folders or local subfolders, and previews which items an import would pick up.
/// Nothing here writes, deletes or marks anything on the source.
/// </summary>
public sealed class ImportBrowser
{
    /// <summary>The most rows a preview returns; the page shows them in steps of 25.</summary>
    private const int MaxPreviewItems = 200;

    /// <summary>POP3 has to download every message it looks at, so its preview window stays small.</summary>
    private const int Pop3PreviewMaxMessages = 500;

    /// <summary>Per-operation network timeout for the wizard (MailKit defaults to two minutes).</summary>
    private const int MailTimeoutMs = 20000;

    /// <summary>Messages above this size are skipped by the POP3 preview (it must download them).</summary>
    private const long Pop3PreviewMaxBytes = 25L * 1024 * 1024;

    /// <summary>
    /// Hard budget for one SMB browse/preview. SMBLibrary blocks without a timeout, so the
    /// request gives up and reports an error instead of holding the page forever.
    /// </summary>
    private const int NetworkBudgetSeconds = 30;

    /// <summary>
    /// How long listing mailbox folders may spend asking for message counts. A mailbox such as
    /// Gmail has dozens of labels and every count is its own round trip; without a limit the
    /// answer takes so long that a proxy in front of the app drops the request. Folders reached
    /// after the budget simply show no count.
    /// </summary>
    private static readonly TimeSpan FolderCountBudget = TimeSpan.FromSeconds(8);

    private readonly ConnectionService _connections;
    private readonly ILogger<ImportBrowser> _logger;

    public ImportBrowser(ConnectionService connections, ILogger<ImportBrowser> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    // ----- Mail -------------------------------------------------------------

    /// <summary>Lists the mailbox folders with their message counts (IMAP only).</summary>
    public async Task<IReadOnlyList<BrowseEntry>> ListMailFoldersAsync(
        MailImportSettings settings, string? plaintextPassword, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var mail = await ResolveMailAsync(settings, plaintextPassword, ct).ConfigureAwait(false);

        using var client = new ImapClient { Timeout = MailTimeoutMs };
        try
        {
            await client.ConnectAsync(mail.Host, mail.Port, ImportRunner.SecureOption(mail.UseSsl), ct).ConfigureAwait(false);
            await _connections.AuthenticateAsync(client, mail, ct).ConfigureAwait(false);

            IList<IMailFolder> root = client.PersonalNamespaces.Count > 0
                ? await client.GetFoldersAsync(client.PersonalNamespaces[0], false, ct).ConfigureAwait(false)
                : new List<IMailFolder>();

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var entries = new List<BrowseEntry>();
            foreach (var folder in root)
            {
                ct.ThrowIfCancellationRequested();

                if ((folder.Attributes & FolderAttributes.NonExistent) != 0)
                {
                    continue;
                }

                int? count = null;
                var selectable = (folder.Attributes & FolderAttributes.NoSelect) == 0;
                if (!selectable || clock.Elapsed >= FolderCountBudget || !client.IsConnected)
                {
                    entries.Add(new BrowseEntry(folder.FullName, folder.Name, null, false));
                    continue;
                }

                try
                {
                    await folder.StatusAsync(StatusItems.Count, ct).ConfigureAwait(false);
                    count = folder.Count;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // \NoSelect folders and servers without STATUS simply show no count.
                    _logger.LogDebug(ex, "No message count for IMAP folder {Folder}.", folder.FullName);
                }

                entries.Add(new BrowseEntry(folder.FullName, folder.Name, count, false));
            }

            return entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(true, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Lists the messages an IMAP/POP3 import would pick up, without changing anything.</summary>
    public async Task<PreviewResult> PreviewMailAsync(
        MailImportSettings settings, bool isPop3, string? plaintextPassword, int window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var mail = await ResolveMailAsync(settings, plaintextPassword, ct).ConfigureAwait(false);
            var extensions = ImportRunner.ParseExtensions(settings.AttachmentExtensions);
            var senderRegex = ImportRunner.CompileRegex(settings.SenderRegex);
            var subjectRegex = ImportRunner.CompileRegex(settings.SubjectRegex);

            return isPop3
                ? await PreviewPop3Async(settings, mail, extensions, senderRegex, subjectRegex, window, ct).ConfigureAwait(false)
                : await PreviewImapAsync(settings, mail, extensions, senderRegex, subjectRegex, window, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PreviewResult(false, ex.Message, 0, Array.Empty<PreviewItem>(), 0);
        }
    }

    private async Task<PreviewResult> PreviewImapAsync(
        MailImportSettings settings, ResolvedMail mail,
        IReadOnlyCollection<string> extensions, Regex? senderRegex, Regex? subjectRegex, int window, CancellationToken ct)
    {
        using var client = new ImapClient { Timeout = MailTimeoutMs };
        try
        {
            await client.ConnectAsync(mail.Host, mail.Port, ImportRunner.SecureOption(mail.UseSsl), ct).ConfigureAwait(false);
            await _connections.AuthenticateAsync(client, mail, ct).ConfigureAwait(false);

            var folder = string.IsNullOrWhiteSpace(settings.Folder) || settings.Folder.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
                ? client.Inbox
                : await client.GetFolderAsync(settings.Folder, ct).ConfigureAwait(false);

            await folder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

            // The same selection RunImapAsync starts from (all messages, within the chosen period),
            // newest first and capped at the window. The remembered position is NOT applied, so the
            // preview shows what the filters would catch, not only what is new since the last run.
            var query = ImportRunner.ServerFilter(settings, client);
            if (ImportSync.Cutoff(settings.LookbackMode, settings.LookbackDays, settings.LookbackDate, DateTime.UtcNow) is DateTime since)
            {
                query = query.And(MailKit.Search.SearchQuery.DeliveredAfter(since.Date));
            }

            var uids = await folder.SearchAsync(query, ct).ConfigureAwait(false);
            var newest = uids.Reverse().Take(window).ToList();

            var items = new List<PreviewItem>();
            var matches = 0;

            if (newest.Count > 0)
            {
                // Envelope + structure only: downloading the body would set \Seen on the
                // server, which the post-action "mark seen" is supposed to decide.
                var summaries = await folder
                    .FetchAsync(newest, MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure, ct)
                    .ConfigureAwait(false);

                foreach (var summary in summaries)
                {
                    ct.ThrowIfCancellationRequested();

                    var envelope = summary.Envelope;
                    var from = envelope?.From?.ToString() ?? string.Empty;
                    var to = envelope?.To?.ToString() ?? string.Empty;
                    var subject = envelope?.Subject ?? string.Empty;

                    if (!ImportRunner.HeadersMatch(from, to, subject, settings, senderRegex, subjectRegex))
                    {
                        continue;
                    }

                    var attachments = summary.Attachments
                        .Select(a => a.FileName ?? string.Empty)
                        .Where(name => name.Length > 0 && extensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
                        .ToList();

                    if (attachments.Count == 0 && !settings.ImportBodyAsPdf)
                    {
                        continue;
                    }

                    matches++;
                    if (items.Count < MaxPreviewItems)
                    {
                        var extra = attachments.Count > 0 ? string.Join(", ", attachments) : null;
                        if (settings.ImportBodyAsPdf)
                        {
                            extra = string.IsNullOrEmpty(extra) ? "PDF" : extra + " + PDF";
                        }

                        items.Add(new PreviewItem(
                            string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject,
                            from,
                            envelope?.Date?.UtcDateTime,
                            extra));
                    }
                }
            }

            return new PreviewResult(true, null, matches, items, newest.Count, uids.Count > newest.Count);
        }
        finally
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(true, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<PreviewResult> PreviewPop3Async(
        MailImportSettings settings, ResolvedMail mail,
        IReadOnlyCollection<string> extensions, Regex? senderRegex, Regex? subjectRegex, int window, CancellationToken ct)
    {
        using var client = new Pop3Client { Timeout = MailTimeoutMs };
        try
        {
            await client.ConnectAsync(mail.Host, mail.Port, ImportRunner.SecureOption(mail.UseSsl), ct).ConfigureAwait(false);
            await _connections.AuthenticateAsync(client, mail, ct).ConfigureAwait(false);

            var items = new List<PreviewItem>();
            var matches = 0;
            var total = await client.GetMessageCountAsync(ct).ConfigureAwait(false);
            var scanned = Math.Min(total, Math.Min(window, Pop3PreviewMaxMessages));
            var since = ImportSync.Cutoff(settings.LookbackMode, settings.LookbackDays, settings.LookbackDate, DateTime.UtcNow);

            // POP3 cannot fetch envelopes, so sizes decide what is worth downloading.
            IList<int> sizes;
            try
            {
                sizes = await client.GetMessageSizesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "POP3 server does not report message sizes.");
                sizes = Array.Empty<int>();
            }

            // Newest first: a POP3 mailbox lists its messages oldest first.
            for (var k = 0; k < scanned; k++)
            {
                ct.ThrowIfCancellationRequested();

                var i = total - 1 - k;
                if (i < sizes.Count && sizes[i] > Pop3PreviewMaxBytes)
                {
                    continue;
                }

                if (since is DateTime cutoff)
                {
                    var headers = await client.GetMessageHeadersAsync(i, ct).ConfigureAwait(false);
                    var dateText = headers[MimeKit.HeaderId.Date];
                    if (dateText is not null && MimeKit.Utils.DateUtils.TryParse(dateText, out var sent) && sent.UtcDateTime < cutoff)
                    {
                        continue;
                    }
                }

                var message = await client.GetMessageAsync(i, ct).ConfigureAwait(false);
                if (!ImportRunner.MessageMatches(message, settings, senderRegex, subjectRegex))
                {
                    continue;
                }

                var attachments = AttachmentNames(message, extensions);
                if (attachments.Count == 0 && !settings.ImportBodyAsPdf)
                {
                    continue;
                }

                matches++;
                if (items.Count < MaxPreviewItems)
                {
                    items.Add(ToPreviewItem(message, attachments, settings.ImportBodyAsPdf));
                }
            }

            return new PreviewResult(true, null, matches, items, scanned, total > scanned);
        }
        finally
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(true, ct).ConfigureAwait(false);
            }
        }
    }

    private static List<string> AttachmentNames(MimeKit.MimeMessage message, IReadOnlyCollection<string> extensions)
    {
        var names = new List<string>();
        foreach (var attachment in message.Attachments)
        {
            var fileName = attachment.ContentDisposition?.FileName ?? attachment.ContentType?.Name;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext.Length > 0 && extensions.Contains(ext))
            {
                names.Add(fileName);
            }
        }

        return names;
    }

    private static PreviewItem ToPreviewItem(MimeKit.MimeMessage message, List<string> attachments, bool bodyAsPdf)
    {
        var extra = attachments.Count > 0 ? string.Join(", ", attachments) : null;
        if (bodyAsPdf)
        {
            extra = string.IsNullOrEmpty(extra) ? "PDF" : extra + " + PDF";
        }

        // A message without a Date header reports MinValue — show nothing instead of year 1.
        var date = message.Date == default ? (DateTime?)null : message.Date.UtcDateTime;

        return new PreviewItem(
            string.IsNullOrWhiteSpace(message.Subject) ? "(no subject)" : message.Subject,
            message.From?.ToString(),
            date,
            extra);
    }

    /// <summary>
    /// Resolves the mailbox exactly as the runner will, so browse and preview sign in the same
    /// way the import will: a connection (password or OAuth) wins, else the legacy inline login,
    /// with a freshly typed password allowed to stand in for an unsaved legacy task.
    /// </summary>
    private async Task<ResolvedMail> ResolveMailAsync(
        MailImportSettings settings, string? plaintextPassword, CancellationToken ct)
    {
        var mail = await _connections.ResolveMailAsync(settings, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(plaintextPassword) && mail.Connection is null)
        {
            mail = mail with { Password = plaintextPassword };
        }

        return mail;
    }

    // ----- SMB --------------------------------------------------------------

    /// <summary>Lists the shares the server offers.</summary>
    public async Task<IReadOnlyList<BrowseEntry>> ListSmbSharesAsync(
        SmbImportSettings settings, string? plaintextPassword, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var login = await _connections.ResolveSmbAsync(settings, plaintextPassword, ct).ConfigureAwait(false);

        return await Task.Run(() => SmbSession
            .ListShares(login.Host, login.Domain, login.Username, login.Password)
            .Select(s => new BrowseEntry(s, s, null, true))
            .ToList(), ct).WaitAsync(TimeSpan.FromSeconds(NetworkBudgetSeconds), ct).ConfigureAwait(false);
    }

    /// <summary>Lists the subfolders of <paramref name="path"/> on the configured share.</summary>
    public async Task<IReadOnlyList<BrowseEntry>> ListSmbFoldersAsync(
        SmbImportSettings settings, string? plaintextPassword, string? path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var share = await _connections.ResolveSmbAsync(settings, plaintextPassword, ct).ConfigureAwait(false);
        var pattern = settings.Pattern;

        return await Task.Run(() =>
        {
            using var session = SmbSession.Connect(share);

            var basePath = SmbSession.Norm(path);
            return session.ListDirectories(basePath)
                .Select(name =>
                {
                    var full = string.IsNullOrEmpty(basePath) ? name : basePath + "\\" + name;
                    // One round trip per entry for the file count; whether it has
                    // subfolders is left open (the picker shows "nothing found" if not)
                    // so listing a folder does not cost two calls per child.
                    var count = session.CountFiles(full, pattern);
                    return new BrowseEntry(full.Replace('\\', '/'), name, count, true);
                })
                .ToList();
        }, ct).WaitAsync(TimeSpan.FromSeconds(NetworkBudgetSeconds), ct).ConfigureAwait(false);
    }

    /// <summary>Lists the files an SMB import would pick up right now.</summary>
    public async Task<PreviewResult> PreviewSmbAsync(
        SmbImportSettings settings, string? plaintextPassword, int window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var share = await _connections.ResolveSmbAsync(settings, plaintextPassword, ct).ConfigureAwait(false);

            return await Task.Run(() =>
            {
                using var session = SmbSession.Connect(share);

                // Stop walking the share once the scan cap is reached — a deep archive
                // must not be enumerated in full just to show the first rows.
                var listed = session.ListEntries(settings.Path, settings.Pattern, settings.Recursive, window);
                var since = ImportSync.Cutoff(settings.LookbackMode, settings.LookbackDays, settings.LookbackDate, DateTime.UtcNow);
                var entries = (since is DateTime cutoff ? listed.Where(e => e.ModifiedUtc >= cutoff) : listed)
                    .OrderByDescending(e => e.ModifiedUtc)
                    .ToList();
                var items = entries
                    .Take(MaxPreviewItems)
                    .Select(e => new PreviewItem(
                        e.Path.Contains('\\') ? e.Path[(e.Path.LastIndexOf('\\') + 1)..] : e.Path,
                        e.Path.Replace('\\', '/'),
                        e.ModifiedUtc,
                        FormatBytes(e.Size)))
                    .ToList();

                return new PreviewResult(true, null, entries.Count, items, listed.Count, listed.Count >= window);
            }, ct).WaitAsync(TimeSpan.FromSeconds(NetworkBudgetSeconds), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PreviewResult(false, ex.Message, 0, Array.Empty<PreviewItem>(), 0);
        }
    }

    // ----- Local filesystem -------------------------------------------------

    /// <summary>Lists the subfolders of a local path (the container's view).</summary>
    public Task<IReadOnlyList<BrowseEntry>> ListLocalFoldersAsync(string? path, string? pattern, CancellationToken ct)
    {
        var root = string.IsNullOrWhiteSpace(path) ? "/" : path;

        return Task.Run<IReadOnlyList<BrowseEntry>>(() =>
        {
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"Folder '{root}' does not exist inside the container.");
            }

            var entries = new List<BrowseEntry>();
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith('.'))
                    {
                        continue;
                    }

                    var count = Directory.EnumerateFiles(dir, string.IsNullOrWhiteSpace(pattern) ? "*" : pattern).Count();
                    var children = Directory.EnumerateDirectories(dir).Any();
                    entries.Add(new BrowseEntry(dir.Replace('\\', '/'), name, count, children));
                }
                catch (UnauthorizedAccessException)
                {
                    // not readable: skip
                }
                catch (IOException)
                {
                    // vanished mid-enumeration: skip
                }
            }

            entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return entries;
        }, ct);
    }

    /// <summary>Lists the files a filesystem import would pick up right now.</summary>
    public Task<PreviewResult> PreviewLocalAsync(FilesystemImportSettings settings, int window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(settings.SourcePath) || !Directory.Exists(settings.SourcePath))
                {
                    return new PreviewResult(false, $"Folder '{settings.SourcePath}' does not exist inside the container.", 0, Array.Empty<PreviewItem>(), 0);
                }

                var pattern = string.IsNullOrWhiteSpace(settings.Pattern) ? "*" : settings.Pattern;
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = settings.Recursive,
                    IgnoreInaccessible = true,
                    MaxRecursionDepth = 12
                };

                // Walk lazily and stop at the scan cap: pointing a preview at a huge tree
                // must not enumerate the whole container before showing the first rows.
                var since = ImportSync.Cutoff(settings.LookbackMode, settings.LookbackDays, settings.LookbackDate, DateTime.UtcNow);
                var matches = new List<PreviewItem>();
                var scanned = 0;
                foreach (var file in Directory.EnumerateFiles(settings.SourcePath, pattern, options))
                {
                    ct.ThrowIfCancellationRequested();
                    scanned++;

                    try
                    {
                        var info = new FileInfo(file);
                        if (since is not DateTime cutoff || info.LastWriteTimeUtc >= cutoff)
                        {
                            matches.Add(new PreviewItem(info.Name, file, info.LastWriteTimeUtc, FormatBytes(info.Length)));
                        }
                    }
                    catch (IOException)
                    {
                        // vanished mid-enumeration: skip
                    }

                    if (scanned >= window)
                    {
                        break;
                    }
                }

                var items = matches.OrderByDescending(i => i.Date).Take(MaxPreviewItems).ToList();
                return new PreviewResult(true, null, matches.Count, items, scanned, scanned >= window);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new PreviewResult(false, ex.Message, 0, Array.Empty<PreviewItem>(), 0);
            }
        }, ct);
    }

    // ----- Shared -----------------------------------------------------------

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return (bytes / (1024d * 1024)).ToString("0.#") + " MB";
        }
        if (bytes >= 1024)
        {
            return (bytes / 1024d).ToString("0") + " KB";
        }

        return bytes + " B";
    }
}
