using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace MatPaper.Services;

public class CurrentUser(IHttpContextAccessor http)
{
    private ClaimsPrincipal? Principal => http.HttpContext?.User;

    public long? UserId =>
        long.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public string? Username => Principal?.FindFirstValue(ClaimTypes.Name);

    public string? DisplayName => Principal?.FindFirstValue(AppClaims.DisplayName);

    public string? Role => Principal?.FindFirstValue(ClaimTypes.Role);

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public bool IsAdmin => Role == "Admin";
}
