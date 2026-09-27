namespace MatPaper.Data;

public enum UpdateState
{
    Deleted = 0,
    Created = 1,
    Updated = 2
}

public enum OcrState
{
    Pending = 0,
    Done = 1,
    Failed = 2,

    /// <summary>
    /// Never queued for analysis. Files discovered by a storage search start here so a
    /// search over thousands of files costs nothing; the user starts OCR explicitly.
    /// </summary>
    Deferred = 3
}

/// <summary>
/// Whether a document still needs a human to review the auto-suggested
/// metadata (type, correspondent, date, tags) in the inbox, or has been
/// confirmed ("gelesen"/erledigt).
/// </summary>
public enum ReviewState
{
    Pending = 0,
    Reviewed = 1,

    /// <summary>
    /// A known file that is deliberately not managed by MatPaper. Used for files a storage
    /// search discovered and the user waved away; the file itself is never touched, and the
    /// row keeps the next search from offering it again.
    /// </summary>
    Ignored = 2
}

/// <summary>How a document entered MatPaper. Shown as a badge in the inbox.</summary>
public enum DocumentOrigin
{
    Upload = 0,
    Mail = 1,
    ImportFolder = 2,
    CameraScan = 3,

    /// <summary>Found by a storage search; the file already lay inside the storage location.</summary>
    StorageScan = 4
}

/// <summary>Where a <see cref="StorageLocation"/> keeps its files.</summary>
public enum StorageKind
{
    /// <summary>A folder on the local filesystem (or a mount inside the container).</summary>
    Local = 0,

    /// <summary>An SMB/CIFS network share reached directly over the network.</summary>
    Smb = 1
}

public enum ImportTaskType
{
    Imap = 0,
    Pop3 = 1,
    Filesystem = 2,
    Smb = 3
}

public enum ExportTaskType
{
    Backup = 0,
    Export = 1
}

public enum TaskRunKind
{
    Import = 0,
    Export = 1
}

public enum TaskRunState
{
    Running = 0,
    Success = 1,
    Failed = 2
}
