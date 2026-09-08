using MatPaper.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatPaper.Pages.Account;

public class LogoutModel : PageModel
{
    private readonly SignInService _signInService;

    public LogoutModel(SignInService signInService)
    {
        _signInService = signInService;
    }

    public IActionResult OnGet()
    {
        return RedirectToPage("/Account/Login");
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await _signInService.SignOutAsync();
        return RedirectToPage("/Account/Login");
    }
}
