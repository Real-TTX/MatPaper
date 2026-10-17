using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>A person as MatIdentity names it: the subject (<c>sub</c>), the address, the names and the role claim of this app (or null).</summary>
public sealed record ExternalIdentity(string Subject, string? Email, string? Name, string? PreferredUserName, string? Role);

/// <summary>
/// The outcome of <see cref="ExternalSignInService.SignInExternalAsync"/>: the local user to sign in, or the id of the account that matches by
/// its e-mail address and has to prove itself with its password before it is linked (<see cref="LinkUserId"/>), or the reason for a refusal.
/// </summary>
public sealed record ExternalSignInResult(User? User, long? LinkUserId, string? Error)
{
    public static ExternalSignInResult SignedIn(User user) => new(user, null, null);

    public static ExternalSignInResult LinkRequired(long userId) => new(null, userId, null);

    public static ExternalSignInResult Refused(string error) => new(null, null, error);
}

/// <summary>
/// How a person signed in by MatIdentity becomes a local user of MatPaper (MatDesign docs/PLATFORM.md, "Sign-in with MatIdentity"):
/// found by <see cref="User.ExternalSubject"/>; else, when exactly one active account that is not linked yet has the same e-mail address
/// and a password, that account is <b>not</b> entered: it is linked only after its own local sign-in (<see cref="LinkExternalAsync"/>),
/// because the address of a local account proves nothing; else a new user is created. The claim <c>role</c> (<c>admin</c> or <c>user</c>)
/// sets the role at every sign-in, except that the last active administrator is never demoted.
/// </summary>
public sealed class ExternalSignInService(AppDbContext db, ILogger<ExternalSignInService> logger)
{
    public const string AdminRole = "Admin";
    public const string UserRole = "User";

    private const int UsernameMaxLength = 100;

    public async Task<ExternalSignInResult> SignInExternalAsync(ExternalIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            return ExternalSignInResult.Refused("MatIdentity did not send a subject (sub).");
        }

        var subject = identity.Subject.Trim();
        var email = NormalizeEmail(identity.Email);

        var user = await db.Users.FirstOrDefaultAsync(u => u.ExternalSubject == subject, ct);
        if (user is not null)
        {
            if (!user.IsActive)
            {
                return ExternalSignInResult.Refused("The account is deactivated in MatPaper.");
            }

            await ApplyRoleAsync(user, identity.Role, created: false, ct);
            if (user.Email is null && email is not null && !await EmailTakenAsync(email, user.Id, ct))
            {
                user.Email = email;
            }

            if (db.Entry(user).State == EntityState.Modified)
            {
                user.UpdateDate = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            return ExternalSignInResult.SignedIn(user);
        }

        if (email is not null)
        {
            var matches = await db.Users.AsNoTracking()
                .Where(u => u.Email != null && u.Email.ToLower() == email)
                .ToListAsync(ct);

            if (matches.Count > 1)
            {
                logger.LogWarning("MatIdentity subject {Subject} matches {Count} accounts by e-mail; refused", subject, matches.Count);
                return ExternalSignInResult.Refused("Several accounts in MatPaper have this e-mail address.");
            }

            if (matches.Count == 1)
            {
                var match = matches[0];
                if (match.ExternalSubject is not null)
                {
                    logger.LogWarning("MatIdentity subject {Subject} matches user {UserId}, which is linked with another subject; refused", subject, match.Id);
                    return ExternalSignInResult.Refused("The account with this e-mail address is linked with another person of MatIdentity.");
                }

                if (!match.IsActive)
                {
                    return ExternalSignInResult.Refused("The account is deactivated in MatPaper.");
                }

                if (!string.IsNullOrEmpty(match.PasswordHash))
                {
                    // Not linked by the address alone: the person proves the account with its password first.
                    logger.LogInformation("MatIdentity subject {Subject} matches user {UserId} by e-mail; the link waits for its password", subject, match.Id);
                    return ExternalSignInResult.LinkRequired(match.Id);
                }

                // An account without a password cannot prove itself: a new account is made beside it (without the address).
            }
        }

        var created = await CreateAsync(subject, email, identity, ct);
        return ExternalSignInResult.SignedIn(created);
    }

