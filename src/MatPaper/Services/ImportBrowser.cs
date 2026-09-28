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
    private const int PreviewLimit = 25;
    private const int ScanLimit = 200;

    /// <summary>Per-operation network timeout for the wizard (MailKit defaults to two minutes).</summary>
    private const int MailTimeoutMs = 20000;

    /// <summary>Messages above this size are skipped by the POP3 preview (it must download them).</summary>
    private const long Pop3PreviewMaxBytes = 25L * 1024 * 1024;

    /// <summary>
    /// Hard budget for one SMB browse/preview. SMBLibrary blocks without a timeout, so the
    /// request gives up and reports an error instead of holding the page forever.
    /// </summary>
    private const int NetworkBudgetSeconds = 30;

    private readonly AppDbContext _db;
    private readonly SecretProtector _secrets;
    private readonly ConnectionService _connections;
    private readonly ILogger<ImportBrowser> _logger;

    public ImportBrowser(AppDbContext db, SecretProtector secrets, ConnectionService connections, ILogger<ImportBrowser> logger)
    {
        _db = db;
        _secrets = secrets;
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

            var root = client.PersonalNamespaces.Count > 0
                ? client.GetFolders(client.PersonalNamespaces[0], false, ct)
                : new List<IMailFolder>();

            var entries = new List<BrowseEntry>();
            foreach (var folder in root)
            {
                ct.ThrowIfCancellationRequested();

                if ((folder.Attributes & FolderAttributes.NonExistent) != 0)
                {
                    continue;
                }

                int? count = null;
                try
                {
                    await folder.StatusAsync(StatusItems.Count, ct).ConfigureAwait(false);
                    count = folder.Count;
                }
                catch (Exception ex)
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
        MailImportSettings settings, bool isPop3, string? plaintextPassword, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var mail = await ResolveMailAsync(settings, plaintextPassword, ct).ConfigureAwait(false);
            var extensions = ImportRunner.ParseExtensions(settings.AttachmentExtensions);
            var senderRegex = ImportRunner.CompileRegex(settings.SenderRegex);
            var subjectRegex = ImportRunner.CompileRegex(settings.SubjectRegex);

            return isPop3
                ? await PreviewPop3Async(settings, mail, extensions, senderRegex, subjectRegex, ct).ConfigureAwait(false)
                : await PreviewImapAsync(settings, mail, extensions, senderRegex, subjectRegex, ct).ConfigureAwait(false);
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
        IReadOnlyCollection<string> extensions, Regex? senderRegex, Regex? subjectRegex, CancellationToken ct)
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

            // Exactly the selection RunImapAsync uses, so the preview cannot promise
            // more or less than the import; newest first, capped at ScanLimit.
            var uids = await folder.SearchAsync(MailKit.Search.SearchQuery.All, ct).ConfigureAwait(false);
            var window = uids.Reverse().Take(ScanLimit).ToList();

            var items = new List<PreviewItem>();
            var matches = 0;

            if (window.Count > 0)
            {
                // Envelope + structure only: downloading the body would set \Seen on the
                // server, which the post-action "mark seen" is supposed to decide.
                var summaries = await folder
                    .FetchAsync(window, MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure, ct)
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
                    if (items.Count < PreviewLimit)
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

            return new PreviewResult(true, null, matches, items, window.Count, uids.Count > window.Count);
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
        IReadOnlyCollection<string> extensions, Regex? senderRegex, Regex? subjectRegex, CancellationToken ct)
    {
        using var client = new Pop3Client { Timeout = MailTimeoutMs };
        try
        {
            await client.ConnectAsync(mail.Host, mail.Port, ImportRunner.SecureOption(mail.UseSsl), ct).ConfigureAwait(false);
            await _connections.AuthenticateAsync(client, mail, ct).ConfigureAwait(false);

            var items = new List<PreviewItem>();
            var matches = 0;
            var total = await client.GetMessageCountAsync(ct).ConfigureAwait(false);
            var scanned = Math.Min(total, ScanLimit);

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

            for (var i = 0; i < scanned; i++)
            {
                ct.ThrowIfCancellationRequested();

                if (i < sizes.Count && sizes[i] > Pop3PreviewMaxBytes)
                {
                    continue;
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
                if (items.Count < PreviewLimit)
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

        var login = await ResolveSmbLoginAsync(settings, plaintextPassword, ct).ConfigureAwait(false);

        return await Task.Run(() => SmbSession
            .ListShares(settings.Host, login.Domain, login.User, login.Password)
            .Select(s => new BrowseEntry(s, s, null, true))
            .ToList(), ct).WaitAsync(TimeSpan.FromSeconds(NetworkBudgetSeconds), ct).ConfigureAwait(false);
    }

    /// <summary>Lists the subfolders of <paramref name="path"/> on the configured share.</summary>
    public async Task<IReadOnlyList<BrowseEntry>> ListSmbFoldersAsync(
        SmbImportSettings settings, string? plaintextPassword, string? path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var login = await ResolveSmbLoginAsync(settings, plaintextPassword, ct).ConfigureAwait(false);
        var pattern = settings.Pattern;

        return await Task.Run(() =>
        {
            using var session = SmbSession.Connect(
                new SmbConnection(settings.Host, settings.Share, login.Domain, login.User, login.Password));

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
        SmbImportSettings settings, string? plaintextPassword, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var login = await ResolveSmbLoginAsync(settings, plaintextPassword, ct).ConfigureAwait(false);

            return await Task.Run(() =>
            {
                using var session = SmbSession.Connect(
                    new SmbConnection(settings.Host, settings.Share, login.Domain, login.User, login.Password));

                // Stop walking the share once the scan cap is reached — a deep archive
                // must not be enumerated in full just to show the first rows.
                var entries = session.ListEntries(settings.Path, settings.Pattern, settings.Recursive, ScanLimit);
                var items = entries
                    .Take(PreviewLimit)
                    .Select(e => new PreviewItem(
                        e.Path.Contains('\\') ? e.Path[(e.Path.LastIndexOf('\\') + 1)..] : e.Path,
                        e.Path.Replace('\\', '/'),
                        e.ModifiedUtc,
                        FormatBytes(e.Size)))
                    .ToList();

                return new PreviewResult(true, null, entries.Count, items, entries.Count, entries.Count >= ScanLimit);
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

    /// <summary>Saved credential first, exactly like <see cref="ImportRunner"/> resolves it.</summary>
    private async Task<(string User, string Password, string? Domain)> ResolveSmbLoginAsync(
        SmbImportSettings settings, string? plaintextPassword, CancellationToken ct)
    {
        if (settings.CredentialId is long id)
        {
            var cred = await LoadCredentialAsync(id, ct).ConfigureAwait(false);
            if (cred is not null)
            {
                return (cred.Username, _secrets.Unprotect(cred.ProtectedPassword), cred.Domain);
            }
        }

        var password = string.IsNullOrEmpty(plaintextPassword)
            ? _secrets.Unprotect(settings.ProtectedPassword)
            : plaintextPassword;

        return (settings.Username, password, settings.Domain);
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
    public Task<PreviewResult> PreviewLocalAsync(FilesystemImportSettings settings, CancellationToken ct)
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
                var items = new List<PreviewItem>();
                var found = 0;
                foreach (var file in Directory.EnumerateFiles(settings.SourcePath, pattern, options))
                {
                    ct.ThrowIfCancellationRequested();
                    found++;

                    if (items.Count < PreviewLimit)
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            items.Add(new PreviewItem(info.Name, file, info.LastWriteTimeUtc, FormatBytes(info.Length)));
                        }
                        catch (IOException)
                        {
                            // vanished mid-enumeration: skip
                        }
                    }

                    if (found >= ScanLimit)
                    {
                        break;
                    }
                }

                return new PreviewResult(true, null, found, items, found, found >= ScanLimit);
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

    private async Task<Credential?> LoadCredentialAsync(long id, CancellationToken ct)
        => await _db.Credentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.UpdateState != UpdateState.Deleted, ct)
            .ConfigureAwait(false);

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
