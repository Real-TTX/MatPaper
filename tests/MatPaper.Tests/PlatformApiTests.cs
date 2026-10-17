using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MatPaper.Data;
using MatPaper.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatPaper.Tests;

/// <summary>The platform contract of MatDesign docs/PLATFORM.md as MatPaper answers it: shapes, status codes, access (the whole app, PostgreSQL).</summary>
public sealed class PlatformApiTests(PaperFactory site) : IClassFixture<PaperFactory>
{
    private static readonly string[] Protected = ["/api/state", "/api/state/documents", "/api/configure"];

    [DbFact]
    public async Task Health_answers_without_a_key()
    {
        var response = await site.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("matpaper", doc.RootElement.GetProperty("app").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("version").GetString()));
        Assert.Contains(doc.RootElement.GetProperty("checks").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "database" && c.GetProperty("status").GetString() == "ok");
    }

    [DbFact]
    public async Task The_served_definition_is_valid_has_an_etag_and_no_secret()
    {
        var client = site.CreateClient();
        var response = await client.GetAsync("/api");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Empty(MatPlatform.MatPlatformValidator.Validate(text, MatPlatform.MatPlatformValidator.ReadSprite(Repo.Sprite)));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api");
        request.Headers.TryAddWithoutValidation("If-None-Match", response.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(request)).StatusCode);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("contract").GetInt32());
        Assert.Equal("matpaper", root.GetProperty("app").GetProperty("id").GetString());
        Assert.Equal("#268538", root.GetProperty("app").GetProperty("accent").GetString());
        Assert.True(root.GetProperty("auth").GetProperty("apiKey").GetBoolean());
        Assert.False(root.GetProperty("auth").TryGetProperty("identity", out _));

        // Never the key, a password of the database or a path of the host.
        Assert.DoesNotContain(PaperFactory.ApiKey, text);
        Assert.DoesNotContain(site.DatabaseName, text);
        Assert.DoesNotContain(site.DataFolder.Replace("\\", "\\\\", StringComparison.Ordinal), text);
    }

    [DbFact]
    public async Task The_definition_follows_accept_language()
    {
        var client = site.CreateClient();
        using var german = new HttpRequestMessage(HttpMethod.Get, "/api");
        german.Headers.TryAddWithoutValidation("Accept-Language", "de-DE");
        using var english = new HttpRequestMessage(HttpMethod.Get, "/api");
        english.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        using var de = JsonDocument.Parse(await (await client.SendAsync(german)).Content.ReadAsStringAsync());
        using var en = JsonDocument.Parse(await (await client.SendAsync(english)).Content.ReadAsStringAsync());
        Assert.Equal("Übersicht", de.RootElement.GetProperty("pages")[0].GetProperty("label").GetString());
        Assert.Equal("Overview", en.RootElement.GetProperty("pages")[0].GetProperty("label").GetString());
    }

    [DbFact]
    public async Task State_and_configure_need_the_key()
    {
        var anonymous = site.CreateClient();
        foreach (var path in Protected)
        {
            var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/configure", new { values = new { archiveNumbers = true } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/actions/open", null)).StatusCode);

        var wrong = site.CreateClient();
        wrong.DefaultRequestHeaders.Add("X-Api-Key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/state")).StatusCode);

        // A bearer token is no way in while MatIdentity is not configured.
        var bearer = site.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "abc.def.ghi");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/state")).StatusCode);
    }

    [DbFact]
    public async Task State_answers_every_tile_with_the_real_numbers_and_one_value_by_key()
    {
        await AddSampleDocumentsAsync();

        var api = site.Api();
        using var definition = JsonDocument.Parse(await api.GetStringAsync("/api"));
        using var state = JsonDocument.Parse(await api.GetStringAsync("/api/state"));
        var values = state.RootElement.GetProperty("values");
        Assert.True(state.RootElement.TryGetProperty("time", out _));

        foreach (var key in definition.RootElement.GetProperty("state").EnumerateArray().Select(s => s.GetProperty("key").GetString()!))
        {
            Assert.True(values.TryGetProperty(key, out var value), $"No value for {key}");
            Assert.True(value.TryGetProperty("value", out _), $"{key} has no value field");
        }

        long Number(string key) => values.GetProperty(key).GetProperty("value").GetInt64();

        // Four documents: one filed, one in the inbox waiting for text recognition, one whose recognition failed, one deleted; one ignored.
        Assert.Equal(3, Number("documents"));
        Assert.Equal(2, Number("inbox"));
        Assert.Equal(1, Number("ocrQueue"));
        Assert.Equal(1, Number("ocrFailed"));
        Assert.Equal("warn", values.GetProperty("ocrFailed").GetProperty("level").GetString());
        Assert.Equal(1000 + 2000 + 4000, Number("storage"));
        Assert.Equal(1, Number("users"));
        Assert.Equal(JsonValueKind.String, values.GetProperty("lastDocument").GetProperty("value").ValueKind);

        var one = await api.GetAsync("/api/state/documents");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        using (var doc = JsonDocument.Parse(await one.Content.ReadAsStringAsync()))
        {
            Assert.Equal("documents", doc.RootElement.GetProperty("key").GetString());
            Assert.Equal(3, doc.RootElement.GetProperty("value").GetInt32());
        }

        var unknown = await api.GetAsync("/api/state/nothing");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using (var doc = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty("nothing", out _));
        }
    }

    [DbFact]
    public async Task Configure_answers_every_field_and_saves_a_change_where_the_settings_page_reads_it()
    {
        var api = site.Api();
        using var definition = JsonDocument.Parse(await api.GetStringAsync("/api"));
        var text = await api.GetStringAsync("/api/configure");
        using (var configuration = JsonDocument.Parse(text))
        {
            var values = configuration.RootElement.GetProperty("values");
            foreach (var field in definition.RootElement.GetProperty("configure").EnumerateArray())
            {
                Assert.True(values.TryGetProperty(field.GetProperty("key").GetString()!, out _));
            }
        }

        // No secret of the installation is ever sent: the database password, the key.
        Assert.DoesNotContain(PaperFactory.ApiKey, text);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);

        var saved = await api.PutAsJsonAsync("/api/configure", new { values = new { publicUrl = "https://paper.example.com/", archiveNumbers = true } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using (var after = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()))
        {
            Assert.Equal("https://paper.example.com", after.RootElement.GetProperty("values").GetProperty("publicUrl").GetString());
            Assert.True(after.RootElement.GetProperty("values").GetProperty("archiveNumbers").GetBoolean());
        }

        var file = await File.ReadAllTextAsync(Path.Combine(site.DataFolder, "config", "app.json"));
        Assert.Contains("https://paper.example.com", file);

        // Back to off for the other tests.
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsJsonAsync("/api/configure", new { values = new { archiveNumbers = false, publicUrl = "" } })).StatusCode);
    }

    [DbFact]
    public async Task Configure_refuses_a_wrong_value_and_saves_nothing_then()
    {
        var api = site.Api();

        foreach (var wrong in new object[]
                 {
                     new { language = "fr-FR" },
                     new { publicUrl = "not an address" },
                     new { timeZone = "Mars/Olympus" },
                     new { archiveNumbers = "yes" },
                     new { timeZone = "" },
                     new { nothing = 1 },
                     new { archiveNumbers = true, language = "xx" },
                 })
        {
            var response = await api.PutAsJsonAsync("/api/configure", new { values = wrong });
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{JsonSerializer.Serialize(wrong)}: {(int)response.StatusCode}");
            using var errors = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Object, errors.RootElement.GetProperty("errors").ValueKind);
        }

        using var unchanged = JsonDocument.Parse(await api.GetStringAsync("/api/configure"));
        var values = unchanged.RootElement.GetProperty("values");
        Assert.False(values.GetProperty("archiveNumbers").GetBoolean());
        Assert.Equal("de-DE", values.GetProperty("language").GetString());
        Assert.Equal("Europe/Berlin", values.GetProperty("timeZone").GetString());
    }

    [DbFact]
    public async Task An_unknown_action_and_a_link_action_are_404()
    {
        var api = site.Api();
        Assert.Equal(HttpStatusCode.NotFound, (await api.PostAsync("/api/actions/nothing", null)).StatusCode);
        // A link action is not run by POST.
        Assert.Equal(HttpStatusCode.NotFound, (await api.PostAsync("/api/actions/openAdmin", null)).StatusCode);
    }

    /// <summary>
    /// Five documents of the administrator: a filed one (1000 bytes), one in the inbox waiting for text recognition (2000), one in the inbox whose
    /// recognition failed (4000), a deleted one (8000) and an ignored one (16000); the last two do not count.
    /// </summary>
    private async Task AddSampleDocumentsAsync()
    {
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = await db.Users.SingleAsync(u => u.Username == PaperFactory.AdminUser);
        var now = DateTime.UtcNow;

        Document Doc(string title, long size, ReviewState review, OcrState ocr, UpdateState update) => new()
        {
            Token = Guid.NewGuid(),
            Title = title,
            OriginalFileName = title + ".pdf",
            RelativePath = title + ".pdf",
            FileSize = size,
            OwnerId = admin.Id,
            ReviewState = review,
            OcrState = ocr,
            UpdateState = update,
            IsStaged = true,
            CreateDate = now,
            UpdateDate = now,
        };

        db.Documents.AddRange(
            Doc("filed", 1000, ReviewState.Reviewed, OcrState.Done, UpdateState.Created),
            Doc("waiting", 2000, ReviewState.Pending, OcrState.Pending, UpdateState.Created),
            Doc("failed", 4000, ReviewState.Pending, OcrState.Failed, UpdateState.Created),
            Doc("deleted", 8000, ReviewState.Pending, OcrState.Failed, UpdateState.Deleted),
            Doc("ignored", 16000, ReviewState.Ignored, OcrState.Deferred, UpdateState.Created));
        await db.SaveChangesAsync();
    }
}

/// <summary>Without <c>MAT_API_KEY</c> the key is off: the protected endpoints answer 401 to every key.</summary>
public sealed class PlatformWithoutKeyTests(NoKeyFactory site) : IClassFixture<NoKeyFactory>
{
    [DbFact]
    public async Task Every_protected_endpoint_is_401_and_the_definition_says_so()
    {
        var client = site.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", PaperFactory.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/configure")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/actions/nothing", null)).StatusCode);

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api"));
        Assert.False(doc.RootElement.GetProperty("auth").GetProperty("apiKey").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
    }
}

/// <summary>Before the setup (no user yet) the platform answers anyway: no redirect to the setup page, and the health says what is missing.</summary>
public sealed class PlatformBeforeSetupTests(FreshFactory site) : IClassFixture<FreshFactory>
{
    [DbFact]
    public async Task The_api_is_not_sent_to_the_setup_and_the_health_is_degraded()
    {
        var client = site.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api")).StatusCode);

        var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var doc = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
        Assert.Equal("degraded", doc.RootElement.GetProperty("status").GetString());
        Assert.Contains(doc.RootElement.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "administrators");

        // The pages still go to the setup.
        var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/Account/Setup", Browser.PathOf(page.Headers.Location!));
    }
}
