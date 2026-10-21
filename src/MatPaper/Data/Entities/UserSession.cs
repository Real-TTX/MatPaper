namespace MatPaper.Data;

public class UserSession : BaseEntity
{
    public Guid Token { get; set; }
    public long UserId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>The ID token of MatIdentity for a session that began there; the sign-out ends that session at MatIdentity with it. Null for a local sign-in.</summary>
    public string? IdentityToken { get; set; }

    /// <summary>The session at MatIdentity (claim <c>sid</c>) this session began with (or was linked in); a back-channel logout that names it deletes this session.</summary>
    public string? IdentitySessionId { get; set; }
    public User? User { get; set; }
}
