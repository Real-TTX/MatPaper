using System.Text.Json;
using MatPaper.Services;
using MatPlatform;

namespace MatPaper.Platform;

/// <summary>
/// MatPaper as an app of the platform contract (MatDesign docs/PLATFORM.md), on the shared package MatPlatform (the folder <c>MatPlatform/</c>, a copy):
/// how Mattiverse shows it (<see cref="Describe"/>: tiles, the few settings, links), what its values are and whether it is well.
/// The values come from <see cref="InstanceStatusService"/> and <see cref="InstanceSettingsService"/> (no DbContext here: this is the web layer).
/// Texts are German, or English when the caller asks for it with <c>Accept-Language</c>.
/// </summary>
public sealed class PaperPlatform(
    InstanceStatusService status,
    InstanceSettingsService settings) : IMatPlatformState, IMatPlatformConfiguration, IMatPlatformHealth
{
    /// <summary>The id of MatPaper in the platform (MatDesign AppRegistry, and the default client id in MatIdentity).</summary>
    public const string AppId = "matpaper";

    public const string Accent = "#268538";

    /// <summary>The fields of the platform form and the property of <see cref="InstanceSettings"/> each one is.</summary>
    private static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>
    {
        ["publicUrl"] = nameof(InstanceSettings.PublicBaseUrl),
        ["timeZone"] = nameof(InstanceSettings.TimeZone),
        ["language"] = nameof(InstanceSettings.Language),
        ["archiveNumbers"] = nameof(InstanceSettings.ArchiveNumbers),
    };

    /// <summary>The MatApiDefinition of MatPaper (<c>GET /api</c>); <c>auth</c> and <c>endpoints</c> are filled in by MatPlatform.</summary>
    public static void Describe(MatPlatformBuilder o)
    {
        var T = o.T;

        o.App(
            AppId,
            "MatPaper",
            T("Dokumentenverwaltung mit Texterkennung", "Document management with text recognition"),
            AppInfo.Version,
            "/icons/icon.svg",
            Accent);

        o.Page("overview", T("Übersicht", "Overview"), "dashboard",
            MatSection.Tiles("documents", "inbox", "ocrQueue", "ocrFailed", "storage", "lastDocument", "users"),
            MatSection.Actions("open", "openInbox", "openAdmin"));
        o.Page("settings", T("Einstellungen", "Settings"), "settings",
            MatSection.Form(T("Allgemein", "General"), "publicUrl", "timeZone", "language"),
            MatSection.Form(T("Archivnummern", "Archive numbers"), "archiveNumbers"));

        o.Tile("documents", T("Dokumente", "Documents"), icon: "file-text");
        o.Tile(new MatStateItem("inbox", T("Warten im Posteingang", "Waiting in the inboxes")) { Icon = "layers" });
        o.Tile(new MatStateItem("ocrQueue", T("Warten auf Texterkennung", "Waiting for text recognition")) { Icon = "clock", WarnAbove = 50 });
        o.Tile(new MatStateItem("ocrFailed", T("Texterkennung fehlgeschlagen", "Text recognition failed")) { Icon = "alert", WarnAbove = 0 });
        o.Tile("storage", T("Belegter Speicher", "Storage used"), icon: "hard-drive", unit: "bytes");
        o.Tile("lastDocument", T("Letztes Dokument hinzugefügt", "Last document added"), "time", icon: "upload");
        o.Tile("users", T("Aktive Benutzer", "Active users"), icon: "users");

        o.Field(new MatField("publicUrl", T("Öffentliche Adresse", "Public address"), "url")
        {
            Placeholder = "https://paper.example.com",
            Hint = T(
                "Unter dieser Adresse ist MatPaper erreichbar; nötig für die Anmeldung bei Google und Microsoft (Verbindungen). Leer: keine.",
                "The address MatPaper is reached at; needed for signing in to Google and Microsoft (connections). Empty: none."),
        });
        o.Field(new MatField("timeZone", T("Zeitzone", "Time zone"), "text")
        {
            Required = true,
            Max = 64,
            Placeholder = "Europe/Berlin",
            Hint = T("IANA-Name; für alle angezeigten Zeiten und die Zeitpläne.", "IANA name; for every time shown and the schedules."),
        });
        o.Field(new MatField("language", T("Standardsprache", "Default language"), "select")
        {
            Required = true,
            Options = [new MatOption("de-DE", "Deutsch"), new MatOption("en-US", "English")],
            Hint = T("Wenn der Browser keine Sprache wählt.", "When the browser does not choose one."),
        });
        o.Field(new MatField("archiveNumbers", T("Archivnummern verwenden", "Use archive numbers"), "bool")
        {
            Hint = T(
                "Nummern auf Papieroriginalen, Etiketten, Trennblätter und das Lesen von Barcodes beim Hinzufügen.",
                "Numbers on paper originals, labels, separator sheets and reading barcodes of what is added."),
        });

        o.LinkAction("open", T("MatPaper öffnen", "Open MatPaper"), "external", "/");
        o.LinkAction("openInbox", T("Posteingang", "Inbox"), "layers", "/Inbox");
        o.LinkAction("openAdmin", T("Verwaltung", "Administration"), "settings", "/System");

        o.Role("admin", T("Administrator", "Administrator"));
        o.Role("user", T("Benutzer", "User"));
    }

    public async Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct)
    {
        if (!await status.CanReadDatabaseAsync(ct))
        {
            return [new MatHealthCheck("database", "down", call.T("Die Datenbank kann nicht gelesen werden.", "The database cannot be read."))];
        }

        var checks = new List<MatHealthCheck> { new("database", "ok") };
        if (await status.CountActiveAdminsAsync(ct) == 0)
        {
            checks.Add(new MatHealthCheck("administrators", "degraded",
                call.T("Kein aktiver Administrator (die Einrichtung ist noch nicht abgeschlossen?).", "No active administrator (is the setup not done yet?).")));
        }

        return checks;
    }

    public async Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct)
    {
        var snapshot = await status.GetSnapshotAsync(ct);
        return new Dictionary<string, MatStateValue>
        {
            ["documents"] = new(snapshot.Documents),
            ["inbox"] = new(snapshot.InboxWaiting),
            ["ocrQueue"] = new(snapshot.OcrQueue, snapshot.OcrQueue > 50 ? "warn" : null),
            ["ocrFailed"] = new(snapshot.OcrFailed, snapshot.OcrFailed > 0 ? "warn" : null),
            ["storage"] = new(snapshot.StorageBytes),
            ["lastDocument"] = new(snapshot.LastDocumentUtc is { } last
                ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc))
                : null),
            ["users"] = new(snapshot.Users),
        };
    }

    public Task<IReadOnlyDictionary<string, object?>> GetConfigurationAsync(MatPlatformCall call, CancellationToken ct)
    {
        var current = settings.Get();
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["publicUrl"] = current.PublicBaseUrl ?? string.Empty,
            ["timeZone"] = current.TimeZone,
            ["language"] = current.Language,
            ["archiveNumbers"] = current.ArchiveNumbers,
        });
    }

    public Task<IReadOnlyDictionary<string, string>> ConfigureAsync(IReadOnlyDictionary<string, JsonElement> values, MatPlatformCall call, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        string? publicUrl = null, timeZone = null, language = null;
        bool? archiveNumbers = null;

        foreach (var (key, value) in values)
        {
            switch (key)
            {
                case "publicUrl" when value.ValueKind == JsonValueKind.String:
                    publicUrl = value.GetString()!;
                    break;
                case "publicUrl" when value.ValueKind == JsonValueKind.Null:
                    publicUrl = string.Empty;
                    break;
                case "timeZone" when value.ValueKind == JsonValueKind.String:
                    timeZone = value.GetString()!;
                    break;
                case "language" when value.ValueKind == JsonValueKind.String:
                    language = value.GetString()!;
                    break;
                case "archiveNumbers" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    archiveNumbers = value.GetBoolean();
                    break;
                case "publicUrl" or "timeZone" or "language":
                    errors[key] = call.T("Bitte einen Text angeben.", "Please give a text.");
                    break;
                case "archiveNumbers":
                    errors[key] = call.T("Bitte true oder false angeben.", "Please give true or false.");
                    break;
                default:
                    errors[key] = call.T("Diese Einstellung gibt es nicht.", "There is no such setting.");
                    break;
            }
        }

        if (errors.Count > 0)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>>(errors);
        }

        var problems = settings.Save(new InstanceSettingsChange(publicUrl, timeZone, language, archiveNumbers), call.Caller);
        foreach (var (property, problem) in problems)
        {
            var key = Fields.FirstOrDefault(f => f.Value == property).Key ?? property;
            errors[key] = (key, problem) switch
            {
                (_, SettingsProblem.NotWritable) => call.T(
                    "Die Einstellungen konnten nicht gespeichert werden: app.json ist nicht beschreibbar. Nichts wurde geändert.",
                    "The settings could not be saved: app.json is not writable. Nothing was changed."),
                ("publicUrl", SettingsProblem.Pinned) => call.T(
                    "Die Adresse ist durch die Umgebungsvariable MATPAPER_PUBLIC_URL festgelegt.",
                    "The address is set by the environment variable MATPAPER_PUBLIC_URL."),
                ("timeZone", SettingsProblem.Pinned) => call.T(
                    "Die Zeitzone ist durch die Umgebungsvariable MATPAPER_TZ festgelegt.",
                    "The time zone is set by the environment variable MATPAPER_TZ."),
                ("publicUrl", _) => call.T(
                    "Bitte die vollständige Adresse mit http:// oder https:// angeben, ohne ? oder #.",
                    "Enter the full address starting with http:// or https://, without ? or #."),
                ("timeZone", _) => call.T(
                    "Unbekannte Zeitzone; bitte einen IANA-Namen wie Europe/Berlin angeben.",
                    "Unknown time zone; use an IANA name such as Europe/Berlin."),
                _ => call.T("Ungültiger Wert.", "Invalid value."),
            };
        }

        return Task.FromResult<IReadOnlyDictionary<string, string>>(errors);
    }
}
