using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MatPaper;
using MatPaper.Configuration;
using MatPaper.Data;

var builder = WebApplication.CreateBuilder(args);

// Resolve data directory and ensure required subfolders exist.
var dataDir = Environment.GetEnvironmentVariable("MATPAPER_DATA") ?? "/data";
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(Path.Combine(dataDir, "config"));
Directory.CreateDirectory(Path.Combine(dataDir, "keys"));
Directory.CreateDirectory(Path.Combine(dataDir, "thumbnails"));

// Load application configuration (creates defaults on first run).
var appConfig = AppConfigLoader.Load(dataDir);

// App version for the footer.
AppInfo.Version = Environment.GetEnvironmentVariable("APP_VERSION") ?? "local";

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(appConfig.Database.ConnectionString));

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
    .SetApplicationName("MatPaper");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.Cookie.Name = "MatPaper.Session";
    });
builder.Services.AddAuthorization();

builder.Services.AddRazorPages();

var app = builder.Build();

// Startup migration with retry to tolerate Postgres warmup.
const int maxAttempts = 10;
for (var attempt = 1; attempt <= maxAttempts; attempt++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        app.Logger.LogInformation("Applying database migrations (attempt {Attempt}/{MaxAttempts}).", attempt, maxAttempts);
        db.Database.Migrate();
        app.Logger.LogInformation("Database migrations applied successfully.");
        break;
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Migration attempt {Attempt}/{MaxAttempts} failed.", attempt, maxAttempts);
        if (attempt == maxAttempts)
        {
            throw;
        }

        Thread.Sleep(3000);
    }
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
