using System.Net;
using System.Security.Claims;
using MatPaper.Data;
using MatPaper.Tests.Support;
using MatPlatform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MatPaper.Tests;

/// <summary>
/// The back-channel logout (MatPlatform 1.4.0): MatIdentity posts a signed logout token to <c>POST /signout-backchannel</c>, MatPaper deletes the
/// sessions it names, and the next request of such a session is signed out.
/// </summary>
public sealed class BackchannelLogoutTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    private const string Path = "/signout-backchannel";

    [DbFact]
    public async Task A_valid_token_ends_the_session_it_names_and_its_next_request_is_signed_out()
    {
        var first = await SignedInBrowserAsync(Person("sub-bc-sid", "sid-bc-1"));
        var second = await SignedInBrowserAsync(Person("sub-bc-sid", "sid-bc-2"));
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/")).StatusCode);

        var response = await PostAsync(Token("sub-bc-sid", "sid-bc-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var signedOut = await first.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.StartsWith("/Account/Login", Browser.PathOf(signedOut.Headers.Location!));
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/")).StatusCode);
        Assert.False(await WithDbAsync(db => db.UserSessions.AnyAsync(s => s.IdentitySessionId == "sid-bc-1")));
        Assert.True(await WithDbAsync(db => db.UserSessions.AnyAsync(s => s.IdentitySessionId == "sid-bc-2")));
    }

    [DbFact]
    public async Task A_token_with_the_subject_alone_ends_every_session_of_the_linked_user()
    {
        var first = await SignedInBrowserAsync(Person("sub-bc-all", "sid-bc-all-1"));
        var second = await SignedInBrowserAsync(Person("sub-bc-all", "sid-bc-all-2"));
        var other = await SignedInBrowserAsync(Person("sub-bc-other", "sid-bc-other"));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token("sub-bc-all", null))).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await first.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await second.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/")).StatusCode);
    }

    [DbFact]
    public async Task An_unknown_subject_or_session_ends_nothing_and_is_no_error()
    {
        var browser = await SignedInBrowserAsync(Person("sub-bc-known", "sid-bc-known"));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token("sub-bc-unknown", null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token(null, "sid-bc-unknown"))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);
    }

    [DbFact]
    public async Task A_token_of_another_key_is_400_and_ends_nothing()
    {
        var browser = await SignedInBrowserAsync(Person("sub-bc-forged", "sid-bc-forged"));
        var other = new SigningCredentials(new RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048)) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(Token("sub-bc-forged", "sid-bc-forged", other))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);
    }

    private static MatIdentityPerson Person(string subject, string sessionId)
        => new(subject, subject + "@example.com", null, subject, "user", "id-token-" + sessionId, new ClaimsPrincipal()) { SessionId = sessionId };

    /// <summary>The person signs in through MatIdentity (the callback, no MatIdentity has to run); a browser with the session cookie.</summary>
    private async Task<Browser> SignedInBrowserAsync(MatIdentityPerson person)
    {
        using var scope = site.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost");
        await MatIdentitySignIn.CompleteSignInAsync(http, person, "/");

        var browser = new Browser(site);
        browser.Take(http.Response);
        return browser;
    }

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = site.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<HttpResponseMessage> PostAsync(string token)
        => site.CreateClient().PostAsync(Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }));

    /// <summary>A logout token as MatIdentity writes it (signed with the key of the fixed configuration).</summary>
    internal static string Token(string? subject, string? sessionId, SigningCredentials? credentials = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = IdentityFactory.Authority,
            ["aud"] = "matpaper",
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + 120,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["events"] = new Dictionary<string, object> { [MatIdentityLogoutToken.EventType] = new Dictionary<string, object>() },
        };
        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        if (sessionId is not null)
        {
            claims["sid"] = sessionId;
        }

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            TokenType = MatIdentityLogoutToken.TokenType,
            SigningCredentials = credentials ?? PaperFactory.IdentityCredentials,
        });
    }
}

/// <summary>Without <c>MATIDENTITY_AUTHORITY</c> there is no back-channel logout.</summary>
public sealed class BackchannelLogoutWithoutIdentityTests(PaperFactory site) : IClassFixture<PaperFactory>
{
    [DbFact]
    public async Task Without_matidentity_there_is_no_endpoint()
    {
        var endpoints = site.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        Assert.DoesNotContain(endpoints, e => e.RoutePattern.RawText?.TrimStart('/') == "signout-backchannel");

        // Like any unknown address: without a session the sign-in page (the fallback policy wants one), with a session 404 - nothing reads the token.
        var form = new Dictionary<string, string> { ["logout_token"] = BackchannelLogoutTests.Token("sub-bc", "sid-bc") };
        var browser = new Browser(site);
        var anonymous = await browser.PostAsync("/signout-backchannel", form);
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.StartsWith("/Account/Login", Browser.PathOf(anonymous.Headers.Location!));

        Assert.Equal(HttpStatusCode.Redirect, (await browser.SignInAsync("/Account/Login", PaperFactory.AdminUser, PaperFactory.AdminPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync("/signout-backchannel", form)).StatusCode);
    }
}
