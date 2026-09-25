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

public class AppConfig
{
    public DatabaseConfig Database { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public DisplayConfig Display { get; set; } = new();

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

    public static AppConfig Load(string dataDir)
    {
        var configDir = Path.Combine(dataDir, "config");
        Directory.CreateDirectory(configDir);

        var configPath = Path.Combine(configDir, "app.json");

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
