namespace MatPaper.Data;

public class UserSession : BaseEntity
{
    public Guid Token { get; set; }
    public long UserId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public string? UserAgent { get; set; }
    public User? User { get; set; }
}
