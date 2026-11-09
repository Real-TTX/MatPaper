using MatPaper.Platform;
using MatPaper.Tests.Support;
using MatPlatform;

namespace MatPaper.Tests;

/// <summary>
/// The definition of MatPaper (no database needed): valid for MatPlatform's validator in both languages against MatPaper's copy of the MatUi sprite, also with
/// <c>MAT_PUBLIC_URL</c>; contract 2: what it consumes, its launcher, quick actions and widgets, the public address fixed by <c>MAT_PUBLIC_URL</c>.
/// </summary>
public sealed class PlatformDefinitionTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_description_is_valid(string language)
    {
        var sprite = MatPlatformValidator.ReadSprite(Repo.Sprite);
        Assert.Empty(MatPlatformValidator.Validate(MatPlatformBuilder.Build(PaperPlatform.Describe, language), sprite));
        Assert.Empty(MatPlatformValidator.Validate(MatPlatformBuilder.Build(o => PaperPlatform.Describe(o, "https://paper.example.com"), language), sprite));
    }

    [Fact]
    public void MatPaper_consumes_only_the_sign_in_and_offers_nothing()
    {
        var definition = MatPlatformBuilder.Build(PaperPlatform.Describe, "en");
        Assert.Equal(MatApiDefinition.ContractVersion, definition.Contract);
        Assert.Equal(2, definition.Contract);
        Assert.Empty(definition.Provides);

        // MatPaper sends no mail (MailKit only reads mailboxes) and uses no other app: the sign-in through MatIdentity is all, and optional.
        var identity = Assert.Single(definition.Consumes);
        Assert.Equal(MatCapabilities.IdentityOidc, identity.Capability);
        Assert.True(identity.Optional);
    }

    [Fact]
    public void One_launcher_with_the_logo_of_the_app_and_quick_actions_to_real_pages()
    {
        var contributes = MatPlatformBuilder.Build(PaperPlatform.Describe, "en").Contributes!;

        var launcher = Assert.Single(contributes.Launchers);
        Assert.Equal(("documents", "MatPaper", "/"), (launcher.Id, launcher.Label, launcher.Url));
        Assert.Null(launcher.Audience);
        Assert.True(File.Exists(Path.Combine(Repo.WebRoot, launcher.Icon.TrimStart('/'))), launcher.Icon);

        // The first views of the menu Documents: each a page of the app (its @page route).
        Assert.Equal(["/Documents/Upload", "/Inbox", "/Documents"], contributes.QuickActions.Select(q => q.Url));
        Assert.All(contributes.QuickActions, q => Assert.Equal("documents", q.Launcher));
        Assert.All(contributes.QuickActions, q => Assert.Null(q.Action));
        var routes = PageRoutes();
        Assert.Contains(launcher.Url, routes);
        Assert.All(contributes.QuickActions, q => Assert.Contains(q.Url!, routes));
    }

    [Fact]
    public void The_widgets_show_only_tiles_of_the_state_and_only_to_administrators()
    {
        var definition = MatPlatformBuilder.Build(PaperPlatform.Describe, "en");
        var widgets = definition.Contributes!.Widgets;
        var tiles = definition.State.Select(s => s.Key).ToHashSet();

        Assert.Equal(["archive", "inbox"], widgets.Select(w => w.Id));
        Assert.All(widgets, w => Assert.Equal(MatAudience.Admin, w.Audience));
        Assert.All(widgets, w => Assert.Contains(w.Kind, new[] { MatWidgetKinds.Stat, MatWidgetKinds.Tiles }));
        Assert.All(widgets, w => Assert.All(w.Items, key => Assert.Contains(key, tiles)));
        Assert.Equal(["documents", "storage", "lastDocument"], widgets[0].Items);
        Assert.Equal(["inbox", "ocrQueue", "ocrFailed"], widgets[1].Items);
        var routes = PageRoutes();
        Assert.All(widgets, w => Assert.Contains(w.Url!, routes));
    }

    [Fact]
    public void MAT_PUBLIC_URL_fixes_the_public_address()
    {
        var free = MatPlatformBuilder.Build(PaperPlatform.Describe, "en").Configure.Single(f => f.Key == "publicUrl");
        Assert.Null(free.Fixed);

        foreach (var language in new[] { "de", "en" })
        {
            var field = MatPlatformBuilder.Build(o => PaperPlatform.Describe(o, "https://paper.example.com"), language).Configure.Single(f => f.Key == "publicUrl");
            Assert.True(field.Fixed);
            Assert.Contains("MAT_PUBLIC_URL", field.Hint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_overview_links_to_the_Connections_area()
    {
        var definition = MatPlatformBuilder.Build(PaperPlatform.Describe, "en");
        var connections = Assert.Single(definition.Actions, a => a.Id == "openConnections");
        Assert.Equal(("link", "/System/AppConnections", "plug"), (connections.Kind, connections.Url, connections.Icon));
        Assert.Contains("/System/AppConnections", PageRoutes());
        Assert.Contains(definition.Pages[0].Sections, s => s.Kind == "actions" && s.Items.Contains("openConnections"));
    }

    /// <summary>The routes of the pages of the app (the <c>@page "..."</c> line of each <c>.cshtml</c> under <c>Pages/</c>).</summary>
    private static HashSet<string> PageRoutes()
    {
        var pages = Path.Combine(Repo.Root, "src", "MatPaper", "Pages");
        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(pages, "*.cshtml", SearchOption.AllDirectories))
        {
            var first = File.ReadLines(file).FirstOrDefault() ?? string.Empty;
            var match = System.Text.RegularExpressions.Regex.Match(first, "^@page \"(/[^\"{]*)\"");
            if (match.Success)
            {
                routes.Add(match.Groups[1].Value);
            }
        }

        return routes;
    }

    [Fact]
    public void The_app_is_the_one_of_the_registry_with_its_icon()
    {
        var definition = MatPlatformBuilder.Build(PaperPlatform.Describe, "de");
        Assert.Equal("matpaper", definition.App.Id);
        Assert.Equal("MatPaper", definition.App.Name);
        Assert.Equal("#268538", definition.App.Accent);
        Assert.Equal("/", definition.App.Home);
        Assert.True(File.Exists(Path.Combine(Repo.WebRoot, definition.App.Icon!.TrimStart('/'))), definition.App.Icon);
    }

    [Fact]
    public void Two_pages_few_settings_no_secret_and_the_roles_of_the_app()
    {
        var definition = MatPlatformBuilder.Build(PaperPlatform.Describe, "de");

        Assert.Equal(["overview", "settings"], definition.Pages.Select(p => p.Id));
        Assert.Equal(["documents", "inbox", "ocrQueue", "ocrFailed", "storage", "lastDocument", "users"], definition.State.Select(s => s.Key));
        Assert.Equal(["publicUrl", "timeZone", "language", "archiveNumbers"], definition.Configure.Select(f => f.Key));
        Assert.InRange(definition.Configure.Count, 1, 10);
        Assert.DoesNotContain(definition.Configure, f => f.Type == "secret");

        // Links only: MatPaper has no function that would be a button of its own (no "re-run failed text recognition").
        Assert.Equal(["open", "openInbox", "openAdmin", "openConnections"], definition.Actions.Select(a => a.Id));
        Assert.All(definition.Actions, a => Assert.Equal("link", a.Kind));
        Assert.Equal(["/", "/Inbox", "/System", "/System/AppConnections"], definition.Actions.Select(a => a.Url));

        // The roles of the table Role, the most powerful first (Anonymous is no role a person is given).
        Assert.Equal(["admin", "user"], definition.Roles!.Select(r => r.Key));
    }

    [Fact]
    public void The_texts_follow_the_language()
    {
        var de = MatPlatformBuilder.Build(PaperPlatform.Describe, "de");
        var en = MatPlatformBuilder.Build(PaperPlatform.Describe, "en");

        Assert.Equal("Übersicht", de.Pages[0].Label);
        Assert.Equal("Overview", en.Pages[0].Label);
        Assert.Equal("Dokumente", de.State[0].Label);
        Assert.Equal("Documents", en.State[0].Label);
        Assert.Equal("Benutzer", de.Roles![1].Label);
        Assert.Equal("User", en.Roles![1].Label);
    }
}
