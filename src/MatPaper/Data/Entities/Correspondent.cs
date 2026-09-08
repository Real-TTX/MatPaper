namespace MatPaper.Data;

public class Correspondent : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? MatchPattern { get; set; }
    public string? Notes { get; set; }
    public UpdateState UpdateState { get; set; }
}
