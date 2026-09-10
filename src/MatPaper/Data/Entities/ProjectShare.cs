namespace MatPaper.Data;

/// <summary>
/// A project shared by its owner with a specific user. <see cref="CanEdit"/>
/// decides whether the recipient may only view the project and its documents or
/// also edit those documents' metadata. Access to a project cascades to every
/// document in it.
/// </summary>
public class ProjectShare : BaseEntity
{
    public long ProjectId { get; set; }
    public long UserId { get; set; }
    public bool CanEdit { get; set; }
    public UpdateState UpdateState { get; set; }
    public Project? Project { get; set; }
    public User? User { get; set; }
}
