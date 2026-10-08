using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using MatPaper.Data;

namespace MatPaper.Services;

/// <summary>
/// Result of a single import/export run.
/// </summary>
public sealed record RunReport(bool Success, int ItemsProcessed, string Log);

/// <summary>
/// Settings for a filesystem-based import task. Serialized to <see cref="ImportTask.SettingsJson"/>.
/// </summary>
public class FilesystemImportSettings
{
    public string SourcePath { get; set; } = "";
    public string Pattern { get; set; } = "*";
    public bool Recursive { get; set; }

    /// <summary>none|delete|move</summary>
    public string PostAction { get; set; } = "none";
    public string? MoveToPath { get; set; }

    public long? StorageLocationId { get; set; }
    public long? CorrespondentId { get; set; }
    public long? DocumentTypeId { get; set; }
    public long? ProjectId { get; set; }
    public List<long> TagIds { get; set; } = new();

    /// <summary>Skip the review inbox: mark imported documents as reviewed immediately.</summary>
    public bool SkipInbox { get; set; }

    /// <summary>Whose review inbox the documents land in. Null = the task creator.</summary>
    public long? OwnerUserId { get; set; }

    /// <summary>Put imported documents into the common area (visible to everyone).</summary>
    public bool IsCommon { get; set; }

    /// <summary>Which items a run considers: all | days (the last N days) | date (from a given day on).</summary>
    public string LookbackMode { get; set; } = "all";
    public int LookbackDays { get; set; } = 30;
    public DateTime? LookbackDate { get; set; }

    /// <summary>Import at most this many documents per run (0 = no limit). The next run continues where this one stopped - handy for testing.</summary>
    public int MaxPerRun { get; set; }
}

/// <summary>
/// Settings for an IMAP/POP3 import task. Serialized to <see cref="ImportTask.SettingsJson"/>.
/// </summary>
public class MailImportSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";

    /// <summary>The connection that provides the mailbox endpoint and sign-in; null = the inline fields above.</summary>
    public long? ConnectionId { get; set; }

    public string Folder { get; set; } = "INBOX";

    /// <summary>Friendly "contains" filters (comma-separated, any match). Empty = no filter.</summary>
    public string? FromFilter { get; set; }
    public string? ToFilter { get; set; }
    public string? SubjectFilter { get; set; }

    /// <summary>IMAP only: take only mail that is still unread. The server does the filtering (SEARCH UNSEEN).</summary>
    public bool OnlyUnread { get; set; }

    /// <summary>Where the contains-filters run on IMAP: auto (the server, except Gmail) | server | local (headers are read and checked here).</summary>
    public string FilterWhere { get; set; } = "auto";

    /// <summary>Optional advanced regex filters (applied in addition to the contains filters).</summary>
    public string? SenderRegex { get; set; }
    public string? SubjectRegex { get; set; }

    public string AttachmentExtensions { get; set; } = ".pdf,.png,.jpg,.jpeg,.tif,.tiff,.zip";

    /// <summary>Also render the e-mail body itself into an archival PDF and import it.</summary>
    public bool ImportBodyAsPdf { get; set; } = false;

    /// <summary>
    /// What happens to a handled message: comma-separated, e.g. "markseen,flag,move". Tokens: markseen,
    /// flag (IMAP star), then at most one of move (IMAP, into <see cref="MoveToFolder"/>) or delete; "none" = nothing.
    /// An empty value means "markseen". See <see cref="MailPostActions"/>.
    /// </summary>
    public string PostAction { get; set; } = "markseen";

    /// <summary>Target IMAP folder for the "move" post-action.</summary>
    public string? MoveToFolder { get; set; }

    public long? StorageLocationId { get; set; }
    public long? CorrespondentId { get; set; }
    public long? DocumentTypeId { get; set; }
    public long? ProjectId { get; set; }
    public List<long> TagIds { get; set; } = new();

    /// <summary>Skip the review inbox: mark imported documents as reviewed immediately.</summary>
    public bool SkipInbox { get; set; }

    /// <summary>Whose review inbox the documents land in. Null = the task creator.</summary>
    public long? OwnerUserId { get; set; }

    /// <summary>Put imported documents into the common area (visible to everyone).</summary>
    public bool IsCommon { get; set; }

    /// <summary>Which items a run considers: all | days (the last N days) | date (from a given day on).</summary>
    public string LookbackMode { get; set; } = "all";
    public int LookbackDays { get; set; } = 30;
    public DateTime? LookbackDate { get; set; }

    /// <summary>Import at most this many documents per run (0 = no limit). The next run continues where this one stopped - handy for testing.</summary>
    public int MaxPerRun { get; set; }
}

