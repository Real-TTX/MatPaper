using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MatPaper.Services;

public class SetupRedirectMiddleware(RequestDelegate next, SetupState setupState)
{
    private const string SetupPath = "/Account/Setup";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (IsStaticOrAllowed(path))
        {
            await next(context);
            return;
        }

        if (!setupState.HasUsers)
        {
            var signInService = context.RequestServices.GetRequiredService<SignInService>();
            if (await signInService.AnyUsersExistAsync())
            {
                setupState.HasUsers = true;
            }
        }

        var isSetupPath = path.Equals(SetupPath, StringComparison.OrdinalIgnoreCase);

        if (!setupState.HasUsers)
        {
            if (!isSetupPath)
            {
                context.Response.Redirect(SetupPath);
                return;
            }

            await next(context);
            return;
        }

        if (isSetupPath)
        {
            context.Response.Redirect("/");
            return;
        }

        await next(context);
    }

    private static bool IsStaticOrAllowed(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.StartsWith("/css", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/js", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/lib", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Path.HasExtension(value);
    }
}
