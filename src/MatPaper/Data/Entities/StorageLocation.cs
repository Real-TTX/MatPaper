using System.Text.Json;

namespace MatPaper.Data;

/// <summary>
/// A place where filed documents live: either a local folder (<see cref="StorageKind.Local"/>,
/// <see cref="RootPath"/>) or an SMB/CIFS share (<see cref="StorageKind.Smb"/>: host, share,
/// optional folder, plus a saved <see cref="Credential"/>). Documents in the review inbox are
/// NOT stored here yet — they stay in the local staging area until they are confirmed and filed.
/// </summary>
public class StorageLocation : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public StorageKind Kind { get; set; } = StorageKind.Local;

    /// <summary>Absolute folder path (local locations only).</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>SMB host name or IP (SMB locations only).</summary>
    public string? SmbHost { get; set; }

    /// <summary>SMB share name (SMB locations only).</summary>
    public string? SmbShare { get; set; }

    /// <summary>Optional folder inside the share that acts as the root (SMB locations only).</summary>
    public string? SmbPath { get; set; }

    /// <summary>Saved credential used to sign in to the share (SMB locations only).</summary>
    /// <remarks>Legacy: superseded by <see cref="ConnectionId"/>; kept until the cleanup migration.</remarks>
    public long? CredentialId { get; set; }

    /// <summary>
    /// The connection that provides this locations endpoint and sign-in (SMB, Drive, OneDrive).
    /// Null for a local folder. Replaces the inline SMB host/share/credential fields.
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

    public Credential? Credential { get; set; }

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

            // Connection-backed locations keep host/share in the connection and the sub-folder
            // in BasePath; older ones still carry the inline fields.
            var host = SmbHost;
            var share = SmbShare;
            var sub = SmbPath;
            if (Connection is not null)
            {
                try
                {
                    var endpoint = JsonSerializer.Deserialize<SmbEndpoint>(Connection.SettingsJson);
                    host = endpoint?.Host;
                    share = endpoint?.Share;
                }
                catch (JsonException)
                {
                    // fall back to whatever the legacy fields hold
                }

                sub = BasePath;
            }

            var root = "\\\\" + (string.IsNullOrWhiteSpace(host) ? "?" : host) + "\\" + (string.IsNullOrWhiteSpace(share) ? "?" : share);
            var cleaned = (sub ?? string.Empty).Trim().Trim('/', '\\').Replace('/', '\\');
            return cleaned.Length == 0 ? root : root + "\\" + cleaned;
        }
    }
}