    /// <summary>
    /// Links the user <paramref name="userId"/> with the person of <paramref name="identity"/> after the user proved the account with its
    /// password (the pending link of <see cref="SignInExternalAsync"/>): sets the subject and follows the role claim like a sign-in through
    /// MatIdentity. Refused when the account is gone, deactivated or linked meanwhile, or when another user has the subject by now.
    /// </summary>
    public async Task<ExternalSignInResult> LinkExternalAsync(long userId, ExternalIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            return ExternalSignInResult.Refused("MatIdentity did not send a subject (sub).");
        }

        var subject = identity.Subject.Trim();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
        {
            return ExternalSignInResult.Refused("The account is deactivated in MatPaper.");
        }

        if (user.ExternalSubject is not null || await db.Users.AnyAsync(u => u.ExternalSubject == subject, ct))
        {
            logger.LogWarning("User {UserId} was not linked with MatIdentity subject {Subject}: one of them is linked already", userId, subject);
            return ExternalSignInResult.Refused("The account is linked with MatIdentity already.");
        }

        user.ExternalSubject = subject;
        await ApplyRoleAsync(user, identity.Role, created: false, ct);
        user.UpdateDate = DateTime.UtcNow;
        user.UpdateUserId = user.Id;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("User {UserId} ({Username}) linked with MatIdentity subject {Subject} after its password", user.Id, user.Username, subject);
        return ExternalSignInResult.SignedIn(user);
    }

    private async Task<User> CreateAsync(string subject, string? email, ExternalIdentity identity, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var username = await FreeUsernameAsync(FirstNonEmpty(identity.PreferredUserName, email?.Split('@')[0], identity.Name) ?? "matidentity", ct);
        var displayName = FirstNonEmpty(identity.Name, identity.PreferredUserName, email) ?? username;

        var user = new User
        {
            Username = username,
            DisplayName = displayName.Length > 200 ? displayName[..200] : displayName,
            Email = email is not null && !await EmailTakenAsync(email, null, ct) ? email : null,
            // No local password anybody knows: this person signs in through MatIdentity.
            PasswordHash = string.Empty,
            IsActive = true,
            ExternalSubject = subject,
            CreateDate = now,
            UpdateDate = now,
        };

        await ApplyRoleAsync(user, identity.Role, created: true, ct);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("User {UserId} ({Username}) created at the first sign-in through MatIdentity (subject {Subject})", user.Id, user.Username, subject);
        return user;
    }

    /// <summary>
    /// The claim <c>role</c>: <c>admin</c> or <c>user</c> sets the role (never demoting the last active administrator); anything else, or no
    /// claim, leaves it (a new user is a user then).
    /// </summary>
    private async Task ApplyRoleAsync(User user, string? claim, bool created, CancellationToken ct)
    {
        var wanted = claim?.Trim().ToLowerInvariant() switch
        {
            "admin" => AdminRole,
            "user" => UserRole,
            _ => created ? UserRole : null,
        };

        if (wanted is null)
        {
            return;
        }

        var roleId = await db.Roles.Where(r => r.Name == wanted).Select(r => r.Id).FirstAsync(ct);
        if (user.RoleId == roleId)
        {
            return;
        }

        if (!created && wanted != AdminRole && await IsLastActiveAdminAsync(user, ct))
        {
            logger.LogWarning("MatIdentity asks for role {Role} for user {UserId}, who is the last active administrator; the role stays", wanted, user.Id);
            return;
        }

        user.RoleId = roleId;
    }

    private async Task<bool> IsLastActiveAdminAsync(User user, CancellationToken ct)
    {
        var adminId = await db.Roles.Where(r => r.Name == AdminRole).Select(r => r.Id).FirstAsync(ct);
        return user.RoleId == adminId
            && user.IsActive
            && await db.Users.CountAsync(u => u.RoleId == adminId && u.IsActive, ct) <= 1;
    }

    private async Task<string> FreeUsernameAsync(string wanted, CancellationToken ct)
    {
        var name = wanted.Trim();
        if (name.Length > UsernameMaxLength)
        {
            name = name[..UsernameMaxLength];
        }

        var candidate = name;
        for (var n = 2; await db.Users.AnyAsync(u => u.Username.ToLower() == candidate.ToLower(), ct); n++)
        {
            candidate = name + "-" + n;
        }

        return candidate;
    }

    private Task<bool> EmailTakenAsync(string email, long? exceptUserId, CancellationToken ct)
        => db.Users.AnyAsync(u => u.Email != null && u.Email.ToLower() == email && u.Id != exceptUserId, ct);

    private static string? NormalizeEmail(string? email)
        => string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
