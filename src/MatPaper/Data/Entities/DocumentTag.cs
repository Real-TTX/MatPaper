namespace MatPaper.Data;

public class DocumentTag : BaseEntity
{
    public long DocumentId { get; set; }
    public long TagId { get; set; }
    public Document? Document { get; set; }
    public Tag? Tag { get; set; }
}
