namespace MatPaper.Data;

public class User : BaseEntity
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public long RoleId { get; set; }
    public bool IsActive { get; set; }

    /// <summary>
    /// The person at MatIdentity (the claim <c>sub</c>) this account belongs to; null for a local-only account.
    /// Set on the first sign-in through MatIdentity, or after the account proved itself with its password (see <c>ExternalSignInService</c>).
    /// </summary>
    public string? ExternalSubject { get; set; }

    /// <summary>Own theme mode (system/light/dark); null = the instance default.</summary>
    public string? ThemeMode { get; set; }

    /// <summary>Own colour scheme (see ThemeCatalog); null = the instance default.</summary>
    public string? ThemeScheme { get; set; }

    /// <summary>Own accent colour (see ThemeCatalog); null = the instance default.</summary>
    public string? ThemeAccent { get; set; }
    public Role? Role { get; set; }
}
