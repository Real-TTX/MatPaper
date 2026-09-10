namespace MatPaper.Data;

/// <summary>
/// A document shared by its owner with a specific user. <see cref="CanEdit"/>
/// decides whether the recipient may only view/download the document or also
/// change its metadata. Public, anonymous links are a separate concept
/// (see <see cref="ShareLink"/>).
/// </summary>
public class DocumentShare : BaseEntity
{
    public long DocumentId { get; set; }
    public long UserId { get; set; }
    public bool CanEdit { get; set; }
    public UpdateState UpdateState { get; set; }
    public Document? Document { get; set; }
    public User? User { get; set; }
}
