using System.Text.Json;

namespace MatPaper.Data;

/// <summary>
/// A place where filed documents live: a local folder (<see cref="StorageKind.Local"/>,
/// <see cref="RootPath"/>), or a <see cref="Connection"/>-backed remote — an SMB/CIFS share
/// (<see cref="StorageKind.Smb"/>) or a cloud drive (<see cref="StorageKind.Cloud"/>) — with an
/// optional <see cref="BasePath"/> sub-folder. Documents in the review inbox are NOT stored here
/// yet — they stay in the local staging area until they are confirmed and filed.
/// </summary>
public class StorageLocation : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public StorageKind Kind { get; set; } = StorageKind.Local;

    /// <summary>Absolute folder path (local locations only).</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// The connection that provides this location's endpoint and sign-in (SMB, Drive, OneDrive).
    /// Null for a local folder.
    /// </summary>
    public long? ConnectionId { get; set; }

    /// <summary>
    /// Folder within the connection that acts as this locations root: the sub-path in an SMB
    /// share, or a Drive/OneDrive folder. Empty means the connections own root.
    /// </summary>
    public string? BasePath { get; set; }

    public string PathTemplate { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public UpdateState UpdateState { get; set; }

    /// <summary>
    /// Owner assigned to documents a storage search finds here. Null = the user who
    /// started the search.
    /// </summary>
    public long? DefaultOwnerId { get; set; }

    /// <summary>Put documents found here into the shared common area.</summary>
    public bool DefaultIsCommon { get; set; }

    /// <summary>
    /// Comma-separated file extensions a storage search picks up (".pdf,.png"). Empty
    /// means every file.
    /// </summary>
    public string ScanExtensions { get; set; } = ".pdf,.png,.jpg,.jpeg,.tif,.tiff";

    /// <summary>
    /// Cron expression for an automatic storage search, or null/empty for manual only.
    /// Evaluated in the configured display time zone, like the import and export tasks.
    /// </summary>
    public string? ScanCron { get; set; }

    /// <summary>When the last storage search finished (UTC), or null if it never ran.</summary>
    public DateTime? LastScanUtc { get; set; }

    /// <summary>How many new documents the last storage search added.</summary>
    public int LastScanFound { get; set; }

    /// <summary>Error of the last storage search, or null when it succeeded.</summary>
    public string? LastScanError { get; set; }

    public Connection? Connection { get; set; }

    public User? DefaultOwner { get; set; }

    /// <summary>Human-readable root, e.g. "/storage" or "\\nas\docs\archive".</summary>
    public string DisplayRoot
    {
        get
        {
            if (Kind == StorageKind.Cloud)
            {
                var label = Connection?.Name ?? "?";
                var folder = (BasePath ?? string.Empty).Trim().Trim('/', '\\');
                return folder.Length == 0 ? label : label + " / " + folder.Replace('\\', '/');
            }

            if (Kind != StorageKind.Smb)
            {
                return RootPath;
            }

            // Host and share live in the connection, the sub-folder in BasePath.
            string? host = null;
            string? share = null;
            string? connectionFolder = null;
            if (Connection is not null)
            {
                try
                {
                    var endpoint = JsonSerializer.Deserialize<SmbEndpoint>(Connection.SettingsJson);
                    host = endpoint?.Host;
                    share = endpoint?.Share;
                    connectionFolder = endpoint?.Path;
                }
                catch (JsonException)
                {
                    // unreadable endpoint: show placeholders
                }
            }

            var root = "\\\\" + (string.IsNullOrWhiteSpace(host) ? "?" : host) + "\\" + (string.IsNullOrWhiteSpace(share) ? "?" : share);
            var cleaned = string.Join("\\", new[] { connectionFolder, BasePath }
                .Select(p => (p ?? string.Empty).Trim().Trim('/', '\\').Replace('/', '\\'))
                .Where(p => p.Length > 0));
            return cleaned.Length == 0 ? root : root + "\\" + cleaned;
        }
    }
}
