namespace MatPaper.Data;

public class ShareLink : BaseEntity
{
    public Guid Token { get; set; }
    public long DocumentId { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public UpdateState UpdateState { get; set; }
    public Document? Document { get; set; }
}
