namespace MatPaper.Data;

public class User : BaseEntity
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public long RoleId { get; set; }
    public bool IsActive { get; set; }
    public Role? Role { get; set; }
}
