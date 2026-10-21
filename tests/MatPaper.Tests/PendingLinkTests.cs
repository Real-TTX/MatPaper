using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using MatPaper.Data;
using MatPaper.Services;
using MatPaper.Tests.Support;
using MatPlatform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatPaper.Tests;

/// <summary>
/// The e-mail address of a local account proves nothing (anyone may type any address into an account), so an unlinked account with the
/// address of a MatIdentity person is linked only after its own password (MatPlatform <see cref="MatIdentityPendingLink"/>).
/// </summary>
public sealed class PendingLinkTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    private const string LinkPage = "/Account/Login?local=1&link=1";
    private const string SessionCookie = "MatPaper.Session";

    [DbFact]
    public async Task A_pre_claimed_address_is_not_entered_and_the_person_lands_on_the_local_sign_in()
    {
        var (claimer, email) = await CreateUserAsync("claimer-password");

        var (target, http) = await MatIdentitySignInAsync(Person(email));

        Assert.Equal(LinkPage, target);
        // No session was started, the account is not linked and no second account was made.
        Assert.DoesNotContain(http.Response.Headers.SetCookie, c => c!.StartsWith(SessionCookie + "=", StringComparison.Ordinal));
        Assert.Null((await UserAsync(claimer.Id)).ExternalSubject);
        Assert.False(await WithDbAsync(db => db.Users.AnyAsync(u => u.ExternalSubject == "sub-" + email)));
        Assert.False(await WithDbAsync(db => db.UserSessions.AnyAsync(s => s.UserId == claimer.Id)));

        var browser = new Browser(site);
        browser.Take(http.Response);
        var page = await browser.GetAsync(LinkPage);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-link\"", html);
        Assert.Contains(email, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=\"login-link-cancel\"", html);
        Assert.Contains("id=\"login-form\"", html);
    }

    [DbFact]
    public async Task The_local_sign_in_of_the_matched_account_links_it_with_the_role_and_the_id_token()
    {
        var (owner, email) = await CreateUserAsync("owner-password");
        var browser = await PendingBrowserAsync(email, role: "admin");

        var signedIn = await browser.SignInAsync(LinkPage, owner.Username, "owner-password");

        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal("/", Browser.PathOf(signedIn.Headers.Location!));
        var user = await UserAsync(owner.Id);
        Assert.Equal("sub-" + email, user.ExternalSubject);
        Assert.Equal("Admin", await RoleAsync(user));
        Assert.False(browser.Has(MatIdentityPendingLink.CookieName));
        Assert.True(browser.Has(SessionCookie));
        Assert.Equal("id-token-" + email, await WithDbAsync(db => db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == owner.Id).Select(s => s.IdentityToken).SingleAsync()));
        Assert.Equal("sid-" + email, await WithDbAsync(db => db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == owner.Id).Select(s => s.IdentitySessionId).SingleAsync()));

        // From now on MatIdentity finds the account by sub.
        var (target, _) = await MatIdentitySignInAsync(Person(email));
        Assert.Equal("/", target);
    }

    [DbFact]
    public async Task Another_account_signing_in_links_nothing_and_ends_the_pending_link()
    {
        var (matched, email) = await CreateUserAsync("matched-password");
        var (other, _) = await CreateUserAsync("other-password");
        var browser = await PendingBrowserAsync(email);

        var signedIn = await browser.SignInAsync(LinkPage, other.Username, "other-password");

        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Null((await UserAsync(matched.Id)).ExternalSubject);
        Assert.Null((await UserAsync(other.Id)).ExternalSubject);
        Assert.False(browser.Has(MatIdentityPendingLink.CookieName));
        Assert.Null(await WithDbAsync(db => db.UserSessions.AsNoTracking().Where(s => s.UserId == other.Id).Select(s => s.IdentityToken).SingleAsync()));
    }

    [DbFact]
    public async Task A_wrong_password_keeps_the_link_pending()
    {
        var (matched, email) = await CreateUserAsync("matched-password");
        var browser = await PendingBrowserAsync(email);

        var wrong = await browser.SignInAsync(LinkPage, matched.Username, "guessed");

        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.True(browser.Has(MatIdentityPendingLink.CookieName));
        Assert.Null((await UserAsync(matched.Id)).ExternalSubject);
    }

    [DbFact]
    public async Task Cancel_deletes_the_pending_link()
    {
        var (matched, email) = await CreateUserAsync("matched-password");
        var browser = await PendingBrowserAsync(email);

        var html = await (await browser.GetAsync(LinkPage)).Content.ReadAsStringAsync();
        var cancel = await browser.PostAsync("/Account/Login?handler=CancelLink", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Browser.Antiforgery(html),
        });

        Assert.Equal(HttpStatusCode.Redirect, cancel.StatusCode);
        Assert.Equal("/Account/Login?local=1", Browser.PathOf(cancel.Headers.Location!));
        Assert.False(browser.Has(MatIdentityPendingLink.CookieName));

        // The password afterwards is a plain local sign-in.
        await browser.SignInAsync("/Account/Login?local=1", matched.Username, "matched-password");
        Assert.Null((await UserAsync(matched.Id)).ExternalSubject);
    }

    [DbFact]
    public async Task An_expired_or_forged_pending_cookie_is_ignored()
    {
        var (matched, email) = await CreateUserAsync("matched-password");
        var json = JsonSerializer.Serialize(
            new MatIdentityPendingLink("sub-" + email, email, "X", null, "admin", "t", matched.Id.ToString(CultureInfo.InvariantCulture)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var expired = site.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(MatIdentityPendingLink.Purpose)
            .ToTimeLimitedDataProtector().Protect(json, DateTimeOffset.UtcNow.AddSeconds(-1));

        foreach (var value in new[] { expired, Uri.EscapeDataString(json), "CfDJ8forged" })
        {
            var browser = new Browser(site);
            browser.Set(MatIdentityPendingLink.CookieName, value);

            var html = await (await browser.GetAsync(LinkPage)).Content.ReadAsStringAsync();
            Assert.Contains("id=\"login-link-expired\"", html);
            Assert.DoesNotContain("id=\"login-link\"", html);

            await browser.SignInAsync(LinkPage, matched.Username, "matched-password");
            var user = await UserAsync(matched.Id);
            Assert.Null(user.ExternalSubject);
            Assert.Equal("User", await RoleAsync(user));
        }
    }

    [DbFact]
    public async Task A_deactivated_match_is_refused()
    {
        var (inactive, email) = await CreateUserAsync("x-password");
        await WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == inactive.Id)).IsActive = false;
            return await db.SaveChangesAsync();
        });

        Assert.Equal("/Account/AccessDenied", (await MatIdentitySignInAsync(Person(email))).Target);
        Assert.False(await WithDbAsync(db => db.Users.AnyAsync(u => u.ExternalSubject == "sub-" + email)));
    }

    private async Task<Browser> PendingBrowserAsync(string email, string? role = null)
    {
        var (target, http) = await MatIdentitySignInAsync(Person(email, role));
        Assert.Equal(LinkPage, target);
        var browser = new Browser(site);
        browser.Take(http.Response);
        Assert.True(browser.Has(MatIdentityPendingLink.CookieName));
        return browser;
    }

    /// <summary>What the callback of MatIdentity does for <paramref name="person"/> (no MatIdentity has to run).</summary>
    private async Task<(string Target, DefaultHttpContext Http)> MatIdentitySignInAsync(MatIdentityPerson person)
    {
        using var scope = site.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost");
        return (await MatIdentitySignIn.CompleteSignInAsync(http, person), http);
    }

    private static MatIdentityPerson Person(string email, string? role = null)
        => new("sub-" + email, email.ToUpperInvariant(), "Someone", "someone", role, "id-token-" + email, new ClaimsPrincipal()) { SessionId = "sid-" + email };

    /// <summary>A local account with a password and an address nobody else has.</summary>
    private async Task<(User User, string Email)> CreateUserAsync(string password)
    {
        var name = "u" + Guid.NewGuid().ToString("N")[..10];
        var email = name + "@example.com";
        var user = await WithScopeAsync(sp => sp.GetRequiredService<SignInService>().CreateUserAsync(name, name, email, password, "User", isActive: true, actingUserId: null));
        return (user, email);
    }

    private Task<User> UserAsync(long id) => WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<string> RoleAsync(User user) => WithDbAsync(db => db.Roles.Where(r => r.Id == user.RoleId).Select(r => r.Name).SingleAsync());

    private Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> work) => WithScopeAsync(sp => work(sp.GetRequiredService<AppDbContext>()));

    private async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = site.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }
}
