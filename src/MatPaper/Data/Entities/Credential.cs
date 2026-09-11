namespace MatPaper.Data;

/// <summary>
/// A reusable, named set of access credentials (username + protected password,
/// optional domain) that import tasks can reference instead of storing the
/// password per task. Used for mailboxes (IMAP/POP3) and SMB/CIFS shares.
/// The password is stored protected via DataProtection.
/// </summary>
public class Credential : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public string ProtectedPassword { get; set; } = string.Empty;
    public UpdateState UpdateState { get; set; }
}
