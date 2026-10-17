using MatPaper.Platform;
using MatPaper.Tests.Support;
using MatPlatform;

namespace MatPaper.Tests;

/// <summary>The definition of MatPaper (no database needed): valid for MatPlatform's validator in both languages against MatPaper's copy of the MatUi sprite.</summary>
public sealed class PlatformDefinitionTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_description_is_valid(string language)
        => Assert.Empty(MatPlatformValidator.Validate(MatPlatformBuilder.Build(PaperPlatform.Describe, language), MatPlatformValidator.ReadSprite(Repo.Sprite)));

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
        Assert.Equal(["open", "openInbox", "openAdmin"], definition.Actions.Select(a => a.Id));
        Assert.All(definition.Actions, a => Assert.Equal("link", a.Kind));
        Assert.Equal(["/", "/Inbox", "/System"], definition.Actions.Select(a => a.Url));

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
