namespace MatPaper.Data;

public class DocumentType : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public UpdateState UpdateState { get; set; }
}
