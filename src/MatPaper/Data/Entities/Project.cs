namespace MatPaper.Data;

public class Project : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
    public UpdateState UpdateState { get; set; }

    /// <summary>The user who owns this project. Null = ownerless (visible to admins only).</summary>
    public long? OwnerId { get; set; }

    /// <summary>When true, the project (and its documents) is visible to every user.</summary>
    public bool IsCommon { get; set; }

    public User? Owner { get; set; }
    public ICollection<ProjectShare> Shares { get; set; } = new List<ProjectShare>();
}
