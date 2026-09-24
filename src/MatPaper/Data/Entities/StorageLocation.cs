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
    public long? CredentialId { get; set; }

    public string PathTemplate { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public UpdateState UpdateState { get; set; }

    public Credential? Credential { get; set; }

    /// <summary>Human-readable root, e.g. "/storage" or "\\nas\docs\archive".</summary>
    public string DisplayRoot
    {
        get
        {
            if (Kind != StorageKind.Smb)
            {
                return RootPath;
            }

            var root = "\\\\" + (SmbHost ?? "?") + "\\" + (SmbShare ?? "?");
            var sub = (SmbPath ?? string.Empty).Trim().Trim('/', '\\').Replace('/', '\\');
            return sub.Length == 0 ? root : root + "\\" + sub;
        }
    }
}
