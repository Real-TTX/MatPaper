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
public sealed record PreviewResult(bool Ok, string? Error, int TotalMatches, IReadOnlyList<PreviewItem> Items, int Scanned);

/// <summary>
/// Read-only inspection of an import source for the wizard: lists mail folders, SMB shares
/// and folders or local subfolders, and previews which items an import would pick up.
/// Nothing here writes, deletes or marks anything on the source.
/// </summary>
public sealed class ImportBrowser
{
    private const int PreviewLimit = 25;
    private const int ScanLimit = 200;

    private readonly AppDbContext _db;
    private readonly SecretProtector _secrets;
    private readonly ILogger<ImportBrowser> _logger;

    public ImportBrowser(AppDbContext db, SecretProtector secrets, ILogger<ImportBrowser> logger)
    {
        _db = db;
        _secrets = secrets;
        _logger = logger;
    }

    // ----- Mail -------------------------------------------------------------

    /// <summary>Lists the mailbox folders with their message counts (IMAP only).</summary>
    public async Task<IReadOnlyList<BrowseEntry>> ListMailFoldersAsync(
        MailImportSettings settings, string? plaintextPassword, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var (user, password) = await ResolveMailLoginAsync(settings, plaintextPassword, ct).ConfigureAwait(false);

        using var client = new ImapClient();
        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, ImportRunner.SecureOption(settings.UseSsl), ct).ConfigureAwait(false);
            await client.AuthenticateAsync(user, password, ct).ConfigureAwait(false);

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
            var (user, password) = await ResolveMailLoginAsync(settings, plaintextPassword, ct).ConfigureAwait(false);
            var extensions = ImportRunner.ParseExtensions(settings.AttachmentExtensions);
            var senderRegex = ImportRunner.CompileRegex(settings.SenderRegex);
            var subjectRegex = ImportRunner.CompileRegex(settings.SubjectRegex);

            return isPop3
                ? await PreviewPop3Async(settings, user, password, extensions, senderRegex, subjectRegex, ct).ConfigureAwait(false)
                : await PreviewImapAsync(settings, user, password, extensions, senderRegex, subjectRegex, ct).ConfigureAwait(false);
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
        MailImportSettings settings, string user, string password,
        IReadOnlyCollection<string> extensions, Regex? senderRegex, Regex? subjectRegex, CancellationToken ct)
    {
        using var client = new ImapClient();
        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, ImportRunner.SecureOption(settings.UseSsl), ct).ConfigureAwait(false);
            await client.AuthenticateAsync(user, password, ct).ConfigureAwait(false);

            var folder = string.IsNullOrWhiteSpace(settings.Folder) || settings.Folder.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
                ? client.Inbox
                : await client.GetFolderAsync(settings.Folder, ct).ConfigureAwait(false);

            await folder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

            // Same selection the runner uses: unread messages only.
            var uids = await folder.SearchAsync(MailKit.Search.SearchQuery.NotSeen, ct).ConfigureAwait(false);
            var window = uids.Take(ScanLimit).ToList();

            var items = new List<PreviewItem>();
            var matches = 0;

            if (window.Count > 0)
            {
                // Envelope + structure only: downloading the body would set \Seen on the
                // server and the real import (which reads unseen mail) would skip them.
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

            return new PreviewResult(true, null, matches, items, window.Count);
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
        MailImportSettings settings, string user, string password,
        IReadOnlyCollection<string> extensions, Regex? senderRegex, Regex? subjectRegex, CancellationToken ct)
    {
        using var client = new Pop3Client();
        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, ImportRunner.SecureOption(settings.UseSsl), ct).ConfigureAwait(false);
            await client.AuthenticateAsync(user, password, ct).ConfigureAwait(false);

            var items = new List<PreviewItem>();
            var matches = 0;
            var total = await client.GetMessageCountAsync(ct).ConfigureAwait(false);
            var scanned = Math.Min(total, ScanLimit);

            for (var i = 0; i < scanned; i++)
            {
                ct.ThrowIfCancellationRequested();

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

            return new PreviewResult(true, null, matches, items, scanned);
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

    private async Task<(string User, string Password)> ResolveMailLoginAsync(
        MailImportSettings settings, string? plaintextPassword, CancellationToken ct)
    {
        var user = settings.Username;
        var password = string.IsNullOrEmpty(plaintextPassword)
            ? _secrets.Unprotect(settings.ProtectedPassword)
            : plaintextPassword;

        if (string.IsNullOrEmpty(plaintextPassword) && settings.CredentialId is long id)
        {
            var cred = await LoadCredentialAsync(id, ct).ConfigureAwait(false);
            if (cred is not null)
            {
                user = cred.Username;
                password = _secrets.Unprotect(cred.ProtectedPassword);
            }
        }

        return (user, password);
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
            .ToList(), ct).ConfigureAwait(false);
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
                    var count = session.CountFiles(full, pattern);
                    var children = session.ListDirectories(full).Count > 0;
                    return new BrowseEntry(full.Replace('\\', '/'), name, count, children);
                })
                .ToList();
        }, ct).ConfigureAwait(false);
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

                var entries = session.ListEntries(settings.Path, settings.Pattern, settings.Recursive);
                var items = entries
                    .Take(PreviewLimit)
                    .Select(e => new PreviewItem(
                        e.Path.Contains('\\') ? e.Path[(e.Path.LastIndexOf('\\') + 1)..] : e.Path,
                        e.Path.Replace('\\', '/'),
                        e.ModifiedUtc,
                        FormatBytes(e.Size)))
                    .ToList();

                return new PreviewResult(true, null, entries.Count, items, entries.Count);
            }, ct).ConfigureAwait(false);
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

    private async Task<(string User, string Password, string? Domain)> ResolveSmbLoginAsync(
        SmbImportSettings settings, string? plaintextPassword, CancellationToken ct)
    {
        var user = settings.Username;
        var domain = settings.Domain;
        var password = string.IsNullOrEmpty(plaintextPassword)
            ? _secrets.Unprotect(settings.ProtectedPassword)
            : plaintextPassword;

        if (string.IsNullOrEmpty(plaintextPassword) && settings.CredentialId is long id)
        {
            var cred = await LoadCredentialAsync(id, ct).ConfigureAwait(false);
            if (cred is not null)
            {
                user = cred.Username;
                domain = cred.Domain;
                password = _secrets.Unprotect(cred.ProtectedPassword);
            }
        }

        return (user, password, domain);
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

                var option = settings.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var pattern = string.IsNullOrWhiteSpace(settings.Pattern) ? "*" : settings.Pattern;

                var files = Directory.EnumerateFiles(settings.SourcePath, pattern, option).ToList();
                var items = new List<PreviewItem>();
                foreach (var file in files.Take(PreviewLimit))
                {
                    ct.ThrowIfCancellationRequested();
                    var info = new FileInfo(file);
                    items.Add(new PreviewItem(info.Name, file, info.LastWriteTimeUtc, FormatBytes(info.Length)));
                }

                return new PreviewResult(true, null, files.Count, items, files.Count);
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
