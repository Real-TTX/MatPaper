using System.Globalization;
using MatPaper.Configuration;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;

namespace MatPaper.Services;

/// <summary>The few settings of the installation that an administrator sets from outside (the platform contract, <c>/api/configure</c>).</summary>
public sealed record InstanceSettings(string? PublicBaseUrl, string TimeZone, string Language, bool ArchiveNumbers);

/// <summary>
/// The rudimentary settings of MatPaper as one unit: the public address (System, Settings), the time zone and the default language
/// (System, Settings) and the archive number feature as a whole (System, Archive numbers). They live in <c>app.json</c> like on those pages:
/// a change is checked, written to the file first and only then applied to the running app, so file and runtime never disagree.
/// A value pinned by an environment variable (<c>MATPAPER_PUBLIC_URL</c>, <c>MATPAPER_TZ</c>) is shown as it is in effect and cannot be changed here.
/// </summary>
public sealed class InstanceSettingsService(
    AppConfig config,
    Fmt fmt,
    IOptions<RequestLocalizationOptions> localization,
    ILogger<InstanceSettingsService> logger)
{
    public const string PublicUrlEnvVar = "MATPAPER_PUBLIC_URL";

    /// <summary>The languages a visitor gets when the browser does not choose one.</summary>
    public static readonly IReadOnlyList<string> Languages = ["de-DE", "en-US"];

    public InstanceSettings Get() => new(
        Env(PublicUrlEnvVar) ?? config.OAuth?.PublicBaseUrl,
        Env(Fmt.TimeZoneEnvVar) ?? config.Display?.TimeZone ?? fmt.ZoneName,
        config.Display?.Culture ?? Languages[0],
        config.ArchiveNumbersEnabled);

    /// <summary>True when the environment variable <paramref name="name"/> pins the value.</summary>
    public static bool IsPinned(string name) => Env(name) is not null;

    /// <summary>
    /// Changes the settings that are not null in <paramref name="change"/>. Answers the problems under the names of the record's properties (an empty name: the file could not be written)
    /// (nothing is saved when there is one), or an empty dictionary when the settings are saved and in effect.
    /// </summary>
    public IReadOnlyDictionary<string, SettingsProblem> Save(InstanceSettingsChange change, string changedBy)
    {
        ArgumentNullException.ThrowIfNull(change);
        var errors = new Dictionary<string, SettingsProblem>();
        var current = Get();

        string? publicUrl = config.OAuth?.PublicBaseUrl;
        if (change.PublicBaseUrl is not null)
        {
            var normalized = NormalizePublicUrl(change.PublicBaseUrl, out var valid);
            if (!valid)
            {
                errors[nameof(InstanceSettings.PublicBaseUrl)] = SettingsProblem.Invalid;
            }
            else if (IsPinned(PublicUrlEnvVar) && normalized != current.PublicBaseUrl)
            {
                errors[nameof(InstanceSettings.PublicBaseUrl)] = SettingsProblem.Pinned;
            }
            else if (!IsPinned(PublicUrlEnvVar))
            {
                publicUrl = normalized;
            }
        }

        string? timeZone = config.Display?.TimeZone;
        if (change.TimeZone is not null)
        {
            var wanted = change.TimeZone.Trim();
            if (!Fmt.IsKnownTimeZone(wanted))
            {
                errors[nameof(InstanceSettings.TimeZone)] = SettingsProblem.Invalid;
            }
            else if (IsPinned(Fmt.TimeZoneEnvVar) && wanted != current.TimeZone)
            {
                errors[nameof(InstanceSettings.TimeZone)] = SettingsProblem.Pinned;
            }
            else if (!IsPinned(Fmt.TimeZoneEnvVar))
            {
                timeZone = wanted;
            }
        }

        var language = config.Display?.Culture ?? Languages[0];
        if (change.Language is not null)
        {
            var wanted = Languages.FirstOrDefault(l => string.Equals(l, change.Language.Trim(), StringComparison.OrdinalIgnoreCase));
            if (wanted is null)
            {
                errors[nameof(InstanceSettings.Language)] = SettingsProblem.Invalid;
            }
            else
            {
                language = wanted;
            }
        }

        var archiveNumbers = change.ArchiveNumbers ?? config.ArchiveNumbersEnabled;

        if (errors.Count > 0)
        {
            return errors;
        }

        var updated = AppConfigLoader.Clone(config);
        updated.OAuth ??= new OAuthConfig();
        updated.Display ??= new DisplayConfig();
        updated.Barcodes ??= new BarcodeConfig();
        updated.OAuth.PublicBaseUrl = publicUrl;
        updated.Display.TimeZone = timeZone ?? updated.Display.TimeZone;
        updated.Display.Culture = language;
        updated.Barcodes.Enabled = archiveNumbers;

        try
        {
            AppConfigLoader.Save(AppInfo.DataDir, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write {ConfigPath}.", AppConfigLoader.ConfigPath(AppInfo.DataDir));
            return new Dictionary<string, SettingsProblem> { [string.Empty] = SettingsProblem.NotWritable };
        }

        config.OAuth ??= new OAuthConfig();
        config.Display ??= new DisplayConfig();
        config.Barcodes ??= new BarcodeConfig();
        config.OAuth.PublicBaseUrl = publicUrl;
        config.Display.TimeZone = updated.Display.TimeZone;
        config.Display.Culture = language;
        config.Barcodes.Enabled = archiveNumbers;
        fmt.UseTimeZone(config.Display.TimeZone);
        localization.Value.DefaultRequestCulture = new RequestCulture(CultureInfo.GetCultureInfo(language));

        logger.LogInformation(
            "Settings changed by {ChangedBy}: public address {PublicUrl}, time zone {TimeZone}, language {Language}, archive numbers {ArchiveNumbers}",
            changedBy, publicUrl ?? "(none)", config.Display.TimeZone, language, archiveNumbers ? "on" : "off");

        return new Dictionary<string, SettingsProblem>();
    }

    /// <summary>
    /// Empty → null. Otherwise an absolute http(s) URL without query, fragment or user info; a path (reverse-proxy sub-path) is kept,
    /// a trailing slash is dropped (the rule of the settings page).
    /// </summary>
    public static string? NormalizePublicUrl(string? value, out bool valid)
    {
        valid = true;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            valid = false;
            return trimmed;
        }

        return (uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath).TrimEnd('/');
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/');
    }
}

/// <summary>A change of <see cref="InstanceSettings"/>: null = leave as it is (an empty public address clears it).</summary>
public sealed record InstanceSettingsChange(string? PublicBaseUrl = null, string? TimeZone = null, string? Language = null, bool? ArchiveNumbers = null);

/// <summary>Why a setting was not saved.</summary>
public enum SettingsProblem
{
    /// <summary>Not a valid value (an address, a known time zone, one of the languages).</summary>
    Invalid,

    /// <summary>An environment variable sets the value; it cannot be changed here.</summary>
    Pinned,

    /// <summary>app.json cannot be written; nothing was changed.</summary>
    NotWritable,
}
