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
    public string Folder { get; set; } = "INBOX";
    public string? SenderRegex { get; set; }
    public string? SubjectRegex { get; set; }
    public string AttachmentExtensions { get; set; } = ".pdf,.png,.jpg,.jpeg,.tif,.tiff";

    /// <summary>Also render the e-mail body itself into an archival PDF and import it.</summary>
    public bool ImportBodyAsPdf { get; set; } = false;

    /// <summary>markseen|delete|none</summary>
    public string PostAction { get; set; } = "markseen";

    public long? StorageLocationId { get; set; }
    public long? CorrespondentId { get; set; }
    public long? DocumentTypeId { get; set; }
    public long? ProjectId { get; set; }
    public List<long> TagIds { get; set; } = new();
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
public record TaskTrigger(TaskRunKind Kind, long TaskId);

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

    public void Enqueue(TaskRunKind kind, long taskId) =>
        _channel.Writer.TryWrite(new TaskTrigger(kind, taskId));

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