/// <summary>
/// Settings for an SMB/CIFS network-share import task (in-app client, no host mount).
/// Serialized to <see cref="ImportTask.SettingsJson"/>.
/// </summary>
public class SmbImportSettings
{
    public string Host { get; set; } = "";
    public string Share { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Domain { get; set; }
    public string Username { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";

    /// <summary>
    /// The connection that provides the host and sign-in; null = the inline fields above.
    /// <see cref="Share"/> and <see cref="Path"/> stay per task either way.
    /// </summary>
    public long? ConnectionId { get; set; }

    public string Pattern { get; set; } = "*";
    public bool Recursive { get; set; }

    /// <summary>none|delete|move (move relocates the file into <see cref="MoveToPath"/> on the share).</summary>
    public string PostAction { get; set; } = "none";

    /// <summary>Target subfolder on the share for the "move" post-action.</summary>
    public string? MoveToPath { get; set; }

    public long? StorageLocationId { get; set; }
    public long? CorrespondentId { get; set; }
    public long? DocumentTypeId { get; set; }
    public long? ProjectId { get; set; }
    public List<long> TagIds { get; set; } = new();

    /// <summary>Skip the review inbox: mark imported documents as reviewed immediately.</summary>
    public bool SkipInbox { get; set; }

    /// <summary>Whose review inbox the documents land in. Null = the task creator.</summary>
    public long? OwnerUserId { get; set; }

    /// <summary>Put imported documents into the common area (visible to everyone).</summary>
    public bool IsCommon { get; set; }

    /// <summary>Which items a run considers: all | days (the last N days) | date (from a given day on).</summary>
    public string LookbackMode { get; set; } = "all";
    public int LookbackDays { get; set; } = 30;
    public DateTime? LookbackDate { get; set; }

    /// <summary>Import at most this many documents per run (0 = no limit). The next run continues where this one stopped - handy for testing.</summary>
    public int MaxPerRun { get; set; }
}

/// <summary>
/// The fields every import settings type shares. Lets the runner read ownership out of
/// <see cref="ImportTask.SettingsJson"/> without knowing the concrete settings type.
/// </summary>
public class CommonImportSettings
{
    public long? OwnerUserId { get; set; }
    public bool IsCommon { get; set; }
    public bool SkipInbox { get; set; }
}

/// <summary>
/// Settings for a backup export task. Serialized to <see cref="ExportTask.SettingsJson"/>.
/// </summary>
public class BackupSettings
{
    public string TargetPath { get; set; } = "";
    public bool IncludeConfig { get; set; } = true;
    public bool IncludeDocuments { get; set; }
    public int Retention { get; set; } = 7;
}

/// <summary>
/// Settings for a document export rule: which documents (all filters combine), where they are copied
/// to and under which path. Serialized to <see cref="ExportTask.SettingsJson"/>.
/// </summary>
public class DocumentExportSettings
{
    public List<long> TagIds { get; set; } = new();
    public long? CorrespondentId { get; set; }
    public long? DocumentTypeId { get; set; }
    public long? ProjectId { get; set; }
    public long? OwnerUserId { get; set; }

    /// <summary>Only documents stored in this location (and, if set, below <see cref="SourceFolder"/>).</summary>
    public long? SourceLocationId { get; set; }
    public string? SourceFolder { get; set; }

    /// <summary>The storage location the copies are written to.</summary>
    public long? TargetLocationId { get; set; }

    /// <summary>Path of a copy inside the target, with the same placeholders as a storage location.</summary>
    public string PathTemplate { get; set; } = "{Year}/{Title}{Ext}";
}

/// <summary>
/// Tolerant helpers for reading/writing task settings JSON.
/// </summary>
public static class TaskSettingsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static T Read<T>(string? json) where T : new()
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new T();
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>
/// A request to run a specific import or export task.
/// </summary>
public record TaskTrigger(TaskRunKind Kind, long TaskId, long? ActingUserId = null, string? Options = null);

/// <summary>
/// Unbounded in-memory queue of task triggers consumed by the scheduler service.
/// </summary>
public class TaskTriggerQueue
{
    private readonly Channel<TaskTrigger> _channel =
        Channel.CreateUnbounded<TaskTrigger>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    /// <summary>Tasks that are queued or running, so the same work is never started twice.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(TaskRunKind, long), byte> _busy = new();

    /// <summary>Queues a task run. False when one is already queued or running for that task.</summary>
    public bool Enqueue(TaskRunKind kind, long taskId, long? actingUserId = null, string? options = null)
    {
        if (!_busy.TryAdd((kind, taskId), 0))
        {
            return false;
        }

        if (_channel.Writer.TryWrite(new TaskTrigger(kind, taskId, actingUserId, options)))
        {
            return true;
        }

        _busy.TryRemove((kind, taskId), out _);
        return false;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(TaskRunKind, long), CancellationTokenSource> _running = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(TaskRunKind, long), byte> _cancelQueued = new();

    /// <summary>
    /// Stops a task: a run in progress is cancelled at its next checkpoint, a queued one is dropped before it
    /// starts. False when the task is neither running nor queued.
    /// </summary>
    public bool Cancel(TaskRunKind kind, long taskId)
    {
        if (_running.TryGetValue((kind, taskId), out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
            return true;
        }

        if (_busy.ContainsKey((kind, taskId)))
        {
            _cancelQueued.TryAdd((kind, taskId), 0);
            return true;
        }

        return false;
    }

    /// <summary>True once (and forgets it) when this queued task was cancelled before it started.</summary>
    public bool TakeQueuedCancel(TaskRunKind kind, long taskId) => _cancelQueued.TryRemove((kind, taskId), out _);

    /// <summary>The scheduler starts a run: a token that fires on shutdown or when a user cancels it.</summary>
    public CancellationTokenSource BeginRun(TaskRunKind kind, long taskId, CancellationToken appStopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(appStopping);
        _running[(kind, taskId)] = cts;
        return cts;
    }

    public void EndRun(TaskRunKind kind, long taskId) => _running.TryRemove((kind, taskId), out _);

    /// <summary>Marks a task as runnable again. Called by the scheduler when a run ends.</summary>
    public void Release(TaskRunKind kind, long taskId) => _busy.TryRemove((kind, taskId), out _);

    /// <summary>True while a run for this task is queued or in progress.</summary>
    public bool IsBusy(TaskRunKind kind, long taskId) => _busy.ContainsKey((kind, taskId));

    public ChannelReader<TaskTrigger> Reader => _channel.Reader;
}

/// <summary>
/// Protects/unprotects task secrets (e.g. mailbox passwords) using DataProtection.
/// </summary>
public class SecretProtector
{
    private const string Purpose = "MatPaper.TaskSecrets";
    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext ?? "");

    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return "";
        }

        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>The parsed form of <see cref="MailImportSettings.PostAction"/>: what to do with a handled mail.</summary>
public readonly record struct MailPostActions(bool MarkSeen, bool Flag, bool Move, bool Delete)
{
    public static MailPostActions Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new MailPostActions(true, false, false, false); // the historic default
        }

        var tokens = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()).ToHashSet();
        var delete = tokens.Contains("delete");
        return new MailPostActions(
            tokens.Contains("markseen") && !delete,
            tokens.Contains("flag") && !delete,
            tokens.Contains("move") && !delete,
            delete);
    }

    public string Format()
    {
        var tokens = new List<string>();
        if (MarkSeen) { tokens.Add("markseen"); }
        if (Flag) { tokens.Add("flag"); }
        if (Move) { tokens.Add("move"); }
        if (Delete) { tokens.Add("delete"); }
        return tokens.Count == 0 ? "none" : string.Join(',', tokens);
    }
}
