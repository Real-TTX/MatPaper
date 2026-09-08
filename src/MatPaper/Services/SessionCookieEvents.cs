using System.Security.Claims;
using MatPaper.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatPaper.Services;

public class SessionCookieEvents : CookieAuthenticationEvents
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);
    private static readonly TimeSpan SlidingThreshold = TimeSpan.FromMinutes(5);

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext ctx)
    {
        var tokenValue = ctx.Principal?.FindFirstValue(AppClaims.SessionToken);
        if (!Guid.TryParse(tokenValue, out var token))
        {
            await RejectAsync(ctx);
            return;
        }

        var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();

        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.Token == token);
        var now = DateTime.UtcNow;

        if (session is null || session.ExpiresAt < now)
        {
            await RejectAsync(ctx);
            return;
        }

        if (session.LastSeenAt < now - SlidingThreshold)
        {
            session.LastSeenAt = now;
            session.ExpiresAt = now.Add(SessionLifetime);
            session.UpdateDate = now;
            await db.SaveChangesAsync();
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
