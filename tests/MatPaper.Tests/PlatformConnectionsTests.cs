using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MatPaper.Services;
using MatPaper.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MatPaper.Tests;

/// <summary>
/// Contract 2 as MatPaper answers it (the whole app, PostgreSQL): the description with what it consumes and contributes, <c>/api/connections</c> behind the key,
/// and the Connections area (System → App connections) for administrators only.
/// </summary>
public sealed class PlatformConnectionsTests(PaperFactory site) : IClassFixture<PaperFactory>
{
    private const string Page = "/System/AppConnections";

    [DbFact]
    public async Task The_api_says_contract_2_and_what_MatPaper_consumes_and_contributes()
    {
        using var doc = JsonDocument.Parse(await site.CreateClient().GetStringAsync("/api"));
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("contract").GetInt32());
        Assert.Equal("identity.oidc", Assert.Single(root.GetProperty("consumes").EnumerateArray()).GetProperty("capability").GetString());

        var contributes = root.GetProperty("contributes");
        var launcher = Assert.Single(contributes.GetProperty("launchers").EnumerateArray());
        Assert.Equal("documents", launcher.GetProperty("id").GetString());
        Assert.Equal("/icons/logo.svg", launcher.GetProperty("icon").GetString());
        Assert.Equal(["archive", "inbox"], contributes.GetProperty("widgets").EnumerateArray().Select(w => w.GetProperty("id").GetString()!));
        Assert.All(contributes.GetProperty("widgets").EnumerateArray(), w => Assert.Equal("admin", w.GetProperty("audience").GetString()));
        Assert.Equal(3, contributes.GetProperty("quickActions").GetArrayLength());

        // The launcher's picture is served.
        Assert.Equal(HttpStatusCode.OK, (await site.CreateClient().GetAsync(launcher.GetProperty("icon").GetString())).StatusCode);
    }

    [DbFact]
    public async Task Connections_answer_only_with_the_key()
    {
        var anonymous = await site.CreateClient().GetAsync("/api/connections");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var answer = await site.Api().GetAsync("/api/connections");
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        using var doc = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
        var use = Assert.Single(doc.RootElement.GetProperty("uses").EnumerateArray());
        Assert.Equal("identity.oidc", use.GetProperty("capability").GetString());
        Assert.Equal("none", use.GetProperty("source").GetString());
        Assert.True(use.GetProperty("optional").GetBoolean());
        Assert.False(use.GetProperty("byHand").GetBoolean());
        Assert.Empty(doc.RootElement.GetProperty("offers").EnumerateArray());
        Assert.Equal("documents", doc.RootElement.GetProperty("contributes").GetProperty("launchers")[0].GetProperty("id").GetString());
    }

    [DbFact]
    public async Task The_Connections_area_is_for_administrators_only()
    {
        // An administrator sees the area, and the menu leads there.
        var admin = new Browser(site);
        Assert.Equal(HttpStatusCode.Redirect, (await admin.SignInAsync("/Account/Login", PaperFactory.AdminUser, PaperFactory.AdminPassword)).StatusCode);
        var page = await admin.GetAsync(Page);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("data-connections", html, StringComparison.Ordinal);
        Assert.Contains("href=\"" + Page + "\"", html, StringComparison.Ordinal);

        // A person without the role Admin gets neither the page nor its form.
        using (var scope = site.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SignInService>()
                .CreateUserAsync("user", "User", "user@example.com", "Start12345", ExternalSignInService.UserRole, isActive: true, actingUserId: null);
        }

        var user = new Browser(site);
        Assert.Equal(HttpStatusCode.Redirect, (await user.SignInAsync("/Account/Login", "user", "Start12345")).StatusCode);
        var refused = await user.GetAsync(Page);
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", Browser.PathOf(refused.Headers.Location!), StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"" + Page + "\"", await (await user.GetAsync("/")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var form = await user.PostAsync("/api/connections/form", new Dictionary<string, string> { ["do"] = "clear", ["capability"] = "identity.oidc", ["returnUrl"] = Page });
        Assert.Equal(HttpStatusCode.Forbidden, form.StatusCode);
        var anonymous = await new Browser(site).PostAsync("/api/connections/form", new Dictionary<string, string> { ["do"] = "clear", ["capability"] = "identity.oidc" });
        Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
    }
}

/// <summary>With the variables of MatIdentity the Connections area shows the sign-in as set by them, and never its secret.</summary>
public sealed class PlatformConnectionsWithIdentityTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    [DbFact]
    public async Task The_sign_in_is_set_by_the_variables_and_its_secret_is_not_sent()
    {
        var text = await site.Api().GetStringAsync("/api/connections");
        Assert.DoesNotContain("test-secret", text, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(text);
        var use = Assert.Single(doc.RootElement.GetProperty("uses").EnumerateArray());
        Assert.Equal(("identity.oidc", "variables"), (use.GetProperty("capability").GetString(), use.GetProperty("source").GetString()));
        var secret = use.GetProperty("values").EnumerateArray().Single(v => v.GetProperty("key").GetString() == "clientSecret");
        Assert.True(secret.GetProperty("value").GetProperty("isSet").GetBoolean());
    }
}

/// <summary><c>MAT_PUBLIC_URL</c> (a broker such as Mattiverse sets it) fixes the public address: in <c>/api</c>, in <c>/api/configure</c> and on System → Settings.</summary>
public sealed class PlatformPublicUrlTests(PublicUrlFactory site) : IClassFixture<PublicUrlFactory>
{
    [DbFact]
    public async Task The_public_address_is_fixed_and_in_force()
    {
        var api = site.Api();
        using (var definition = JsonDocument.Parse(await api.GetStringAsync("/api")))
        {
            var field = definition.RootElement.GetProperty("configure").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "publicUrl");
            Assert.True(field.GetProperty("fixed").GetBoolean());
        }

        using (var configuration = JsonDocument.Parse(await api.GetStringAsync("/api/configure")))
        {
            Assert.Equal(PublicUrlFactory.Address, configuration.RootElement.GetProperty("values").GetProperty("publicUrl").GetString());
        }

        var refused = await api.PutAsJsonAsync("/api/configure", new { values = new { publicUrl = "https://other.example.com" } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using (var errors = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()))
        {
            Assert.True(errors.RootElement.GetProperty("errors").TryGetProperty("publicUrl", out _));
        }

        // The settings page shows it read-only, and the OAuth redirect address is built from it.
        var admin = new Browser(site);
        Assert.Equal(HttpStatusCode.Redirect, (await admin.SignInAsync("/Account/Login", PaperFactory.AdminUser, PaperFactory.AdminPassword)).StatusCode);
        var html = await (await admin.GetAsync("/System/Settings")).Content.ReadAsStringAsync();
        Assert.Contains("id=\"locked-public-url\" type=\"text\" value=\"" + PublicUrlFactory.Address + "\" disabled", html, StringComparison.Ordinal);
        Assert.Contains("MAT_PUBLIC_URL", html, StringComparison.Ordinal);
        Assert.Contains(PublicUrlFactory.Address + "/System/Connections/OAuthCallback", html, StringComparison.Ordinal);

        // The variable is not written into the settings of the app: they come back when it goes.
        Assert.DoesNotContain(PublicUrlFactory.Address, await File.ReadAllTextAsync(Path.Combine(site.DataFolder, "config", "app.json")), StringComparison.Ordinal);
    }
}
