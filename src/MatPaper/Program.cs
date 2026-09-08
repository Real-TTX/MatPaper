using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MatPaper;
using MatPaper.Configuration;
using MatPaper.Data;
using MatPaper.Services;

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

// Authentication / session / authorization services.
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<PasswordHasher<User>>();
builder.Services.AddScoped<SignInService>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddSingleton<SetupState>();
builder.Services.AddScoped<SessionCookieEvents>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "MatPaper.Session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.IsEssential = true;
        o.LoginPath = "/Account/Login";
        o.LogoutPath = "/Account/Logout";
        o.AccessDeniedPath = "/Account/AccessDenied";
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.EventsType = typeof(SessionCookieEvents);
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToFolder("/Account");
    options.Conventions.AuthorizeFolder("/System", "AdminOnly");
});

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
app.UseMiddleware<SetupRedirectMiddleware>();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
