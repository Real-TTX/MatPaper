namespace MatPaper.Data;

public class Project : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
    public UpdateState UpdateState { get; set; }
}
