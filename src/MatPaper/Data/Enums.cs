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
    Failed = 2
}

/// <summary>
/// Whether a document still needs a human to review the auto-suggested
/// metadata (type, correspondent, date, tags) in the inbox, or has been
/// confirmed ("gelesen"/erledigt).
/// </summary>
public enum ReviewState
{
    Pending = 0,
    Reviewed = 1
}

public enum ImportTaskType
{
    Imap = 0,
    Pop3 = 1,
    Filesystem = 2
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
