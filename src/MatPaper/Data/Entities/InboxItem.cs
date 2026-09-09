namespace MatPaper.Data;

/// <summary>
/// A file discovered inside a <see cref="StorageLocation"/> that is not yet registered
/// as a <see cref="Document"/>. Populated by a storage scan and cleared when the file is
/// imported (becomes a Document) or dismissed.
/// </summary>
public class InboxItem : BaseEntity
{
    public long StorageLocationId { get; set; }

    /// <summary>Path of the file relative to the storage location root (forward slashes).</summary>
    public string RelativePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public long FileSize { get; set; }

    /// <summary>SHA-256 (lowercase hex) of the file content, computed during the scan.</summary>
    public string? ContentHash { get; set; }

    public DateTime FileModifiedUtc { get; set; }

    /// <summary>Created = pending in the inbox; Deleted = dismissed/imported (hidden from the inbox).</summary>
    public UpdateState UpdateState { get; set; }

    public StorageLocation? StorageLocation { get; set; }
}
