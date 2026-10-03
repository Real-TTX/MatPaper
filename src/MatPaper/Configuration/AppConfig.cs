using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatPaper.Configuration;

public class DatabaseConfig
{
    public string Host { get; set; } = "db";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "matpaper";
    public string Username { get; set; } = "postgres";
    public string Password { get; set; } = "matpaper";

    [JsonIgnore]
    public string ConnectionString =>
        $"Host={Host};Port={Port};Database={Database};Username={Username};Password={Password}";
}

public class StorageConfig
{
    /// <summary>
    /// Folder holding documents that wait in the review inbox (not filed yet). Must be a
    /// local path inside the container (a mounted NAS share is fine). Null = "{data}/inbox".
    /// The environment variable MATPAPER_INBOX overrides this value.
    /// </summary>
    public string? InboxPath { get; set; }
}

public class DisplayConfig
{
    /// <summary>
    /// IANA time zone used for every date/time shown in the UI and for evaluating task
    /// schedules ("daily at 08:00" means 08:00 in this zone). Falls back to UTC when the
    /// system does not know the id. The environment variable MATPAPER_TZ overrides it.
    /// </summary>
    public string TimeZone { get; set; } = "Europe/Berlin";

    /// <summary>
    /// UI language used when the visitor has not picked one: "de-DE" or "en-US".
    /// Untranslated strings always fall back to their English original.
    /// </summary>
    public string Culture { get; set; } = "de-DE";
}

public class OAuthConfig
{
    /// <summary>
    /// The public base URL under which this instance is reachable, e.g.
    /// "https://papers.example.com". The OAuth redirect URI is this plus
    /// "/System/Connections/OAuthCallback" and must be registered verbatim with Google
    /// and Microsoft. Behind a reverse proxy the app cannot discover it reliably, so it
    /// is configured here. The environment variable MATPAPER_PUBLIC_URL overrides it.
    /// </summary>
    public string? PublicBaseUrl { get; set; }
}

public class AppConfig
{
    public DatabaseConfig Database { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public DisplayConfig Display { get; set; } = new();
    public OAuthConfig OAuth { get; set; } = new();

    /// <summary>Effective public base URL: env MATPAPER_PUBLIC_URL → config → null.</summary>
    public string? ResolvePublicBaseUrl()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MATPAPER_PUBLIC_URL");
        var value = !string.IsNullOrWhiteSpace(fromEnv) ? fromEnv : OAuth?.PublicBaseUrl;
        return string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('/');
    }

    /// <summary>Effective inbox staging folder: env MATPAPER_INBOX → config → {dataDir}/inbox.</summary>
    public string ResolveInboxPath(string dataDir)
    {
        var fromEnv = Environment.GetEnvironmentVariable("MATPAPER_INBOX");
        var path = !string.IsNullOrWhiteSpace(fromEnv) ? fromEnv
            : !string.IsNullOrWhiteSpace(Storage?.InboxPath) ? Storage!.InboxPath!
            : Path.Combine(dataDir, "inbox");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}

public static class AppConfigLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private static readonly object SaveLock = new();

    /// <summary>Full path of the config file inside <paramref name="dataDir"/>.</summary>
    public static string ConfigPath(string dataDir) => Path.Combine(dataDir, "config", "app.json");

    /// <summary>
    /// Writes <paramref name="config"/> to app.json in the same format <see cref="Load"/> produces.
    /// The file is replaced atomically (temp file + move), so a crash never leaves a half-written
    /// config. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the
    /// config folder is not writable (e.g. a read-only mount).
    /// </summary>
    public static void Save(string dataDir, AppConfig config)
    {
        var configPath = ConfigPath(dataDir);
        var json = JsonSerializer.Serialize(config, SerializerOptions);

        lock (SaveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var temp = configPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, configPath, overwrite: true);
        }
    }

    /// <summary>A detached deep copy, so changes can be validated and saved before the live config is touched.</summary>
    public static AppConfig Clone(AppConfig config)
        => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, SerializerOptions)) ?? new AppConfig();

    public static AppConfig Load(string dataDir)
    {
        var configDir = Path.Combine(dataDir, "config");
        Directory.CreateDirectory(configDir);

        var configPath = ConfigPath(dataDir);

        if (!File.Exists(configPath))
        {
            var defaults = new AppConfig();
            var json = JsonSerializer.Serialize(defaults, SerializerOptions);
            File.WriteAllText(configPath, json);
            return defaults;
        }

        var existing = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<AppConfig>(existing) ?? new AppConfig();

        // Write the file back so sections added by a newer version show up with their
        // defaults and can be edited instead of staying invisible.
        var normalized = JsonSerializer.Serialize(config, SerializerOptions);
        if (!string.Equals(normalized, existing.Trim(), StringComparison.Ordinal))
        {
            try
            {
                File.WriteAllText(configPath, normalized);
            }
            catch (IOException)
            {
                // read-only config mount: keep running with the values we loaded
            }
        }

        return config;
    }
}
