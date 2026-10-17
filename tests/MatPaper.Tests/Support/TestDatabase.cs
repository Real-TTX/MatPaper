using System.Text.RegularExpressions;
using Microsoft.Net.Http.Headers;

namespace MatPaper.Tests.Support;

/// <summary>
/// Tests that start the app need PostgreSQL: <c>MATPAPER_TEST_DB</c> is an admin connection string, e.g.
/// <c>Host=localhost;Port=5432;Username=postgres;Password=matpaper</c> (every factory creates and drops a database of its own there).
/// Without it they are skipped, and the rest of the suite runs alone.
/// </summary>
public static class TestDatabase
{
    public const string Variable = "MATPAPER_TEST_DB";

    public static string? AdminConnectionString
        => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value ? value : null;

    public static bool Available => AdminConnectionString is not null;

    public const string SkipReason = Variable + " is not set (needs a PostgreSQL server).";
}

/// <summary>A test that needs PostgreSQL (<see cref="TestDatabase"/>); skipped without it.</summary>
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (!TestDatabase.Available)
        {
            Skip = TestDatabase.SkipReason;
        }
    }
}

/// <summary>A theory that needs PostgreSQL (<see cref="TestDatabase"/>); skipped without it.</summary>
public sealed class DbTheoryAttribute : TheoryAttribute
{
    public DbTheoryAttribute()
    {
        if (!TestDatabase.Available)
        {
            Skip = TestDatabase.SkipReason;
        }
    }
}

/// <summary>Where the repository is, for the files the tests read (the sprite, the icon).</summary>
public static class Repo
{
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatPaper.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("MatPaper.slnx not found above " + AppContext.BaseDirectory);
        }
    }

    public static string WebRoot => Path.Combine(Root, "src", "MatPaper", "wwwroot");

    /// <summary>MatPaper's copy of the MatUi sprite: the icons a consumer such as Mattiverse can draw.</summary>
    public static string Sprite => Path.Combine(WebRoot, "matui", "matui-icons.svg");
}

/// <summary>A browser with its own cookie jar (no redirects followed), so a cookie made outside the app (the pending link) can be put in.</summary>
public sealed class Browser(PaperFactory site)
{
    private readonly HttpClient _client = site.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
    private readonly Dictionary<string, string> _cookies = [];

    public bool Has(string name) => _cookies.ContainsKey(name);

    public void Set(string name, string value) => _cookies[name] = value;

    public void Take(Microsoft.AspNetCore.Http.HttpResponse response)
        => Take(response.Headers.SetCookie.Select(c => c ?? string.Empty));

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(new HttpRequestMessage(HttpMethod.Get, path));

    public Task<HttpResponseMessage> PostAsync(string path, Dictionary<string, string> form)
        => SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) });

    /// <summary>The local sign-in form on <paramref name="path"/>, filled in and sent.</summary>
    public async Task<HttpResponseMessage> SignInAsync(string path, string username, string password)
    {
        var html = await (await GetAsync(path)).Content.ReadAsStringAsync();
        return await PostAsync(path, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Antiforgery(html),
            ["Input.Username"] = username,
            ["Input.Password"] = password,
            ["Input.RememberMe"] = "true",
        });
    }

    public static string Antiforgery(string html)
    {
        var token = Regex.Match(html, @"name=""__RequestVerificationToken"" type=""hidden"" value=""([^""]+)""").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), "No antiforgery token");
        return token;
    }

    public static string PathOf(Uri location) => location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        if (_cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => c.Key + "=" + c.Value)));
        }

        var response = await _client.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            Take(values);
        }

        return response;
    }

    private void Take(IEnumerable<string> setCookies)
    {
        foreach (var cookie in SetCookieHeaderValue.ParseList(setCookies.ToList()))
        {
            var name = cookie.Name.Value!;
            if (string.IsNullOrEmpty(cookie.Value.Value) || cookie.Expires < DateTimeOffset.UtcNow)
            {
                _cookies.Remove(name);
            }
            else
            {
                _cookies[name] = cookie.Value.Value!;
            }
        }
    }
}
