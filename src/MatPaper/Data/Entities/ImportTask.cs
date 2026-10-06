namespace MatPaper.Data;

public class ImportTask : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public ImportTaskType Type { get; set; }
    public bool IsEnabled { get; set; }
    public string? CronExpression { get; set; }
    public string SettingsJson { get; set; } = string.Empty;

    /// <summary>What the task remembers between runs (see ImportSyncState); null = start from the beginning.</summary>
    public string? SyncState { get; set; }
    public UpdateState UpdateState { get; set; }
}
