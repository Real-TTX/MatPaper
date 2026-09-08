namespace MatPaper.Data;

public class StorageLocation : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string PathTemplate { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public UpdateState UpdateState { get; set; }
}
