namespace MatPaper.Data;

public abstract class BaseEntity
{
    public long Id { get; set; }
    public DateTime CreateDate { get; set; }
    public long? CreateUserId { get; set; }
    public DateTime UpdateDate { get; set; }
    public long? UpdateUserId { get; set; }
}
