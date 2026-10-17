using System.Text.Json;
using MatPaper.Configuration;
using MatPaper.Data;
using MatPaper.Platform;
using MatPaper.Services;
using MatPlatform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MatPaper.Tests;

/// <summary>
/// The four settings of the platform form, without a database: <see cref="InstanceSettingsService"/> (checked, written to app.json, then in effect)
/// and <see cref="PaperPlatform"/> as <c>GET</c>/<c>PUT /api/configure</c> hands them over.
/// </summary>
public sealed class InstanceSettingsTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "matpaper-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousDataDir = AppInfo.DataDir;
    private readonly AppConfig _config = new();
    private readonly Fmt _fmt;
    private readonly IOptions<RequestLocalizationOptions> _localization = Options.Create(new RequestLocalizationOptions());
    private readonly InstanceSettingsService _settings;

    public InstanceSettingsTests()
    {
        Directory.CreateDirectory(Path.Combine(_data, "config"));
        AppInfo.DataDir = _data;
        Environment.SetEnvironmentVariable("MATPAPER_TZ", null);
        Environment.SetEnvironmentVariable(InstanceSettingsService.PublicUrlEnvVar, null);
        _fmt = new Fmt(_config);
        _settings = new InstanceSettingsService(_config, _fmt, _localization, NullLogger<InstanceSettingsService>.Instance);
    }

    public void Dispose()
    {
        AppInfo.DataDir = _previousDataDir;
        Environment.SetEnvironmentVariable("MATPAPER_TZ", null);
        Environment.SetEnvironmentVariable(InstanceSettingsService.PublicUrlEnvVar, null);
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_change_is_written_to_app_json_and_in_effect_at_once()
    {
        var problems = _settings.Save(new InstanceSettingsChange("https://paper.example.com/", "Europe/London", "en-US", true), "test");

        Assert.Empty(problems);
        Assert.Equal(new InstanceSettings("https://paper.example.com", "Europe/London", "en-US", true), _settings.Get());
        Assert.Equal("Europe/London", _fmt.ZoneName);
        Assert.Equal("en-US", _localization.Value.DefaultRequestCulture.UICulture.Name);

        var saved = AppConfigLoader.Load(_data);
        Assert.Equal("https://paper.example.com", saved.OAuth.PublicBaseUrl);
        Assert.Equal("Europe/London", saved.Display.TimeZone);
        Assert.Equal("en-US", saved.Display.Culture);
        Assert.True(saved.Barcodes.Enabled);

        // Null leaves a value, an empty address clears it.
        Assert.Empty(_settings.Save(new InstanceSettingsChange(PublicBaseUrl: ""), "test"));
        Assert.Equal(new InstanceSettings(null, "Europe/London", "en-US", true), _settings.Get());
    }

    [Fact]
    public void Wrong_values_save_nothing()
    {
        var problems = _settings.Save(new InstanceSettingsChange("ftp://x?y", "Mars/Olympus", "fr-FR", false), "test");

        Assert.Equal(SettingsProblem.Invalid, problems[nameof(InstanceSettings.PublicBaseUrl)]);
        Assert.Equal(SettingsProblem.Invalid, problems[nameof(InstanceSettings.TimeZone)]);
        Assert.Equal(SettingsProblem.Invalid, problems[nameof(InstanceSettings.Language)]);
        Assert.False(File.Exists(AppConfigLoader.ConfigPath(_data)));
        Assert.Equal(new InstanceSettings(null, "Europe/Berlin", "de-DE", false), _settings.Get());
    }

    [Fact]
    public void A_value_pinned_by_the_environment_is_shown_and_cannot_be_changed()
    {
        Environment.SetEnvironmentVariable("MATPAPER_TZ", "UTC");
        Environment.SetEnvironmentVariable(InstanceSettingsService.PublicUrlEnvVar, "https://pinned.example.com");

        Assert.Equal("UTC", _settings.Get().TimeZone);
        Assert.Equal("https://pinned.example.com", _settings.Get().PublicBaseUrl);

        var problems = _settings.Save(new InstanceSettingsChange("https://other.example.com", "Europe/Paris"), "test");
        Assert.Equal(SettingsProblem.Pinned, problems[nameof(InstanceSettings.PublicBaseUrl)]);
        Assert.Equal(SettingsProblem.Pinned, problems[nameof(InstanceSettings.TimeZone)]);

        // The same value as in effect is no change.
        Assert.Empty(_settings.Save(new InstanceSettingsChange("https://pinned.example.com", "UTC", ArchiveNumbers: true), "test"));
        Assert.True(_settings.Get().ArchiveNumbers);
    }

    [Fact]
    public async Task The_platform_answers_the_settings_by_their_keys_and_the_problems_in_the_callers_language()
    {
        var platform = Platform();
        var english = new MatPlatformCall(new DefaultHttpContext(), "en", "mattiverse");

        var values = await platform.GetConfigurationAsync(english, CancellationToken.None);
        Assert.Equal(["publicUrl", "timeZone", "language", "archiveNumbers"], values.Keys);
        Assert.Equal(string.Empty, values["publicUrl"]);
        Assert.Equal("Europe/Berlin", values["timeZone"]);
        Assert.Equal("de-DE", values["language"]);
        Assert.Equal(false, values["archiveNumbers"]);

        var ok = await platform.ConfigureAsync(Json(new { timeZone = "Europe/Vienna", archiveNumbers = true }), english, CancellationToken.None);
        Assert.Empty(ok);
        Assert.Equal("Europe/Vienna", _settings.Get().TimeZone);
        Assert.True(_settings.Get().ArchiveNumbers);

        var bad = await platform.ConfigureAsync(Json(new { timeZone = "Nowhere", archiveNumbers = "yes", nothing = 1 }), english, CancellationToken.None);
        Assert.Equal("Please give true or false.", bad["archiveNumbers"]);
        Assert.Equal("There is no such setting.", bad["nothing"]);

        var german = new MatPlatformCall(new DefaultHttpContext(), "de", "mattiverse");
        var zone = await platform.ConfigureAsync(Json(new { timeZone = "Nowhere" }), german, CancellationToken.None);
        Assert.StartsWith("Unbekannte Zeitzone", zone["timeZone"]);
        Assert.Equal("Europe/Vienna", _settings.Get().TimeZone);
    }

    /// <summary>The platform class with a status service whose database is never asked (no query in these tests).</summary>
    private PaperPlatform Platform()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=none").Options);
        return new PaperPlatform(new InstanceStatusService(db, NullLogger<InstanceStatusService>.Instance), _settings);
    }

    private static IReadOnlyDictionary<string, JsonElement> Json(object values)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;
}
