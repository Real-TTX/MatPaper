using System.Text.Json;
using MatPaper.Configuration;
using MatPaper.Services;
using MatPlatform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;

// The factories hand their settings to Program through environment variables (Program reads them before the host is built),
// so the test classes run one after another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MatPaper.Tests.Support;

/// <summary>
/// The whole app in memory (<see cref="WebApplicationFactory{TEntryPoint}"/>) on an empty data folder and a database of its own on the server of
/// <c>MATPAPER_TEST_DB</c> (the app creates and migrates it at the start, the factory drops it), with the first administrator
/// (<see cref="AdminUser"/>) unless <c>seedAdmin</c> is false. The background workers (text recognition, import/export schedules) stay off.
/// <see cref="ApiKey"/> is <c>MAT_API_KEY</c> unless <c>withKey</c> is false; with an <c>authority</c> the sign-in through MatIdentity is on,
/// with a fixed OpenID configuration so that no MatIdentity has to run (the tests look at the redirects).
/// </summary>
public class PaperFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-key-0123456789";
    public const string AdminUser = "admin";
    public const string AdminPassword = "Admin-Passw0rd!";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "matpaper-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _database = "matpaper_t_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string? _authority;
    private readonly bool _reachable;
    private readonly bool _seedAdmin;

    public PaperFactory()
        : this(withKey: true, authority: null)
    {
    }

    protected PaperFactory(bool withKey, string? authority, bool reachable = true, bool seedAdmin = true)
    {
        _authority = authority;
        _reachable = reachable;
        _seedAdmin = seedAdmin;

        // app.json with the test database; Program reads it from MATPAPER_DATA.
        var config = new AppConfig();
        if (TestDatabase.AdminConnectionString is { } admin)
        {
            var server = new NpgsqlConnectionStringBuilder(admin);
            config.Database = new DatabaseConfig
            {
                Host = server.Host ?? "localhost",
                Port = server.Port,
                Database = _database,
                Username = server.Username ?? "postgres",
                Password = server.Password ?? string.Empty,
            };
        }

        Directory.CreateDirectory(Path.Combine(_data, "config"));
        File.WriteAllText(AppConfigLoader.ConfigPath(_data), JsonSerializer.Serialize(config));

        Environment.SetEnvironmentVariable("MATPAPER_DATA", _data);
        Environment.SetEnvironmentVariable("MATPAPER_INBOX", null);
        Environment.SetEnvironmentVariable("MATPAPER_TZ", null);
        Environment.SetEnvironmentVariable(InstanceSettingsService.PublicUrlEnvVar, null);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ApiKeyVariable, withKey ? ApiKey : null);
        Environment.SetEnvironmentVariable(MatPlatformSettings.AuthorityVariable, authority);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ClientIdVariable, null);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ClientSecretVariable, authority is null ? null : "test-secret");
        Environment.SetEnvironmentVariable(MatPlatformSettings.InternalUrlVariable, null);
    }

    public string DataFolder => _data;

    public string DatabaseName => _database;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureTestServices(services =>
        {
            // No text recognition or schedules in the background: the tests look at the numbers they put in.
            foreach (var worker in services.Where(d => d.ServiceType == typeof(IHostedService)
                         && (d.ImplementationType == typeof(DocumentProcessingService) || d.ImplementationType == typeof(TaskSchedulerService))).ToList())
            {
                services.Remove(worker);
            }
        });

        if (_authority is not null && _reachable)
        {
            var configuration = new OpenIdConnectConfiguration
            {
                Issuer = _authority,
                AuthorizationEndpoint = _authority + "/connect/authorize",
                TokenEndpoint = _authority + "/connect/token",
                EndSessionEndpoint = _authority + "/connect/logout",
            };

            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<OpenIdConnectOptions>(MatIdentitySignIn.Scheme, options =>
                {
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
                services.PostConfigure<JwtBearerOptions>(MatApiAccess.BearerScheme, options =>
                {
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
            });
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        if (_seedAdmin)
        {
            using var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<SignInService>()
                .CreateUserAsync(AdminUser, "Administrator", "admin@example.com", AdminPassword, ExternalSignInService.AdminRole, isActive: true, actingUserId: null)
                .GetAwaiter().GetResult();
        }

        return host;
    }

    /// <summary>A client that keeps cookies and does not follow redirects (the tests look at each step).</summary>
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public HttpClient Api()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        NpgsqlConnection.ClearAllPools();

        if (TestDatabase.AdminConnectionString is { } admin)
        {
            try
            {
                using var connection = new NpgsqlConnection(admin);
                connection.Open();
                using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
                command.ExecuteNonQuery();
            }
            catch (NpgsqlException)
            {
                // The server is gone already; nothing to clean up there.
            }
        }

        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Without <c>MAT_API_KEY</c>: the protected endpoints answer 401 to everyone.</summary>
public sealed class NoKeyFactory() : PaperFactory(withKey: false, authority: null);

/// <summary>A fresh installation: nobody has set it up yet (no user).</summary>
public sealed class FreshFactory() : PaperFactory(withKey: true, authority: null, seedAdmin: false);

/// <summary>With <c>MATIDENTITY_AUTHORITY</c> (a MatIdentity that does not need to run).</summary>
public sealed class IdentityFactory() : PaperFactory(withKey: true, authority: IdentityFactory.Authority)
{
    public const string Authority = "http://identity.test";
}

/// <summary>With <c>MATIDENTITY_AUTHORITY</c> pointing at an address where nothing answers (MatIdentity is down).</summary>
public sealed class UnreachableIdentityFactory() : PaperFactory(withKey: true, authority: "http://127.0.0.1:9", reachable: false);
