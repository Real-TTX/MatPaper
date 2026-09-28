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

    /// <summary>Optional reference to a reusable Credential; overrides Username/ProtectedPassword when set.</summary>
    /// <remarks>Legacy: superseded by <see cref="ConnectionId"/>.</remarks>
    public long? CredentialId { get; set; }

    /// <summary>The connection that provides the mailbox endpoint and sign-in.</summary>
    public long? ConnectionId { get; set; }

    public string Folder { get; set; } = "INBOX";

    /// <summary>Friendly "contains" filters (comma-separated, any match). Empty = no filter.</summary>
    public string? FromFilter { get; set; }
    public string? ToFilter { get; set; }
    public string? SubjectFilter { get; set; }

    /// <summary>Optional advanced regex filters (applied in addition to the contains filters).</summary>
    public string? SenderRegex { get; set; }
    public string? SubjectRegex { get; set; }

    public string AttachmentExtensions { get; set; } = ".pdf,.png,.jpg,.jpeg,.tif,.tiff";

    /// <summary>Also render the e-mail body itself into an archival PDF and import it.</summary>
    public bool ImportBodyAsPdf { get; set; } = false;

    /// <summary>markseen|delete|none|move (move is IMAP-only, into <see cref="MoveToFolder"/>).</summary>
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

    /// <summary>Optional reference to a reusable Credential; overrides Username/Domain/ProtectedPassword when set.</summary>
    /// <remarks>Legacy: superseded by <see cref="ConnectionId"/>.</remarks>
    public long? CredentialId { get; set; }

    /// <summary>The connection that provides the share endpoint and sign-in.</summary>
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
public record TaskTrigger(TaskRunKind Kind, long TaskId, long? ActingUserId = null);

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
    public bool Enqueue(TaskRunKind kind, long taskId, long? actingUserId = null)
    {
        if (!_busy.TryAdd((kind, taskId), 0))
        {
            return false;
        }

        if (_channel.Writer.TryWrite(new TaskTrigger(kind, taskId, actingUserId)))
        {
            return true;
        }

        _busy.TryRemove((kind, taskId), out _);
        return false;
    }

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
