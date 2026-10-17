using System.Net;
using System.Security.Claims;
using System.Text.Json;
using MatPaper.Data;
using MatPaper.Tests.Support;
using MatPlatform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatPaper.Tests;

/// <summary>Without <c>MATIDENTITY_AUTHORITY</c> nothing changes: the local sign-in form, as before.</summary>
public sealed class LocalSignInTests(PaperFactory site) : IClassFixture<PaperFactory>
{
    [DbFact]
    public async Task The_sign_in_page_is_the_local_form_without_matidentity()
    {
        var response = await site.Browser().GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.DoesNotContain("MatIdentity", html);
    }

    [DbFact]
    public async Task A_page_that_needs_a_sign_in_sends_to_the_local_form_and_there_is_no_oidc()
    {
        var response = await site.Browser().GetAsync("/Inbox");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/Login", Browser.PathOf(response.Headers.Location!));

        var callback = await site.Browser().GetAsync("/signin-oidc");
        Assert.NotEqual(HttpStatusCode.OK, callback.StatusCode);
        Assert.True(callback.StatusCode == HttpStatusCode.NotFound || Browser.PathOf(callback.Headers.Location!).StartsWith("/Account/Login", StringComparison.Ordinal));
    }

    [DbFact]
    public async Task The_local_sign_in_and_sign_out_work_as_before()
    {
        var browser = new Browser(site);
        var signedIn = await browser.SignInAsync("/Account/Login", PaperFactory.AdminUser, PaperFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal("/", Browser.PathOf(signedIn.Headers.Location!));

        var home = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);

        var signedOut = await browser.PostAsync("/Account/Logout", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Browser.Antiforgery(await home.Content.ReadAsStringAsync()),
        });
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/Account/Login", Browser.PathOf(signedOut.Headers.Location!));

        var after = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.StartsWith("/Account/Login", Browser.PathOf(after.Headers.Location!));
    }

    [DbFact]
    public async Task A_wrong_password_is_refused()
    {
        var browser = new Browser(site);
        var response = await browser.SignInAsync("/Account/Login", PaperFactory.AdminUser, "wrong");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"login-form\"", await response.Content.ReadAsStringAsync());
    }
}

/// <summary>With <c>MATIDENTITY_AUTHORITY</c>: the automatic sign-in at MatIdentity, the local way in with <c>?local=1</c>, the sign-out there.</summary>
public sealed class IdentitySignInTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    [DbFact]
    public async Task The_sign_in_page_goes_straight_to_matidentity_with_code_and_pkce()
    {
        var response = await site.Browser().GetAsync("/Account/Login?returnUrl=%2FInbox");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!;
        Assert.StartsWith(IdentityFactory.Authority + "/connect/authorize", location.OriginalString);

        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("matpaper", query["client_id"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrEmpty(query["code_challenge"].ToString()));
        Assert.EndsWith("/signin-oidc", query["redirect_uri"].ToString());
    }

    [DbFact]
    public async Task A_page_that_needs_a_sign_in_ends_at_matidentity()
    {
        var browser = site.Browser();
        var first = await browser.GetAsync("/Inbox");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        var login = Browser.PathOf(first.Headers.Location!);
        Assert.StartsWith("/Account/Login", login);

        var second = await browser.GetAsync(login);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.StartsWith(IdentityFactory.Authority, second.Headers.Location!.OriginalString);
    }

    [DbFact]
    public async Task The_local_form_stays_reachable_and_a_local_sign_out_does_not_sign_in_again()
    {
        var browser = new Browser(site);
        var page = await browser.GetAsync("/Account/Login?local=1");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.Contains("id=\"lf-sso\"", html);

        var signedIn = await browser.SignInAsync("/Account/Login?local=1", PaperFactory.AdminUser, PaperFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal("/", Browser.PathOf(signedIn.Headers.Location!));

        // A local session has no ID token: the sign-out stays here.
        var home = await browser.GetAsync("/");
        var signedOut = await browser.PostAsync("/Account/Logout", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Browser.Antiforgery(await home.Content.ReadAsStringAsync()),
        });
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/Account/Login?signedOut=1", Browser.PathOf(signedOut.Headers.Location!));
    }

    [DbTheory]
    [InlineData("/Account/Login?signedOut=1")]
    [InlineData("/Account/Login?ssoError=1")]
    [InlineData("/Account/Login?local=true")]
    public async Task After_the_sign_out_or_a_failure_there_is_no_automatic_sign_in(string path)
    {
        var response = await site.Browser().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"lf-sso\"", await response.Content.ReadAsStringAsync());
    }

    [DbFact]
    public async Task A_new_person_from_matidentity_is_signed_in_and_the_sign_out_ends_the_session_there_too()
    {
        using var scope = site.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost");
        var person = new MatIdentityPerson("sub-new", "new.person@example.com", "New Person", "newperson", "user", "the-id-token", new ClaimsPrincipal());

        Assert.Equal("/Inbox", await MatIdentitySignIn.CompleteSignInAsync(http, person, "/Inbox"));

        var browser = new Browser(site);
        browser.Take(http.Response);
        var home = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);

        var user = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(u => u.ExternalSubject == "sub-new");
        Assert.Equal("newperson", user.Username);
        Assert.Equal("the-id-token", await scope.ServiceProvider.GetRequiredService<AppDbContext>().UserSessions.AsNoTracking()
            .Where(s => s.UserId == user.Id).Select(s => s.IdentityToken).SingleAsync());

        var response = await browser.PostAsync("/Account/Logout", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Browser.Antiforgery(await home.Content.ReadAsStringAsync()),
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(IdentityFactory.Authority + "/connect/logout", location.OriginalString);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("the-id-token", query["id_token_hint"].ToString());
        Assert.EndsWith("/signout-callback-oidc", query["post_logout_redirect_uri"].ToString());
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().UserSessions.AnyAsync(s => s.UserId == user.Id));
    }

    [DbFact]
    public async Task The_definition_names_matidentity_and_a_bad_token_is_401()
    {
        using var doc = JsonDocument.Parse(await site.CreateClient().GetStringAsync("/api"));
        Assert.Equal(IdentityFactory.Authority, doc.RootElement.GetProperty("auth").GetProperty("identity").GetString());

        var bearer = site.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.Api().GetAsync("/api/state")).StatusCode);
    }

    [DbFact]
    public async Task Shared_links_stay_anonymous()
    {
        // An unknown link: the page itself answers (404), nobody is sent to MatIdentity or the sign-in.
        var response = await site.Browser().GetAsync("/Share/" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>MatIdentity is configured but down: the sign-in page falls back to the local form instead of an error.</summary>
public sealed class UnreachableIdentityTests(UnreachableIdentityFactory site) : IClassFixture<UnreachableIdentityFactory>
{
    [DbFact]
    public async Task The_sign_in_page_shows_the_local_form_with_a_message()
    {
        var response = await site.Browser().GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.Contains("id=\"login-sso-error\"", html);
    }
}
