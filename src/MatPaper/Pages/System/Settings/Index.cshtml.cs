using System.Globalization;
using MatPaper.Configuration;
using MatPaper.Services;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace MatPaper.Pages.System.Settings;

/// <summary>
/// Instance settings that used to live only in /data/config/app.json: the public address (needed
/// for the OAuth redirect URI), the display/schedule time zone and the default UI language. A save
/// writes app.json first and applies the values to the running app only once that succeeded, so
/// file and runtime never disagree. Values pinned by an environment variable are shown read-only.
/// Database, data and inbox folders are startup-only and therefore informational.
/// </summary>
public class IndexModel : PageModel
{
    private const string PublicUrlEnvVar = "MATPAPER_PUBLIC_URL";
    private const string InboxEnvVar = "MATPAPER_INBOX";

    private readonly AppConfig _config;
    private readonly Fmt _fmt;
    private readonly OAuthService _oauth;
    private readonly RequestLocalizationOptions _localization;
    private readonly ILogger<IndexModel> _logger;
    private readonly IStringLocalizer<SharedResource> _l;

    public IndexModel(
        AppConfig config,
        Fmt fmt,
        OAuthService oauth,
        IOptions<RequestLocalizationOptions> localization,
        ILogger<IndexModel> logger,
        IStringLocalizer<SharedResource> l)
    {
        _config = config;
        _fmt = fmt;
        _oauth = oauth;
        _localization = localization.Value;
        _logger = logger;
        _l = l;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        public string? PublicBaseUrl { get; set; }
        public string? TimeZone { get; set; }
        public string? Culture { get; set; }
    }

    public List<SelectListItem> TimeZoneOptions { get; private set; } = new();
    public List<SelectListItem> CultureOptions { get; private set; } = new();

    /// <summary>Value of the environment variable that pins a field, or null when it is editable.</summary>
    public string? PublicUrlFromEnv { get; } = EnvValue(PublicUrlEnvVar);
    public string? TimeZoneFromEnv { get; } = EnvValue(Fmt.TimeZoneEnvVar);
    public string? InboxFromEnv { get; } = EnvValue(InboxEnvVar);

    // Read-only facts about this instance.
    public string? RedirectUri => _oauth.RedirectUri();
    public string Version => AppInfo.Version;
    public string DataDir => AppInfo.DataDir;
    public string ConfigPath => AppConfigLoader.ConfigPath(AppInfo.DataDir);
    public string InboxPath => _config.ResolveInboxPath(AppInfo.DataDir);
    public string Database => $"{_config.Database.Host}:{_config.Database.Port} / {_config.Database.Database} ({_config.Database.Username})";
    public string ActiveTimeZone => _fmt.ZoneName;
    public string NowInZone => _fmt.DateTime(DateTime.UtcNow);

    public void OnGet()
    {
        SetBreadcrumb();
        Input = new InputModel
        {
            PublicBaseUrl = _config.OAuth?.PublicBaseUrl,
            TimeZone = _config.Display?.TimeZone,
            Culture = _config.Display?.Culture,
        };
        BuildOptions();
    }

    public IActionResult OnPost()
    {
        SetBreadcrumb();

        // Fields pinned by the environment are not posted (disabled); keep the file's value.
        var publicUrl = PublicUrlFromEnv is null ? NormalizePublicUrl(Input.PublicBaseUrl) : _config.OAuth?.PublicBaseUrl;
        var timeZone = TimeZoneFromEnv is null ? Input.TimeZone?.Trim() : _config.Display?.TimeZone;
        var culture = SupportedCulture(Input.Culture);

        if (TimeZoneFromEnv is null && !Fmt.IsKnownTimeZone(timeZone))
        {
            ModelState.AddModelError("Input.TimeZone", _l["Choose a time zone from the list."]);
        }
        if (culture is null)
        {
            ModelState.AddModelError("Input.Culture", _l["Choose a language from the list."]);
        }

        if (!ModelState.IsValid)
        {
            BuildOptions();
            return Page();
        }

        // Validate + save a copy first; only touch the running app once the file is written.
        // A section written as null in app.json deserializes to null; start it fresh then.
        var updated = AppConfigLoader.Clone(_config);
        updated.OAuth ??= new OAuthConfig();
        updated.Display ??= new DisplayConfig();
        updated.OAuth.PublicBaseUrl = publicUrl;
        updated.Display.TimeZone = timeZone!;
        updated.Display.Culture = culture!.Name;

        try
        {
            AppConfigLoader.Save(AppInfo.DataDir, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write {ConfigPath}.", ConfigPath);
            ModelState.AddModelError(string.Empty,
                _l["The settings could not be saved: {0} is not writable. Nothing was changed.", ConfigPath]);
            BuildOptions();
            return Page();
        }

        _config.OAuth ??= new OAuthConfig();
        _config.Display ??= new DisplayConfig();
        _config.OAuth.PublicBaseUrl = publicUrl;
        _config.Display.TimeZone = timeZone!;
        _config.Display.Culture = culture.Name;
        _fmt.UseTimeZone(timeZone);
        _localization.DefaultRequestCulture = new RequestCulture(culture);

        this.Notify(_l["Settings saved."].Value);
        return RedirectToPage();
    }

    /// <summary>
    /// Empty → null. Otherwise an absolute http(s) URL without query, fragment or user info;
    /// a path (reverse-proxy sub-path) is kept, a trailing slash is dropped.
    /// </summary>
    private string? NormalizePublicUrl(string? value)
    {
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
            ModelState.AddModelError("Input.PublicBaseUrl",
                _l["Enter the full address starting with http:// or https://, without ? or #."]);
            return trimmed;
        }

        return (uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath).TrimEnd('/');
    }

    /// <summary>The selectable UI languages: the regional cultures only — the neutral "de"/"en"
    /// entries exist just so browsers asking for a bare language are matched (see Program.cs).</summary>
    private IEnumerable<CultureInfo> Languages
        => (_localization.SupportedUICultures ?? Array.Empty<CultureInfo>()).Where(c => !c.IsNeutralCulture);

    private CultureInfo? SupportedCulture(string? name)
        => Languages.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private void BuildOptions()
    {
        var now = DateTime.UtcNow;
        TimeZoneOptions = TimeZoneInfo.GetSystemTimeZones()
            .Where(z => z.Id.Contains('/') || z.Id == "UTC")
            .OrderBy(z => z.Id, StringComparer.OrdinalIgnoreCase)
            .Select(z =>
            {
                var offset = z.GetUtcOffset(now);
                var sign = offset < TimeSpan.Zero ? "−" : "+";
                return new SelectListItem($"{z.Id} (UTC{sign}{offset.Duration():hh\\:mm})", z.Id);
            })
            .ToList();

        // A configured id the list does not contain (e.g. a legacy alias) stays selectable.
        if (!string.IsNullOrWhiteSpace(Input.TimeZone) && TimeZoneOptions.All(o => o.Value != Input.TimeZone))
        {
            TimeZoneOptions.Insert(0, new SelectListItem(Input.TimeZone, Input.TimeZone));
        }

        CultureOptions = Languages
            .Select(c => new SelectListItem(c.Name switch
            {
                "de-DE" => "Deutsch",
                "en-US" => "English",
                _ => c.NativeName
            }, c.Name))
            .ToList();
    }

    private static string? EnvValue(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private void SetBreadcrumb()
    {
        ViewData["Breadcrumb"] = "System / Settings";
    }
}
