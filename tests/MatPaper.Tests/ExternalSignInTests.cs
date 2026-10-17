using MatPaper.Data;
using MatPaper.Services;
using MatPaper.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatPaper.Tests;

/// <summary>How a person signed in by MatIdentity becomes a local user (<see cref="ExternalSignInService"/>), on the app's own database.</summary>
public sealed class ExternalSignInTests(PaperFactory site) : IClassFixture<PaperFactory>
{
    [DbFact]
    public async Task A_new_person_becomes_a_user_with_the_role_of_the_claim_and_no_local_password()
    {
        var result = await SignInAsync(new ExternalIdentity("sub-1", "Nora@Example.com", "Nora Neu", "nora", "admin"));

        var user = await UserAsync(result.User!.Id);
        Assert.Equal("sub-1", user.ExternalSubject);
        Assert.Equal("nora", user.Username);
        Assert.Equal("Nora Neu", user.DisplayName);
        Assert.Equal("nora@example.com", user.Email);
        Assert.Equal("Admin", await RoleAsync(user));
        Assert.True(user.IsActive);
        Assert.Equal(string.Empty, user.PasswordHash);
        Assert.Null(await WithScopeAsync(sp => sp.GetRequiredService<SignInService>().ValidateCredentialsAsync("nora", "")));

        // The second time the same user, found by sub; the role follows the claim.
        var again = await SignInAsync(new ExternalIdentity("sub-1", "other@example.com", "Other", "other", "user"));
        Assert.Equal(user.Id, again.User!.Id);
        Assert.Equal("User", await RoleAsync(await UserAsync(user.Id)));
    }

    [DbFact]
    public async Task A_taken_user_name_gets_a_number_and_without_a_role_claim_the_user_is_a_user()
    {
        var first = await SignInAsync(new ExternalIdentity("sub-b1", null, null, "bert", null));
        var second = await SignInAsync(new ExternalIdentity("sub-b2", null, null, "bert", null));

        Assert.Equal("bert", first.User!.Username);
        Assert.Equal("bert-2", second.User!.Username);
        Assert.Equal("User", await RoleAsync(second.User));

        // An unknown role claim leaves the role.
        await SignInAsync(new ExternalIdentity("sub-b2", null, null, "bert", "owner"));
        Assert.Equal("User", await RoleAsync(await UserAsync(second.User.Id)));
    }

    [DbFact]
    public async Task The_last_active_administrator_is_not_demoted()
    {
        // The seeded administrator is linked with a subject and then the only active administrator.
        var admin = await WithDbAsync(db => db.Users.SingleAsync(u => u.Username == PaperFactory.AdminUser));
        var others = await WithDbAsync(db => db.Users.Where(u => u.Id != admin.Id && u.Role!.Name == "Admin").ToListAsync());
        await WithDbAsync(async db =>
        {
            foreach (var other in others)
            {
                (await db.Users.SingleAsync(u => u.Id == other.Id)).IsActive = false;
            }

            (await db.Users.SingleAsync(u => u.Id == admin.Id)).ExternalSubject = "sub-admin";
            return await db.SaveChangesAsync();
        });

        var result = await SignInAsync(new ExternalIdentity("sub-admin", null, "Admin", "admin", "user"));
        Assert.Equal(admin.Id, result.User!.Id);
        Assert.Equal("Admin", await RoleAsync(await UserAsync(admin.Id)));

        await WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == admin.Id)).ExternalSubject = null;
            foreach (var other in others)
            {
                (await db.Users.SingleAsync(u => u.Id == other.Id)).IsActive = true;
            }

            return await db.SaveChangesAsync();
        });
    }

    [DbFact]
    public async Task A_deactivated_user_and_ambiguous_or_foreign_matches_are_refused()
    {
        var deactivated = await SignInAsync(new ExternalIdentity("sub-d", "d@example.com", "D", "d", null));
        await WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == deactivated.User!.Id)).IsActive = false;
            return await db.SaveChangesAsync();
        });
        Assert.NotNull((await SignInAsync(new ExternalIdentity("sub-d", "d@example.com", "D", "d", null))).Error);

        // Two accounts with the address: no guess.
        await CreateLocalAsync("twin1", "twin@example.com");
        await CreateLocalAsync("twin2", "TWIN@example.com");
        var twins = await SignInAsync(new ExternalIdentity("sub-twin", "twin@example.com", "T", "t", null));
        Assert.NotNull(twins.Error);
        Assert.Null(twins.LinkUserId);

        // The account with the address belongs to another person of MatIdentity.
        var linked = await SignInAsync(new ExternalIdentity("sub-owner", "owned@example.com", "O", "o", null));
        Assert.Equal("owned@example.com", linked.User!.Email);
        var foreign = await SignInAsync(new ExternalIdentity("sub-intruder", "owned@example.com", "I", "i", null));
        Assert.NotNull(foreign.Error);
        Assert.False(await WithDbAsync(db => db.Users.AnyAsync(u => u.ExternalSubject == "sub-intruder")));
    }

    [DbFact]
    public async Task A_matching_account_with_a_password_waits_for_its_proof_and_is_linked_only_then()
    {
        var local = await CreateLocalAsync("claire", "claire@example.com");

        var result = await SignInAsync(new ExternalIdentity("sub-claire", "Claire@Example.com", "Claire", "claire", "admin"));
        Assert.Equal(local.Id, result.LinkUserId);
        Assert.Null(result.User);
        Assert.Null((await UserAsync(local.Id)).ExternalSubject);
        Assert.False(await WithDbAsync(db => db.Users.AnyAsync(u => u.ExternalSubject == "sub-claire")));

        var linked = await WithScopeAsync(sp => sp.GetRequiredService<ExternalSignInService>()
            .LinkExternalAsync(local.Id, new ExternalIdentity("sub-claire", "claire@example.com", "Claire", "claire", "admin")));
        Assert.Equal(local.Id, linked.User!.Id);
        Assert.Equal("sub-claire", (await UserAsync(local.Id)).ExternalSubject);
        Assert.Equal("Admin", await RoleAsync(await UserAsync(local.Id)));

        // A second link of the same subject or account is refused.
        var other = await CreateLocalAsync("claire2", null);
        Assert.NotNull((await WithScopeAsync(sp => sp.GetRequiredService<ExternalSignInService>()
            .LinkExternalAsync(other.Id, new ExternalIdentity("sub-claire", null, null, null, null)))).Error);
    }

    private Task<ExternalSignInResult> SignInAsync(ExternalIdentity identity)
        => WithScopeAsync(sp => sp.GetRequiredService<ExternalSignInService>().SignInExternalAsync(identity));

    private Task<User> CreateLocalAsync(string username, string? email)
        => WithScopeAsync(sp => sp.GetRequiredService<SignInService>().CreateUserAsync(username, username, email, "local-password", "User", isActive: true, actingUserId: null));

    private Task<User> UserAsync(long id) => WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<string> RoleAsync(User user) => WithDbAsync(db => db.Roles.Where(r => r.Id == user.RoleId).Select(r => r.Name).SingleAsync());

    private Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> work) => WithScopeAsync(sp => work(sp.GetRequiredService<AppDbContext>()));

    private async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = site.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }
}
