using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MatPaper.Services;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages;

/// <summary>
/// The page behind UseStatusCodePagesWithReExecute and UseExceptionHandler: turns a bare
/// 404/500 into something readable and keeps the visitor's way back. Anonymous on purpose —
/// an error must render even when the session is gone or the database is down.
/// </summary>
[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    private readonly IStringLocalizer<SharedResource> _l;
    private readonly ILogger<ErrorModel> _logger;

    public ErrorModel(IStringLocalizer<SharedResource> l, ILogger<ErrorModel> logger)
    {
        _l = l;
        _logger = logger;
    }

    public int StatusCode { get; private set; } = 500;

    public string Title { get; private set; } = string.Empty;

    public string Message { get; private set; } = string.Empty;

    public void OnGet(int? code) => Build(code);

    public void OnPost(int? code) => Build(code);

    private void Build(int? code)
    {
        StatusCode = code is >= 400 and < 600 ? code.Value : 500;

        // An unhandled exception reaches this page through the exception handler; log it
        // here so a 500 shown to the user always has a matching entry in the log.
        var exception = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        if (exception?.Error is not null)
        {
            StatusCode = 500;
            _logger.LogError(exception.Error, "Unhandled exception for {Path}", exception.Path);
        }

        (Title, Message) = StatusCode switch
        {
            404 => (_l["Page not found"].Value, _l["The page you asked for does not exist (any more)."].Value),
            403 => (_l["No access"].Value, _l["Your account may not open this page."].Value),
            401 => (_l["Not signed in"].Value, _l["Please sign in again."].Value),
            _ => (_l["Something went wrong"].Value, _l["The action could not be completed. The details are in the server log."].Value)
        };

        Response.StatusCode = StatusCode;
    }
}
