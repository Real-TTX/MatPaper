namespace MatPaper.Data;

public class ImportTask : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public ImportTaskType Type { get; set; }
    public bool IsEnabled { get; set; }
    public string? CronExpression { get; set; }
    public string SettingsJson { get; set; } = string.Empty;
    public UpdateState UpdateState { get; set; }
}
