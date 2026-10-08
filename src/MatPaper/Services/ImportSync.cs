using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>
/// What an import task remembers between runs, so a run only looks at what is new:
/// <list type="bullet">
/// <item>IMAP: the folder's UIDVALIDITY and the highest UID handled. A UID only grows, so the
/// next run asks the server for UIDs above it. If the server renumbers the folder
/// (UIDVALIDITY changes) the stored UID is meaningless and the run starts over.</item>
/// <item>POP3 and files: a set of <see cref="Seen"/> identifiers (UIDL of a message, or a
/// fingerprint of path + size + modification time of a file). It only ever holds entries that
/// still exist on the server/folder, so it cannot grow beyond the mailbox or folder itself.</item>
/// </list>
/// The content hash of every file stays the safety net against duplicates; this state only
/// saves the work of fetching and hashing again. It is stored in <see cref="ImportTask.SyncState"/>
/// and cleared whenever the task's settings change.
/// </summary>
public sealed class ImportSyncState
{
    public long? UidValidity { get; set; }
    public long LastUid { get; set; }
    public List<string> Seen { get; set; } = new();
}

public static class ImportSync
{
    public const string ModeAll = "all";
    public const string ModeDays = "days";
    public const string ModeDate = "date";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static bool IsMode(string? mode) => mode is ModeAll or ModeDays or ModeDate;

    /// <summary>The oldest moment still wanted (UTC), or null for "everything".</summary>
    public static DateTime? Cutoff(string? mode, int days, DateTime? date, DateTime nowUtc) => mode switch
    {
        ModeDays when days > 0 => nowUtc.AddDays(-days),
        ModeDate when date is not null => DateTime.SpecifyKind(date.Value.Date, DateTimeKind.Utc),
        _ => null
    };

    public static ImportSyncState Load(ImportTask task)
    {
        if (string.IsNullOrWhiteSpace(task.SyncState))
        {
            return new ImportSyncState();
        }

        try
        {
            return JsonSerializer.Deserialize<ImportSyncState>(task.SyncState, Options) ?? new ImportSyncState();
        }
        catch (JsonException)
        {
            return new ImportSyncState();
        }
    }

    /// <summary>Stores the state without touching the tracked entity (the task may belong to another context).</summary>
    public static async Task SaveAsync(AppDbContext db, long taskId, ImportSyncState? state)
    {
        var json = state is null ? null : JsonSerializer.Serialize(state, Options);
        await db.ImportTasks
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.SyncState, json))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// "Start over", completely: forgets the position AND frees what this rule imported and the user deleted since.
    /// A deleted document otherwise stays a tombstone (so a mailbox that keeps its mail does not bring back what
    /// was thrown away); after a reset the user asked for exactly that, so the tombstones of this rule go.
    /// </summary>
    public static async Task ResetAsync(AppDbContext db, long taskId)
    {
        await SaveAsync(db, taskId, null).ConfigureAwait(false);
        await db.Documents
            .Where(d => d.ImportTaskId == taskId && d.UpdateState == UpdateState.Deleted)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ContentHash, (string?)null))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// What decides WHICH items a task picks up. When it changes (another folder, other filters, a
    /// different period) the remembered position no longer fits and the task starts over. Settings
    /// that only say what to do with an item (post-action, owner, tags …) are deliberately left out.
    /// </summary>
    public static string Signature(ImportTaskType type, string? settingsJson)
    {
        switch (type)
        {
            case ImportTaskType.Imap:
            case ImportTaskType.Pop3:
                var m = TaskSettingsJson.Read<MailImportSettings>(settingsJson);
                return string.Join("|", type, m.ConnectionId, m.Host, m.Port, m.UseSsl, m.Username, m.Folder, m.FromFilter, m.ToFilter,
                    m.SubjectFilter, m.OnlyUnread, m.SenderRegex, m.SubjectRegex, m.AttachmentExtensions, m.ImportBodyAsPdf, m.LookbackMode, m.LookbackDays, m.LookbackDate?.ToString("yyyy-MM-dd"));
            case ImportTaskType.Smb:
                var s = TaskSettingsJson.Read<SmbImportSettings>(settingsJson);
                return string.Join("|", type, s.ConnectionId, s.Host, s.Share, s.Path, s.Pattern, s.Recursive, s.LookbackMode, s.LookbackDays, s.LookbackDate?.ToString("yyyy-MM-dd"));
            default:
                var f = TaskSettingsJson.Read<FilesystemImportSettings>(settingsJson);
                return string.Join("|", type, f.SourcePath, f.Pattern, f.Recursive, f.LookbackMode, f.LookbackDays, f.LookbackDate?.ToString("yyyy-MM-dd"));
        }
    }

    /// <summary>A short, stable identifier for one file in one state (changes when its size or time changes).</summary>
    public static string Fingerprint(string path, long size, DateTime modifiedUtc)
    {
        var raw = $"{path}|{size}|{modifiedUtc.Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)), 0, 8);
    }
}
