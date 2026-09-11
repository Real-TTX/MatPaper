namespace MatPaper.Data;

public class Correspondent : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? MatchPattern { get; set; }

    /// <summary>Contact details. Email and phone are also used for auto-matching against document text.</summary>
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }

    public string? Notes { get; set; }
    public UpdateState UpdateState { get; set; }
}
