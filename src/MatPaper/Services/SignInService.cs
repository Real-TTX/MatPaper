using System.Security.Claims;
using MatPaper.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

public class SignInService(AppDbContext db, IHttpContextAccessor http, PasswordHasher<User> hasher)
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    public async Task<bool> AnyUsersExistAsync()
    {
        return await db.Users.AnyAsync();
    }

    public async Task<User> CreateUserAsync(
        string username,
        string displayName,
        string? email,
        string password,
        string roleName,
        bool isActive,
        long? actingUserId)
    {
        var normalizedUsername = (username ?? string.Empty).Trim();

        var exists = await db.Users
            .AnyAsync(u => u.Username.ToLower() == normalizedUsername.ToLower());
        if (exists)
        {
            throw new InvalidOperationException($"A user with the username '{normalizedUsername}' already exists.");
        }

        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName)
            ?? throw new InvalidOperationException($"Role '{roleName}' does not exist.");

        var now = DateTime.UtcNow;

        var user = new User
        {
            Username = normalizedUsername,
            DisplayName = (displayName ?? string.Empty).Trim(),
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            RoleId = role.Id,
            IsActive = isActive,
            CreateDate = now,
            CreateUserId = actingUserId,
            UpdateDate = now,
            UpdateUserId = actingUserId
        };

        user.PasswordHash = hasher.HashPassword(user, password);

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return user;
    }

    public async Task<User?> ValidateCredentialsAsync(string username, string password)
    {
        var normalizedUsername = (username ?? string.Empty).Trim();

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.IsActive && u.Username.ToLower() == normalizedUsername.ToLower());
        if (user is null)
        {
            return null;
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, password);
            user.UpdateDate = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        return user;
    }

    /// <summary>
    /// Starts a session for <paramref name="user"/> and sets the cookie. <paramref name="identityToken"/> is the ID token of MatIdentity when the
    /// session began there (kept with the session for the sign-out at MatIdentity). <paramref name="httpContext"/> is the request to sign in on
    /// (default: the current one; the callback of MatIdentity hands its own).
    /// </summary>
    public async Task SignInAsync(User user, bool isPersistent, string? identityToken = null, HttpContext? httpContext = null)
    {
        var context = httpContext ?? http.HttpContext
            ?? throw new InvalidOperationException("No active HttpContext.");

        var now = DateTime.UtcNow;
        var expiresAt = now.Add(SessionLifetime);

        var userAgent = context.Request.Headers.UserAgent.ToString();
        if (userAgent.Length > 512)
        {
            userAgent = userAgent[..512];
        }

        var token = Guid.NewGuid();

        var session = new UserSession
        {
            Token = token,
            UserId = user.Id,
            ExpiresAt = expiresAt,
            LastSeenAt = now,
            UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent,
            IdentityToken = string.IsNullOrEmpty(identityToken) ? null : identityToken,
            CreateDate = now,
            CreateUserId = user.Id,
            UpdateDate = now,
            UpdateUserId = user.Id
        };

        db.UserSessions.Add(session);
        await db.SaveChangesAsync();

        var roleName = user.Role?.Name
            ?? await db.Roles.Where(r => r.Id == user.RoleId).Select(r => r.Name).FirstOrDefaultAsync()
            ?? string.Empty;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, roleName),
            new(AppClaims.DisplayName, user.DisplayName),
            new(AppClaims.SessionToken, token.ToString())
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var properties = new AuthenticationProperties
        {
            IsPersistent = isPersistent,
            ExpiresUtc = expiresAt
        };

        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
    }

    /// <summary>The ID token of MatIdentity kept with the current session, or null (a local sign-in, or nobody signed in).</summary>
    public async Task<string?> GetIdentityTokenAsync(CancellationToken ct = default)
    {
        var tokenValue = http.HttpContext?.User.FindFirstValue(AppClaims.SessionToken);
        if (!Guid.TryParse(tokenValue, out var token))
        {
            return null;
        }

        return await db.UserSessions.AsNoTracking()
            .Where(s => s.Token == token)
            .Select(s => s.IdentityToken)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SignOutAsync()
    {
        var context = http.HttpContext
            ?? throw new InvalidOperationException("No active HttpContext.");

        var tokenValue = context.User.FindFirstValue(AppClaims.SessionToken);
        if (Guid.TryParse(tokenValue, out var token))
        {
            var sessions = await db.UserSessions.Where(s => s.Token == token).ToListAsync();
            if (sessions.Count > 0)
            {
                db.UserSessions.RemoveRange(sessions);
                await db.SaveChangesAsync();
            }
        }

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
