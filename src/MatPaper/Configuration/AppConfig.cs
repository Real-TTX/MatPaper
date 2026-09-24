using System.Text.Json;

namespace MatPaper.Configuration;

public class DatabaseConfig
{
    public string Host { get; set; } = "db";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "matpaper";
    public string Username { get; set; } = "postgres";
    public string Password { get; set; } = "matpaper";

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

public class AppConfig
{
    public DatabaseConfig Database { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();

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
        return JsonSerializer.Deserialize<AppConfig>(existing) ?? new AppConfig();
    }
}
