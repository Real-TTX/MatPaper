using System.Globalization;
using MatPaper.Services;
using MatPlatform;

namespace MatPaper.Platform;

/// <summary>
/// MatPaper's side of the sign-in through MatIdentity (MatPlatform <see cref="IMatIdentityUsers"/>): the person is mapped to a local user by
/// <see cref="ExternalSignInService.SignInExternalAsync"/> (by <c>sub</c>, else a new user; the claim <c>role</c> when it is there) and then signed
/// in with MatPaper's own cookie and session, like a local sign-in, keeping the ID token with the session for the sign-out at MatIdentity.
/// An account with the same e-mail address is not entered by the address: the answer is <see cref="MatIdentitySignInResult.LinkRequired"/>, and
/// the sign-in page links it after its password (<see cref="MatIdentityPendingLink"/>, <c>Pages/Account/Login</c>).
/// </summary>
public sealed class PaperIdentityUsers(
    ExternalSignInService externalSignIn,
    SignInService signIn,
    SetupState setupState,
    ILogger<PaperIdentityUsers> logger) : IMatIdentityUsers
{
    public async Task<MatIdentitySignInResult> SignInAsync(MatIdentityPerson person, HttpContext http, CancellationToken ct)
    {
        var identity = new ExternalIdentity(person.Subject, person.Email, person.Name, person.PreferredUserName, person.Role);

        var result = await externalSignIn.SignInExternalAsync(identity, ct);
        if (result.LinkUserId is { } linkUserId)
        {
            return MatIdentitySignInResult.LinkRequired(linkUserId.ToString(CultureInfo.InvariantCulture));
        }

        if (result.User is not { } user)
        {
            return MatIdentitySignInResult.Refused(result.Error ?? "refused");
        }

        await signIn.SignInAsync(user, isPersistent: true, person.IdToken, http);
        setupState.HasUsers = true;
        logger.LogInformation("User {UserId} ({Username}) signed in through MatIdentity", user.Id, user.Username);

        return MatIdentitySignInResult.Success();
    }
}
